using System.Security.Claims;

namespace AuthService.Authorization;

/// <summary>The claims of an Auth Service product token, and how to read them. Nothing is ever read from the request instead.</summary>
public static class AuthServiceClaims
{
    /// <summary>The person the token is for.</summary>
    public const string Subject = "sub";

    /// <summary>The one tenant the token is for.</summary>
    public const string TenantId = "tid";

    /// <summary>The permissions held, one claim each, as <c>key:Level</c>.</summary>
    public const string Permissions = "permissions";

    /// <summary>The tenant the token is for, or null if the token has none. It comes only from the verified token.</summary>
    public static Guid? GetTenantId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(TenantId), out var tenantId) && tenantId != Guid.Empty ? tenantId : null;

    /// <summary>
    /// The tenant of the request. Use this, and nothing from the body, path, query string or headers, to decide whose data to touch.
    /// Throws if there is none, which an endpoint protected by this package's policies cannot reach.
    /// </summary>
    public static Guid RequireTenantId(this ClaimsPrincipal principal) =>
        principal.GetTenantId() ?? throw new InvalidOperationException("The request has no verified tenant. Protect the endpoint with RequireTenant or RequirePermission.");

    /// <summary>The person the token is for, or null.</summary>
    public static Guid? GetUserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(Subject), out var userId) ? userId : null;

    /// <summary>The level the token grants for a permission: the highest if it is listed more than once, otherwise <see cref="AccessLevel.None"/>.</summary>
    public static AccessLevel GetLevel(this ClaimsPrincipal principal, string permission)
    {
        var best = AccessLevel.None;
        foreach (var claim in principal.FindAll(Permissions))
        {
            if (TryParse(claim.Value, out var key, out var level) && string.Equals(key, permission, StringComparison.Ordinal) && level > best)
            {
                best = level;
            }
        }

        return best;
    }

    /// <summary>True if the token grants the permission at <paramref name="minimum"/> or higher. For decisions inside a handler.</summary>
    public static bool HasPermission(this ClaimsPrincipal principal, string permission, AccessLevel minimum) =>
        minimum > AccessLevel.None ? principal.GetLevel(permission) >= minimum : true;

    internal static bool TryParse(string claimValue, out string key, out AccessLevel level)
    {
        key = string.Empty;
        level = AccessLevel.None;

        var separator = claimValue.LastIndexOf(':');
        if (separator <= 0 || separator == claimValue.Length - 1)
        {
            return false;
        }

        // Level names only, exactly as issued: a number or another casing is not a level this package will trust.
        var name = claimValue[(separator + 1)..];
        if (!Enum.TryParse(name, ignoreCase: false, out level) || !Enum.IsDefined(level) || name.All(char.IsDigit))
        {
            return false;
        }

        key = claimValue[..separator];
        return true;
    }
}
