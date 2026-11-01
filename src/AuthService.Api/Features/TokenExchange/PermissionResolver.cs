using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.TokenExchange;

/// <summary>
/// Works out what a member of a tenant may do in one product: the one place that rule lives, used when a token is issued (TOK-01)
/// and as the ceiling for what an API key may be given (KEY-01).
/// </summary>
public sealed class PermissionResolver(AuthDbContext db)
{
    /// <summary>
    /// The highest level the member holds for each permission of this one product. The built-in Owner role holds Full on the
    /// product's whole catalog as it is right now, with no grants stored, so a permission added later is covered at once.
    /// Grants from custom roles (ROL-01) are merged in, highest level per permission; there are no deny rules.
    /// </summary>
    public async Task<Dictionary<string, AccessLevel>> ResolveAsync(
        Guid tenantId, Guid membershipId, Guid applicationId, CancellationToken cancellationToken)
    {
        var effective = new Dictionary<string, AccessLevel>(StringComparer.Ordinal);

        var isOwner = await (
            from assignment in db.MembershipRoles
            join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
            where assignment.TenantId == tenantId && assignment.MembershipId == membershipId
                && role.IsBuiltIn && role.Name == Role.OwnerName
            select assignment.Id)
            .AnyAsync(cancellationToken);

        if (isOwner)
        {
            var catalog = await db.Permissions.AsNoTracking()
                .Where(permission => permission.ApplicationId == applicationId)
                .Select(permission => permission.Key)
                .ToListAsync(cancellationToken);
            foreach (var key in catalog)
            {
                effective[key] = AccessLevel.Full;
            }
        }

        // Grants from every role the member holds, for this product only. Where several roles grant the same permission, the
        // highest level wins; there are no deny rules, so another role can never take access away.
        var grants = await (
            from assignment in db.MembershipRoles
            join grant in db.RolePermissions on new { assignment.TenantId, assignment.RoleId } equals new { grant.TenantId, grant.RoleId }
            join permission in db.Permissions on grant.PermissionId equals permission.Id
            where assignment.TenantId == tenantId && assignment.MembershipId == membershipId && permission.ApplicationId == applicationId
            select new { permission.Key, grant.Level })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        foreach (var grant in grants)
        {
            if (!effective.TryGetValue(grant.Key, out var current) || grant.Level > current)
            {
                effective[grant.Key] = grant.Level;
            }
        }

        return effective;
    }
}
