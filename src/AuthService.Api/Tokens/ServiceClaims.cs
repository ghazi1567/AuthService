using System.Security.Claims;

namespace AuthService.Api.Tokens;

/// <summary>The claims that mark a product's service token (APP-05).</summary>
public static class ServiceClaims
{
    public const string TokenUse = "token_use";
    public const string ServiceTokenUse = "service";
    public const string ApplicationId = "app_id";
    public const string ClientId = "client_id";

    /// <summary>The product a service token belongs to, from the verified token only, never from the request.</summary>
    public static Guid? GetApplicationId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(ApplicationId), out var applicationId) ? applicationId : null;
}
