using System.Security.Claims;

namespace AuthService.Api.Common;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The caller's user id, taken only from the verified token's <c>sub</c> claim, never from the request.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out var userId) ? userId : null;
}
