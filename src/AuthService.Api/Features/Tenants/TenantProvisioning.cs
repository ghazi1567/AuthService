using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Tenants;

public sealed record ProvisionedTenant(Tenant Tenant, TenantMembership Membership);

public static class TenantProvisioning
{
    /// <summary>
    /// Adds to the context a new Active tenant, its built-in Owner role, and an Active membership that gives
    /// <paramref name="userId"/> that role. Nothing is saved: the caller's own <c>SaveChangesAsync</c> commits it all in
    /// one transaction, so a failure part-way leaves no tenant, role or membership behind. Used by signup (TEN-01) and by
    /// creating an additional tenant (TEN-09), so the two can never create tenants differently.
    /// </summary>
    public static ProvisionedTenant AddTenantWithOwner(this AuthDbContext db, Guid userId, string tenantName, DateTimeOffset now)
    {
        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = tenantName,
            Status = TenantStatus.Active,
            CreatedAt = now,
        };

        var membership = new TenantMembership
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            UserId = userId,
            Status = MembershipStatus.Active,
            CreatedAt = now,
        };

        var ownerRole = new Role
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            Name = Role.OwnerName,
            IsBuiltIn = true,
            CreatedAt = now,
        };

        var ownerAssignment = new MembershipRole
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.Id,
            MembershipId = membership.Id,
            RoleId = ownerRole.Id,
            AssignedAt = now,
        };

        db.AddRange(tenant, membership, ownerRole, ownerAssignment);

        return new ProvisionedTenant(tenant, membership);
    }
}
