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
        services.TryAddSingleton(provider => new JwksDocumentRetriever(
            provider.GetRequiredService<IHttpClientFactory>(), AuthServiceAuthorizationOptions.JwksHttpClientName));
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
                BearerSetup.Apply(bearer, keys, time, options.ClockSkewSeconds, parameters =>
                {
                    // The issuer and the audience are typed in by the product, so each is compared with that one value.
                    parameters.ValidIssuer = options.Issuer;
                    parameters.ValidAudience = options.Audience;
                });
            });

        BearerSetup.AddEnforcement(services);

        return services;
    }
}
