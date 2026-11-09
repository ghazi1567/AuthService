using AuthService.Api.Email;
using AuthService.Api.Features.Jwks;
using AuthService.Api.Features.Applications;
using AuthService.Api.Tokens;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Discovery;

/// <summary>Settings for the discovery document (ONB-03).</summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Auth:Discovery";

    /// <summary>
    /// The address products and people reach this API at, such as <c>https://auth.example.com</c>. Set it when the API sits behind a proxy
    /// or is called by its internal name, so the document always carries the public address. Left empty, the address of the request is used.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>What a product needs to know about this Auth Service, all of it public. Nothing here is a secret.</summary>
/// <param name="Issuer">The <c>iss</c> of every token.</param>
/// <param name="ApiBaseUrl">The address of this API.</param>
/// <param name="JwksUrl">Where the public keys that check tokens are published.</param>
/// <param name="TokenUrl">Where a product trades its client secret for a service token.</param>
/// <param name="ConsoleUrl">The address of the web console, where people sign in and products are opened from, when it is configured.</param>
public sealed record DiscoveryDocument(string Issuer, string ApiBaseUrl, string JwksUrl, string TokenUrl, string? ConsoleUrl);

/// <summary>
/// ONB-03: one address a product can start from. With the Auth Service's address and its own client secret, a product learns the rest
/// (the issuer, where the keys are, where to ask for a token) instead of being told each value by hand.
/// </summary>
public static class DiscoveryEndpoints
{
    public const string Route = "/.well-known/auth-service-configuration";

    public static IEndpointRouteBuilder MapDiscovery(this IEndpointRouteBuilder app)
    {
        // No authentication, by design: it holds only public addresses and the issuer name, and a product needs it before it has a token.
        app.MapGet(Route, Get).AllowAnonymous();
        return app;
    }

    private static IResult Get(
        HttpContext httpContext,
        IOptions<IdentityTokenOptions> tokenOptions,
        IOptions<DiscoveryOptions> discoveryOptions,
        IOptions<EmailOptions> emailOptions)
    {
        httpContext.Response.Headers.CacheControl = "public, max-age=300";

        var configured = discoveryOptions.Value.PublicBaseUrl?.Trim().TrimEnd('/');
        var apiBaseUrl = Uri.TryCreate(configured, UriKind.Absolute, out var configuredUri) && configuredUri.Scheme is "http" or "https"
            ? configuredUri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : $"{httpContext.Request.Scheme}://{httpContext.Request.Host}{httpContext.Request.PathBase}";

        var consoleUrl = emailOptions.Value.PublicBaseUrl?.Trim().TrimEnd('/');
        return Results.Json(new DiscoveryDocument(
            tokenOptions.Value.Issuer,
            apiBaseUrl,
            apiBaseUrl + JwksEndpoints.Route,
            apiBaseUrl + ServiceTokenEndpoints.Route,
            string.IsNullOrEmpty(consoleUrl) ? null : consoleUrl));
    }
}
