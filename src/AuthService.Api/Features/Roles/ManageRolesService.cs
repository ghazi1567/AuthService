using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Api.Features.Roles;

/// <summary>One role in the caller's tenant. The built-in Owner lists no permissions: it holds Full on every product's whole catalog.</summary>
public sealed record RoleListItem(
    Guid Id, string Name, bool IsBuiltIn, int MemberCount, IReadOnlyList<RoleGrantDto> Permissions, DateTimeOffset CreatedAt);

/// <summary>Body of <c>PATCH /api/auth/roles/{id}</c>. A field left out is left as it is; <c>permissions</c> replaces the whole set.</summary>
public sealed record UpdateRoleRequest(string? Name, IReadOnlyList<RoleGrantInput>? Permissions);

public sealed record ValidRoleUpdate(string? Name, IReadOnlyList<ValidRoleGrant>? Grants);

public abstract record UpdateRoleResult
{
    public sealed record Updated(RoleResponse Role) : UpdateRoleResult;

    public sealed record NotFound : UpdateRoleResult;

    public sealed record OwnerRoleImmutable : UpdateRoleResult;

    public sealed record NameTaken : UpdateRoleResult;

    public sealed record InvalidGrants(Dictionary<string, string[]> Errors) : UpdateRoleResult;
}

public abstract record DeleteRoleResult
{
    public sealed record Deleted : DeleteRoleResult;

    public sealed record NotFound : DeleteRoleResult;

    public sealed record OwnerRoleImmutable : DeleteRoleResult;

    /// <summary>At least one member still holds the role.</summary>
    public sealed record InUse : DeleteRoleResult;
}

public static class UpdateRoleValidator
{
    public static bool TryValidate(UpdateRoleRequest request, out ValidRoleUpdate? valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        if (request.Name is null && request.Permissions is null)
        {
            errors["Name"] = ["Send a new Name, new Permissions, or both."];
            return false;
        }

        var name = request.Name is null ? null : RoleValidator.ValidateName(request.Name, errors);
        var grants = request.Permissions is null ? null : RoleValidator.ValidateGrants(request.Permissions, errors);
        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidRoleUpdate(name, grants);
        return true;
    }
}

/// <summary>ROL-02, ROL-03 and ROL-04: list, change and delete the roles of the caller's tenant.</summary>
public sealed class ManageRolesService(AuthDbContext db, AuditLog audit)
{
    public async Task<List<RoleListItem>> ListAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var roles = await db.Roles.AsNoTracking()
            .Where(role => role.TenantId == tenantId)
            .OrderByDescending(role => role.IsBuiltIn).ThenBy(role => role.Name)
            .Select(role => new
            {
                role.Id,
                role.Name,
                role.IsBuiltIn,
                role.CreatedAt,
                MemberCount = db.MembershipRoles.Count(assignment => assignment.RoleId == role.Id),
            })
            .ToListAsync(cancellationToken);

        var grants = await LoadGrantsAsync(tenantId, cancellationToken);
        return [.. roles.Select(role => new RoleListItem(
            role.Id, role.Name, role.IsBuiltIn, role.MemberCount,
            grants.TryGetValue(role.Id, out var held) ? held : [], role.CreatedAt))];
    }

    public async Task<UpdateRoleResult> UpdateAsync(
        Guid callerUserId, Guid tenantId, Guid roleId, ValidRoleUpdate update, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Two edits of one role take turns.
        var role = await db.Roles
            .FromSqlInterpolated($"SELECT * FROM roles WHERE id = {roleId} AND tenant_id = {tenantId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            return new UpdateRoleResult.NotFound();
        }

        if (role.IsBuiltIn)
        {
            return new UpdateRoleResult.OwnerRoleImmutable();
        }

        if (update.Name is not null)
        {
            role.Name = update.Name;
        }

        if (update.Grants is not null)
        {
            var (resolved, errors) = await RoleGrantResolver.ResolveAsync(db, tenantId, update.Grants, cancellationToken);
            if (errors.Count > 0)
            {
                return new UpdateRoleResult.InvalidGrants(errors);
            }

            // Only the difference is written, so a grant that did not change keeps its row.
            var existing = await db.RolePermissions.Where(grant => grant.RoleId == roleId).ToListAsync(cancellationToken);
            var wanted = resolved.ToDictionary(item => item.PermissionId);

            foreach (var grant in existing)
            {
                if (!wanted.TryGetValue(grant.PermissionId, out var item))
                {
                    db.RolePermissions.Remove(grant);
                }
                else if (grant.Level != item.Grant.Level)
                {
                    grant.Level = item.Grant.Level;
                }
            }

            var held = existing.Select(grant => grant.PermissionId).ToHashSet();
            foreach (var item in resolved.Where(item => !held.Contains(item.PermissionId)))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    RoleId = roleId,
                    PermissionId = item.PermissionId,
                    Level = item.Grant.Level,
                });
            }
        }

        // Audited whenever the call is accepted, with the role id as the detail (a name could be anything).
        audit.Record(AuditActions.RoleUpdated, AuditActor.User(callerUserId), tenantId, roleId.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RoleConfiguration.UniqueNamePerTenantIndexName,
        })
        {
            return new UpdateRoleResult.NameTaken();
        }

        var current = await LoadGrantsAsync(tenantId, cancellationToken);
        return new UpdateRoleResult.Updated(new RoleResponse(
            role.Id, role.Name, role.IsBuiltIn, current.TryGetValue(role.Id, out var grants) ? grants : [], role.CreatedAt));
    }

    public async Task<DeleteRoleResult> DeleteAsync(Guid callerUserId, Guid tenantId, Guid roleId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Role changes in one tenant take turns, so a role cannot be given to a member while it is being deleted.
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM tenants WHERE id = {tenantId} FOR UPDATE", cancellationToken);

        var role = await db.Roles.SingleOrDefaultAsync(candidate => candidate.Id == roleId && candidate.TenantId == tenantId, cancellationToken);
        if (role is null)
        {
            return new DeleteRoleResult.NotFound();
        }

        if (role.IsBuiltIn)
        {
            return new DeleteRoleResult.OwnerRoleImmutable();
        }

        if (await db.MembershipRoles.AnyAsync(assignment => assignment.RoleId == roleId, cancellationToken))
        {
            return new DeleteRoleResult.InUse();
        }

        await db.RolePermissions.Where(grant => grant.RoleId == roleId).ExecuteDeleteAsync(cancellationToken);
        db.Roles.Remove(role);
        audit.Record(AuditActions.RoleDeleted, AuditActor.User(callerUserId), tenantId, roleId.ToString());

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new DeleteRoleResult.Deleted();
    }

    private async Task<Dictionary<Guid, List<RoleGrantDto>>> LoadGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await (
            from grant in db.RolePermissions
            join permission in db.Permissions on grant.PermissionId equals permission.Id
            join application in db.Applications on permission.ApplicationId equals application.Id
            where grant.TenantId == tenantId
            select new { grant.RoleId, permission.ApplicationId, ApplicationKey = application.Key, Permission = permission.Key, grant.Level })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.RoleId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(row => new RoleGrantDto(row.ApplicationId, row.ApplicationKey, row.Permission, row.Level.ToString()))
                    .OrderBy(item => item.ApplicationKey, StringComparer.Ordinal).ThenBy(item => item.Permission, StringComparer.Ordinal)
                    .ToList());
    }
}
