using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;

namespace AuthService.Authorization;

/// <summary>How an endpoint of a product API says what it needs from the caller's token.</summary>
public static class EndpointExtensions
{
    /// <summary>
    /// Requires a valid token for this product that grants <paramref name="permission"/> at <paramref name="minimumLevel"/> or higher.
    /// Every endpoint should say this or <see cref="RequireTenant{TBuilder}"/>; <see cref="EndpointPolicyAudit"/> finds the ones that do not.
    /// </summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string permission, AccessLevel minimumLevel)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (minimumLevel <= AccessLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLevel), "A permission requirement needs a level above None.");
        }

        return builder.RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRequirement(), new PermissionRequirement(permission, minimumLevel)));
    }

    /// <summary>
    /// Requires a valid token for this product, whatever permissions it holds. For endpoints that need no permission beyond belonging
    /// to the tenant. The tenant is then <c>User.RequireTenantId()</c>.
    /// </summary>
    public static TBuilder RequireTenant<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy
            .AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRequirement()));
}
