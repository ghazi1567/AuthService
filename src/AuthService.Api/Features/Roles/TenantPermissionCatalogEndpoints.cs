using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Roles;

/// <summary>One permission a role can grant.</summary>
public sealed record CatalogPermission(string Key, string Label, string? Description);

/// <summary>A product whose permissions the tenant's roles can grant, with its whole catalog.</summary>
public sealed record CatalogApplication(Guid ApplicationId, string Key, string Name, bool IsBuiltIn, IReadOnlyList<CatalogPermission> Permissions);

/// <summary>The access levels a grant can carry (<c>None</c> gives nothing and is left out), lowest first, and what can be granted.</summary>
public sealed record TenantPermissionCatalog(IReadOnlyList<string> Levels, IReadOnlyList<CatalogApplication> Applications);

/// <summary>
/// Lets a tenant admin building a role see what there is to grant: the permissions of every product the tenant uses (an Active
/// subscription) and of the Auth Service itself, which every tenant has. It is exactly the set a role grant is accepted for.
/// </summary>
public static class TenantPermissionCatalogEndpoints
{
    public const string Route = "/api/auth/tenants/me/permissions";

    public static IEndpointRouteBuilder MapTenantPermissionCatalog(this IEndpointRouteBuilder app)
    {
        // Seeing what could be granted goes with seeing the roles that grant it.
        app.MapGet(Route, GetAsync).RequireTenantPermission(PermissionKeys.RolesRead, AccessLevel.ReadOnly);
        return app;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal caller, AuthDbContext db, CancellationToken cancellationToken)
    {
        // The tenant is the token's `tid` and nothing else.
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");

        var subscribed = db.Subscriptions
            .Where(subscription => subscription.TenantId == tenantId && subscription.Status == SubscriptionStatus.Active)
            .Select(subscription => subscription.ApplicationId);

        var applications = await db.Applications.AsNoTracking()
            .Where(application => application.Id == BuiltInApplication.Id || subscribed.Contains(application.Id))
            .Select(application => new { application.Id, application.Key, application.Name, application.IsBuiltIn })
            .ToListAsync(cancellationToken);
        var applicationIds = applications.Select(application => application.Id).ToList();
        var permissions = await db.Permissions.AsNoTracking()
            .Where(permission => applicationIds.Contains(permission.ApplicationId))
            .Select(permission => new { permission.ApplicationId, permission.Key, permission.Label, permission.Description })
            .ToListAsync(cancellationToken);

        // The Auth Service's own product first (it is the tenant's own administration), then the others by name.
        var catalog = new TenantPermissionCatalog(
            [nameof(AccessLevel.ReadOnly), nameof(AccessLevel.CreateRead), nameof(AccessLevel.Full)],
            [.. applications
                .OrderByDescending(application => application.IsBuiltIn).ThenBy(application => application.Name, StringComparer.OrdinalIgnoreCase)
                .Select(application => new CatalogApplication(
                    application.Id,
                    application.Key,
                    application.Name,
                    application.IsBuiltIn,
                    [.. permissions
                        .Where(permission => permission.ApplicationId == application.Id)
                        .OrderBy(permission => permission.Key, StringComparer.Ordinal)
                        .Select(permission => new CatalogPermission(permission.Key, permission.Label, permission.Description))]))]);

        return Results.Ok(catalog);
    }
}
