using Microsoft.AspNetCore.Authorization;

namespace AuthService.Authorization;

/// <summary>The token must name a tenant (<c>tid</c>) and a person (<c>sub</c>). Every protected endpoint carries this one.</summary>
public sealed class TenantRequirement : IAuthorizationRequirement;

/// <summary>The token must grant a permission at least at a given level. Never a role name: roles are the Auth Service's business.</summary>
public sealed class PermissionRequirement(string permission, AccessLevel minimumLevel) : IAuthorizationRequirement
{
    /// <summary>The permission key, such as <c>campaign.create</c>.</summary>
    public string Permission { get; } = permission;

    /// <summary>The lowest level that is enough.</summary>
    public AccessLevel MinimumLevel { get; } = minimumLevel;
}

internal sealed class TenantHandler : AuthorizationHandler<TenantRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRequirement requirement)
    {
        if (context.User.GetTenantId() is not null && context.User.GetUserId() is not null)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasPermission(requirement.Permission, requirement.MinimumLevel) && requirement.MinimumLevel > AccessLevel.None)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
