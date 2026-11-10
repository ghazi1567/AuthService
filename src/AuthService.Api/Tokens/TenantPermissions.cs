using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;

namespace AuthService.Api.Tokens;

/// <summary>The caller's token must grant a permission of the Auth Service's own product at least at a given level.</summary>
public sealed class PermissionRequirement(string permissionKey, AccessLevel minimumLevel) : IAuthorizationRequirement
{
    public string PermissionKey { get; } = permissionKey;

    public AccessLevel MinimumLevel { get; } = minimumLevel;
}

/// <summary>
/// Reads the <c>permissions</c> claims (<c>key:Level</c>) of a tenant-admin token. It never looks at role names: a permission is
/// held or it is not, at a level that is high enough or it is not.
/// </summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        foreach (var claim in context.User.FindAll(ProductClaims.Permissions))
        {
            var separator = claim.Value.LastIndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(claim.Value[..separator], requirement.PermissionKey, StringComparison.Ordinal)
                && Enum.TryParse<AccessLevel>(claim.Value[(separator + 1)..], ignoreCase: false, out var level)
                && Enum.IsDefined(level)
                && level >= requirement.MinimumLevel)
            {
                context.Succeed(requirement);
                return Task.CompletedTask;
            }
        }

        return Task.CompletedTask;
    }
}

public static class TenantAuthorization
{
    /// <summary>
    /// A token from token exchange (TOK-01) for the Auth Service's own product: its audience is the built-in one and it names the
    /// person and exactly one tenant. The tenant of such a request is always <c>tid</c>, never anything the client sends.
    /// </summary>
    public static AuthorizationPolicyBuilder TenantContext(this AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireClaim("aud", BuiltInApplication.Audience)
        .RequireAssertion(context => context.User.GetTenantId() is not null && context.User.FindFirstValue("sub") is not null)

        // And the organisation must not be suspended, looked up now rather than read from a token issued before (AUTH-268).
        .AddRequirements(new ActiveTenantRequirement());

    /// <summary>Like <see cref="TenantContext"/>, and the token must also hold a permission at least at the given level.</summary>
    public static AuthorizationPolicyBuilder TenantPermission(
        this AuthorizationPolicyBuilder policy, string permissionKey, AccessLevel minimumLevel) =>
        policy.TenantContext().AddRequirements(new PermissionRequirement(permissionKey, minimumLevel));

    /// <summary>Protects an endpoint with a tenant-admin token that holds a permission at least at the given level.</summary>
    public static TBuilder RequireTenantPermission<TBuilder>(this TBuilder builder, string permissionKey, AccessLevel minimumLevel)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.TenantPermission(permissionKey, minimumLevel));

    /// <summary>Protects an endpoint with any valid tenant-admin token, whatever permissions it holds.</summary>
    public static TBuilder RequireTenantContext<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(policy => policy.TenantContext());
}
