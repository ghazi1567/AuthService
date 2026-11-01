using System.Net.Http.Headers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Authorization;

/// <summary>Installs Auth Service token enforcement in a product API.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Makes the API accept Auth Service product tokens: signature checked locally against the published keys, issuer and audience
    /// required (the audience must be this product's own), lifetime enforced, only ES256 accepted, and a clean 401 or 403 for
    /// anything else. Endpoints then say what they need with <c>RequirePermission</c> or <c>RequireTenant</c>. Call
    /// <c>UseAuthentication</c> and <c>UseAuthorization</c> after it.
    /// </summary>
    public static IServiceCollection AddAuthServiceAuthorization(this IServiceCollection services, Action<AuthServiceAuthorizationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<AuthServiceAuthorizationOptions>()
            .Configure(configure)
            .Validate(options => !string.IsNullOrWhiteSpace(options.Audience), "AuthServiceAuthorizationOptions.Audience is required: the product's own audience.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.Issuer), "AuthServiceAuthorizationOptions.Issuer is required.")
            .Validate(options => options.JwksUrl is { IsAbsoluteUri: true }, "AuthServiceAuthorizationOptions.JwksUrl is required and must be an absolute URL.")
            .Validate(
                options => options.JwksUrl is null || options.JwksUrl.Scheme == Uri.UriSchemeHttps || options.AllowInsecureJwksUrl,
                "The JWKS URL must be https. Set AllowInsecureJwksUrl only for local development.")
            .ValidateOnStart();

        services.AddHttpClient(AuthServiceAuthorizationOptions.JwksHttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<JwksDocumentRetriever>();
        services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<AuthServiceAuthorizationOptions>>().Value;
            return new JwksConfigurationManager(
                options.JwksUrl!, provider.GetRequiredService<JwksDocumentRetriever>(), options.KeyCacheMinutes, options.MinimumRefreshSeconds);
        });

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AuthServiceAuthorizationOptions>, JwksConfigurationManager, TimeProvider>((bearer, settings, keys, time) =>
            {
                var options = settings.Value;

                // Keep claim names as issued ("sub", "tid", "permissions"), not renamed to long Microsoft claim types.
                bearer.MapInboundClaims = false;
                bearer.ConfigurationManager = keys;
                bearer.RefreshOnIssuerKeyNotFound = true;
                bearer.SaveToken = false;

                bearer.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuerSigningKey = true,

                    // Only the algorithm the Auth Service signs with, so a token cannot pick a weaker one, or none.
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],

                    ValidateIssuer = true,
                    ValidIssuer = options.Issuer,

                    // The audience is the product's own, and a token must have one. This is what stops a token for one product
                    // being replayed against another, and an identity or service token (different or no audience) being used here.
                    ValidateAudience = true,
                    ValidAudience = options.Audience,
                    RequireAudience = true,

                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),

                    // Judged on the product's own TimeProvider if it registers one (so a test can move the clock), otherwise the system clock.
                    LifetimeValidator = (notBefore, expires, _, parameters) =>
                    {
                        var now = time.GetUtcNow().UtcDateTime;
                        return expires is { } expiry
                            && expiry >= now - parameters.ClockSkew
                            && (notBefore is not { } start || start <= now + parameters.ClockSkew);
                    },
                    NameClaimType = AuthServiceClaims.Subject,
                };

                bearer.Events = new JwtBearerEvents
                {
                    // A clean answer for any bad token: no exception text, no stack trace, nothing about why.
                    OnChallenge = async context =>
                    {
                        context.HandleResponse();
                        context.Response.Headers.WWWAuthenticate = JwtBearerDefaults.AuthenticationScheme;
                        await WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, "unauthorized", "Unauthorized", "A valid access token is required.");
                    },
                    OnForbidden = context => WriteProblemAsync(
                        context.HttpContext, StatusCodes.Status403Forbidden, "forbidden", "Forbidden", "The access token does not allow this."),
                };
            });

        services.AddAuthorization();
        services.AddSingleton<IAuthorizationHandler, TenantHandler>();
        services.AddSingleton<IAuthorizationHandler, PermissionHandler>();

        return services;
    }

    private static Task WriteProblemAsync(HttpContext httpContext, int status, string code, string title, string detail)
    {
        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(new { status, title, detail, code }, options: null, contentType: "application/problem+json");
    }
}
