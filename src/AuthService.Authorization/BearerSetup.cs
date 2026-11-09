using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Authorization;

/// <summary>
/// What every way of installing the package has in common: how a token is judged and what a bad one is answered with. The two ways
/// differ only in where the issuer, the audience and the keys address come from (typed in, or learned from the Auth Service), so the
/// checks that matter are written once and a product gets the same enforcement either way.
/// </summary>
internal static class BearerSetup
{
    /// <param name="bearer">The JWT bearer options to fill in.</param>
    /// <param name="keys">Where the signing keys come from.</param>
    /// <param name="time">The clock a token's lifetime is judged on.</param>
    /// <param name="clockSkewSeconds">How much clock difference between machines is forgiven.</param>
    /// <param name="issuerAndAudience">Sets how the issuer and the audience are judged, on the validation parameters built here.</param>
    public static void Apply(
        JwtBearerOptions bearer,
        IConfigurationManager<OpenIdConnectConfiguration> keys,
        TimeProvider time,
        int clockSkewSeconds,
        Action<TokenValidationParameters> issuerAndAudience)
    {
        // Keep claim names as issued ("sub", "tid", "permissions"), not renamed to long Microsoft claim types.
        bearer.MapInboundClaims = false;
        bearer.ConfigurationManager = keys;
        bearer.RefreshOnIssuerKeyNotFound = true;
        bearer.SaveToken = false;

        var parameters = new TokenValidationParameters
        {
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,

            // Only the algorithm the Auth Service signs with, so a token cannot pick a weaker one, or none.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],

            // The issuer and audience are always required; how each is judged is up to the caller. The audience is the product's own, and
            // a token must have one: this is what stops a token for one product being replayed against another, and an identity or
            // service token (different or no audience) being used here.
            ValidateIssuer = true,
            ValidateAudience = true,
            RequireAudience = true,

            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(clockSkewSeconds),

            // Judged on the product's own TimeProvider if it registers one (so a test can move the clock), otherwise the system clock.
            LifetimeValidator = (notBefore, expires, _, validation) =>
            {
                var now = time.GetUtcNow().UtcDateTime;
                return expires is { } expiry
                    && expiry >= now - validation.ClockSkew
                    && (notBefore is not { } start || start <= now + validation.ClockSkew);
            },
            NameClaimType = AuthServiceClaims.Subject,
        };
        issuerAndAudience(parameters);
        bearer.TokenValidationParameters = parameters;

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
    }

    /// <summary>The authorization policies behind <c>RequirePermission</c> and <c>RequireTenant</c>.</summary>
    public static void AddEnforcement(IServiceCollection services)
    {
        services.AddAuthorization();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, TenantHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuthorizationHandler, PermissionHandler>());
    }

    private static Task WriteProblemAsync(HttpContext httpContext, int status, string code, string title, string detail)
    {
        httpContext.Response.StatusCode = status;
        return httpContext.Response.WriteAsJsonAsync(new { status, title, detail, code }, options: null, contentType: "application/problem+json");
    }
}
