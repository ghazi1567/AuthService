using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Roles;

/// <summary>Body of <c>PUT /api/auth/tenants/me/members/{membershipId}/roles</c>: the member's complete set of roles.</summary>
public sealed record AssignRolesRequest(IReadOnlyList<Guid>? RoleIds);

public sealed record AssignedRoleDto(Guid Id, string Name, bool IsBuiltIn);

public sealed record AssignRolesResponse(Guid MembershipId, IReadOnlyList<AssignedRoleDto> Roles);

public abstract record AssignRolesResult
{
    public sealed record Assigned(AssignRolesResponse Response) : AssignRolesResult;

    /// <summary>The membership, or one of the roles, is not in the caller's tenant (or does not exist). Never a 403, so ids are not confirmed.</summary>
    public sealed record NotFound : AssignRolesResult;

    /// <summary>A member must keep at least one role. To remove all access, disable the membership instead (TEN-07).</summary>
    public sealed record EmptySet : AssignRolesResult;

    /// <summary>Only an Owner can give the Owner role or take it away.</summary>
    public sealed record OwnerRoleRestricted : AssignRolesResult;

    /// <summary>The change would leave the tenant without an active Owner.</summary>
    public sealed record WouldLeaveNoOwner : AssignRolesResult;
}

public static class AssignRolesValidator
{
    public const int MaxRolesPerMember = 10;

    /// <summary>The distinct ids, or false with an error when the request cannot be read or names too many roles.</summary>
    public static bool TryValidate(AssignRolesRequest request, out List<Guid> roleIds, out Dictionary<string, string[]> errors)
    {
        errors = [];
        roleIds = [];

        if (request.RoleIds is null)
        {
            errors[nameof(AssignRolesRequest.RoleIds)] = ["RoleIds is required: the member's complete set of roles."];
            return false;
        }

        if (request.RoleIds.Any(id => id == Guid.Empty))
        {
            errors[nameof(AssignRolesRequest.RoleIds)] = ["RoleIds must be valid role ids."];
            return false;
        }

        // The same role named twice is one role.
        roleIds = [.. request.RoleIds.Distinct()];
        if (roleIds.Count > MaxRolesPerMember)
        {
            errors[nameof(AssignRolesRequest.RoleIds)] = [$"A member can hold at most {MaxRolesPerMember} roles."];
            return false;
        }

        return true;
    }
}

/// <summary>
/// ROL-05: replaces the set of roles a member holds in the caller's tenant, all at once. It never touches seats: product access is
/// separate (SUB-05). The new set applies from the member's next token exchange.
/// </summary>
public sealed class AssignRolesService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<AssignRolesResult> AssignAsync(
        Guid callerUserId, Guid tenantId, Guid membershipId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Role changes in one tenant take turns. This is what keeps "the tenant always has an Owner" true when two admins act at once.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {tenantId} FOR UPDATE", cancellationToken);

        var membershipExists = await db.TenantMemberships.AnyAsync(
            membership => membership.Id == membershipId && membership.TenantId == tenantId, cancellationToken);
        if (!membershipExists)
        {
            return new AssignRolesResult.NotFound();
        }

        var roles = await db.Roles.AsNoTracking()
            .Where(role => role.TenantId == tenantId && roleIds.Contains(role.Id))
            .Select(role => new AssignedRoleDto(role.Id, role.Name, role.IsBuiltIn))
            .ToListAsync(cancellationToken);
        if (roles.Count != roleIds.Count)
        {
            return new AssignRolesResult.NotFound();
        }

        if (roles.Count == 0)
        {
            return new AssignRolesResult.EmptySet();
        }

        var ownerRoleId = await db.Roles
            .Where(role => role.TenantId == tenantId && role.IsBuiltIn && role.Name == Role.OwnerName)
            .Select(role => role.Id)
            .SingleAsync(cancellationToken);

        var current = await db.MembershipRoles.Where(assignment => assignment.MembershipId == membershipId).ToListAsync(cancellationToken);
        var hadOwner = current.Any(assignment => assignment.RoleId == ownerRoleId);
        var wantsOwner = roleIds.Contains(ownerRoleId);

        if (hadOwner != wantsOwner)
        {
            // Giving or taking the Owner role is reserved to Owners, or anyone allowed to assign roles could make themselves one.
            var callerIsOwner = await (
                from assignment in db.MembershipRoles
                join membership in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
                where assignment.TenantId == tenantId && assignment.RoleId == ownerRoleId
                    && membership.UserId == callerUserId && membership.Status == MembershipStatus.Active
                select assignment.Id)
                .AnyAsync(cancellationToken);
            if (!callerIsOwner)
            {
                return new AssignRolesResult.OwnerRoleRestricted();
            }

            if (hadOwner)
            {
                var otherActiveOwners = await (
                    from assignment in db.MembershipRoles
                    join membership in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
                    where assignment.TenantId == tenantId && assignment.RoleId == ownerRoleId
                        && assignment.MembershipId != membershipId && membership.Status == MembershipStatus.Active
                    select assignment.Id)
                    .AnyAsync(cancellationToken);
                if (!otherActiveOwners)
                {
                    return new AssignRolesResult.WouldLeaveNoOwner();
                }
            }
        }

        var now = timeProvider.GetUtcNow();
        foreach (var assignment in current.Where(assignment => !roleIds.Contains(assignment.RoleId)))
        {
            db.MembershipRoles.Remove(assignment);
        }

        var held = current.Select(assignment => assignment.RoleId).ToHashSet();
        foreach (var roleId in roleIds.Where(roleId => !held.Contains(roleId)))
        {
            db.MembershipRoles.Add(new MembershipRole
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membershipId,
                RoleId = roleId,
                AssignedAt = now,
            });
        }

        audit.Record(AuditActions.MemberRolesChanged, AuditActor.User(callerUserId), tenantId, membershipId.ToString());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AssignRolesResult.Assigned(new AssignRolesResponse(membershipId, [.. roles.OrderBy(role => role.Name, StringComparer.OrdinalIgnoreCase)]));
    }
}
