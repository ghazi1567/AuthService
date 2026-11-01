using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Api.Features.Roles;

/// <summary>One permission a new role should grant, and at what level. Nullable so a missing field is a validation error.</summary>
public sealed record RoleGrantInput(Guid? ApplicationId, string? Permission, string? Level);

/// <summary>Body of <c>POST /api/auth/roles</c>.</summary>
public sealed record CreateRoleRequest(string? Name, IReadOnlyList<RoleGrantInput>? Permissions);

public sealed record RoleGrantDto(Guid ApplicationId, string ApplicationKey, string Permission, string Level);

public sealed record RoleResponse(Guid Id, string Name, bool IsBuiltIn, IReadOnlyList<RoleGrantDto> Permissions, DateTimeOffset CreatedAt);

/// <summary>A grant that passed validation, with the key normalised.</summary>
public sealed record ValidRoleGrant(Guid ApplicationId, string Permission, AccessLevel Level);

public sealed record ValidRole(string Name, IReadOnlyList<ValidRoleGrant> Grants);

public abstract record CreateRoleResult
{
    public sealed record Created(RoleResponse Role) : CreateRoleResult;

    public sealed record NameTaken : CreateRoleResult;

    /// <summary>One or more grants name a product the tenant cannot use or a permission that does not exist.</summary>
    public sealed record InvalidGrants(Dictionary<string, string[]> Errors) : CreateRoleResult;
}

public static class RoleValidator
{
    public const int MaxNameLength = 100;
    public const int MaxGrantsPerRole = 500;

    public static bool TryValidate(CreateRoleRequest request, out ValidRole? valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors[nameof(CreateRoleRequest.Name)] = ["Name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors[nameof(CreateRoleRequest.Name)] = [$"Name must be at most {MaxNameLength} characters."];
        }

        var inputs = request.Permissions ?? [];
        if (inputs.Count > MaxGrantsPerRole)
        {
            errors[nameof(CreateRoleRequest.Permissions)] = [$"A role can grant at most {MaxGrantsPerRole} permissions."];
        }

        var grants = new List<ValidRoleGrant>();
        var seen = new HashSet<(Guid, string)>();
        for (var index = 0; index < Math.Min(inputs.Count, MaxGrantsPerRole); index++)
        {
            var input = inputs[index];
            var field = $"{nameof(CreateRoleRequest.Permissions)}[{index}]";

            if (input?.ApplicationId is not { } applicationId || applicationId == Guid.Empty)
            {
                errors[$"{field}.ApplicationId"] = ["ApplicationId is required."];
                continue;
            }

            // Keys are case-insensitive in requests, like when they are registered.
            var key = input.Permission?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
            {
                errors[$"{field}.Permission"] = ["Permission is required."];
                continue;
            }

            // Level names only: a number would be read as the enum's underlying value, which is not a level a client may rely on.
            if (!Enum.TryParse<AccessLevel>(input.Level?.Trim(), ignoreCase: true, out var level)
                || !Enum.IsDefined(level)
                || input.Level!.Trim().All(character => char.IsDigit(character) || character == '-'))
            {
                errors[$"{field}.Level"] = ["Level must be None, ReadOnly, CreateRead or Full."];
                continue;
            }

            if (!seen.Add((applicationId, key)))
            {
                errors[$"{field}.Permission"] = [$"Permission '{key}' appears more than once for this application."];
                continue;
            }

            grants.Add(new ValidRoleGrant(applicationId, key, level));
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidRole(name!, grants);
        return true;
    }
}

/// <summary>ROL-01: a tenant admin defines a named role that grants permissions of the products the tenant uses.</summary>
public sealed class CreateRoleService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<CreateRoleResult> CreateAsync(Guid userId, Guid tenantId, ValidRole input, CancellationToken cancellationToken)
    {
        // A grant is allowed only for a product the tenant actually uses: one it has an Active subscription to, or the Auth Service's
        // own, which every tenant has. An unknown product id and an unsubscribed one look the same, so ids cannot be probed.
        var applicationIds = input.Grants.Select(grant => grant.ApplicationId).Distinct().ToList();
        var usable = (await db.Subscriptions
                .Where(subscription => subscription.TenantId == tenantId && subscription.Status == SubscriptionStatus.Active
                    && applicationIds.Contains(subscription.ApplicationId))
                .Select(subscription => subscription.ApplicationId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        if (applicationIds.Contains(BuiltInApplication.Id))
        {
            usable.Add(BuiltInApplication.Id);
        }

        var catalog = await (
            from permission in db.Permissions
            join application in db.Applications on permission.ApplicationId equals application.Id
            where applicationIds.Contains(permission.ApplicationId)
            select new { permission.Id, permission.ApplicationId, permission.Key, ApplicationKey = application.Key })
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var byKey = catalog.ToDictionary(item => (item.ApplicationId, item.Key));

        var errors = new Dictionary<string, string[]>();
        var resolved = new List<(ValidRoleGrant Grant, Guid PermissionId, string ApplicationKey)>();
        for (var index = 0; index < input.Grants.Count; index++)
        {
            var grant = input.Grants[index];
            var field = $"{nameof(CreateRoleRequest.Permissions)}[{index}]";

            if (!usable.Contains(grant.ApplicationId))
            {
                errors[field] = ["The application is unknown or the tenant has no active subscription to it."];
            }
            else if (!byKey.TryGetValue((grant.ApplicationId, grant.Permission), out var found))
            {
                errors[field] = [$"Unknown permission '{grant.Permission}' for this application."];
            }
            else
            {
                resolved.Add((grant, found.Id, found.ApplicationKey));
            }
        }

        if (errors.Count > 0)
        {
            return new CreateRoleResult.InvalidGrants(errors);
        }

        var now = timeProvider.GetUtcNow();
        var role = new Role { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = input.Name, IsBuiltIn = false, CreatedAt = now };
        db.Roles.Add(role);
        foreach (var (grant, permissionId, _) in resolved)
        {
            db.RolePermissions.Add(new RolePermission
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                RoleId = role.Id,
                PermissionId = permissionId,
                Level = grant.Level,
            });
        }

        // The role's id is a safe detail (a name could be anything); the actor is the admin and the tenant is the one concerned.
        audit.Record(AuditActions.RoleCreated, AuditActor.User(userId), tenantId, role.Id.ToString());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: RoleConfiguration.UniqueNamePerTenantIndexName,
        })
        {
            return new CreateRoleResult.NameTaken();
        }

        var grants = resolved
            .Select(item => new RoleGrantDto(item.Grant.ApplicationId, item.ApplicationKey, item.Grant.Permission, item.Grant.Level.ToString()))
            .OrderBy(item => item.ApplicationKey, StringComparer.Ordinal).ThenBy(item => item.Permission, StringComparer.Ordinal)
            .ToList();
        return new CreateRoleResult.Created(new RoleResponse(role.Id, role.Name, role.IsBuiltIn, grants, role.CreatedAt));
    }
}
