using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Roles;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Api.Features.Applications;

/// <summary>One permission of the product's catalogue that a role template grants, and at what level. Nullable so a missing field is a validation error.</summary>
public sealed record RoleTemplateGrantInput(string? Key, string? Level);

/// <summary>A role a product ships with (ONB-04): a name, what it is for, and the permissions it grants.</summary>
public sealed record RoleTemplateInput(string? Name, string? Description, IReadOnlyList<RoleTemplateGrantInput>? Permissions);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/role-templates</c>: the whole set. Templates not in it are removed.</summary>
public sealed record SetRoleTemplatesRequest(IReadOnlyList<RoleTemplateInput>? Roles);

public sealed record RoleTemplateGrantDto(string Key, string Level);

public sealed record RoleTemplateDto(Guid Id, string Name, string? Description, IReadOnlyList<RoleTemplateGrantDto> Permissions);

public sealed record RoleTemplatesResponse(IReadOnlyList<RoleTemplateDto> Roles);

/// <summary>What copying a product's role templates into its organisations did.</summary>
/// <param name="Organisations">How many organisations with an active subscription were looked at.</param>
/// <param name="RolesCreated">How many roles were created.</param>
/// <param name="RolesSkipped">How many were left alone because the organisation already has a role of that name.</param>
public sealed record ApplyRoleTemplatesResponse(int Organisations, int RolesCreated, int RolesSkipped);

/// <summary>A role template that passed validation, with names trimmed, keys lowercased and levels parsed.</summary>
public sealed record ValidRoleTemplate(string Name, string? Description, IReadOnlyList<ValidRoleTemplateGrant> Grants);

public sealed record ValidRoleTemplateGrant(string Key, AccessLevel Level);

public static class RoleTemplateValidator
{
    public const int MaxTemplates = 50;
    public const int MaxGrantsPerTemplate = 500;
    public const int MaxDescriptionLength = 500;

    /// <summary>
    /// Checks the shape of the templates. Whether each key is in the product's catalogue is checked afterwards, against the catalogue the
    /// templates will live with (<see cref="CheckKeys"/>). Names must not repeat, and "Owner" is the one built-in role every organisation has.
    /// </summary>
    public static bool TryValidate(IReadOnlyList<RoleTemplateInput> inputs, out List<ValidRoleTemplate> valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = [];
        if (inputs.Count > MaxTemplates)
        {
            errors["Roles"] = [$"At most {MaxTemplates} roles."];
            return false;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var field = $"Roles[{index}]";
            var before = errors.Count;

            var nameErrors = new Dictionary<string, string[]>();
            var name = RoleValidator.ValidateName(input?.Name, nameErrors);
            if (name is null)
            {
                errors[$"{field}.Name"] = nameErrors["Name"];
            }
            else if (string.Equals(name, Role.OwnerName, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{field}.Name"] = [$"'{Role.OwnerName}' is the role every organisation already has. Choose another name."];
            }
            else if (!names.Add(name))
            {
                errors[$"{field}.Name"] = [$"A role named '{name}' appears more than once."];
            }

            var description = string.IsNullOrWhiteSpace(input?.Description) ? null : input.Description.Trim();
            if (description is { Length: > MaxDescriptionLength })
            {
                errors[$"{field}.Description"] = [$"Description must be at most {MaxDescriptionLength} characters."];
            }

            var grants = ReadGrants(input?.Permissions ?? [], field, errors);
            if (errors.Count == before)
            {
                valid.Add(new ValidRoleTemplate(name!, description, grants));
            }
        }

        return errors.Count == 0;
    }

    private static List<ValidRoleTemplateGrant> ReadGrants(IReadOnlyList<RoleTemplateGrantInput> inputs, string roleField, Dictionary<string, string[]> errors)
    {
        var grants = new List<ValidRoleTemplateGrant>();
        if (inputs.Count > MaxGrantsPerTemplate)
        {
            errors[$"{roleField}.Permissions"] = [$"A role can grant at most {MaxGrantsPerTemplate} permissions."];
            return grants;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            var field = $"{roleField}.Permissions[{index}]";

            var key = input?.Key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
            {
                errors[$"{field}.Key"] = ["Key is required."];
                continue;
            }

            // Level names only: a number would be read as the enum's underlying value, which is not a level a client may rely on.
            var levelText = input!.Level?.Trim();
            if (!Enum.TryParse<AccessLevel>(levelText, ignoreCase: true, out var level)
                || !Enum.IsDefined(level)
                || levelText!.All(character => char.IsDigit(character) || character == '-'))
            {
                errors[$"{field}.Level"] = ["Level must be None, ReadOnly, CreateRead or Full."];
                continue;
            }

            if (!seen.Add(key))
            {
                errors[$"{field}.Key"] = [$"Permission '{key}' appears more than once in this role."];
                continue;
            }

            grants.Add(new ValidRoleTemplateGrant(key, level));
        }

        return grants;
    }

    /// <summary>Reports every grant whose key is not in the catalogue the templates belong to, so a typo is not a role that silently grants nothing.</summary>
    public static Dictionary<string, string[]> CheckKeys(IReadOnlyList<ValidRoleTemplate> templates, IReadOnlySet<string> catalogueKeys)
    {
        var errors = new Dictionary<string, string[]>();
        for (var role = 0; role < templates.Count; role++)
        {
            for (var grant = 0; grant < templates[role].Grants.Count; grant++)
            {
                var key = templates[role].Grants[grant].Key;
                if (!catalogueKeys.Contains(key))
                {
                    errors[$"Roles[{role}].Permissions[{grant}].Key"] = [$"Unknown permission '{key}' for this product. Register it in the product's permissions first."];
                }
            }
        }

        return errors;
    }
}

public abstract record SetRoleTemplatesResult
{
    public sealed record Stored(RoleTemplatesResponse Response) : SetRoleTemplatesResult;

    public sealed record ApplicationNotFound : SetRoleTemplatesResult;

    public sealed record InvalidGrants(Dictionary<string, string[]> Errors) : SetRoleTemplatesResult;
}

public abstract record ApplyRoleTemplatesResult
{
    public sealed record Applied(ApplyRoleTemplatesResponse Response) : ApplyRoleTemplatesResult;

    public sealed record ApplicationNotFound : ApplyRoleTemplatesResult;

    /// <summary>A person created a role with one of the names at the same moment. Nothing was changed; trying again settles it.</summary>
    public sealed record Conflict : ApplyRoleTemplatesResult;
}

/// <summary>
/// ONB-04: the roles a product ships with, and how they reach an organisation. A platform admin keeps a product's templates; when an
/// organisation first subscribes, each becomes an ordinary custom role there, a copy its admins then edit freely. A copy is never
/// overwritten, and a role the organisation already has under that name is left alone.
/// </summary>
public sealed class RoleTemplateService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<RoleTemplatesResponse?> ListAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        if (!await db.Applications.AsNoTracking().AnyAsync(application => application.Id == applicationId && !application.IsBuiltIn, cancellationToken))
        {
            return null;
        }

        return await ReadAsync(applicationId, cancellationToken);
    }

    /// <summary>Makes the product's templates exactly these: new ones added, existing ones (by name) changed in place, others removed.</summary>
    public async Task<SetRoleTemplatesResult> SetAsync(
        Guid adminId, Guid applicationId, IReadOnlyList<ValidRoleTemplate> templates, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new SetRoleTemplatesResult.ApplicationNotFound();
        }

        var catalogue = await db.Permissions.Where(permission => permission.ApplicationId == applicationId)
            .ToDictionaryAsync(permission => permission.Key, permission => permission.Id, cancellationToken);
        var errors = RoleTemplateValidator.CheckKeys(templates, catalogue.Keys.ToHashSet(StringComparer.Ordinal));
        if (errors.Count > 0)
        {
            return new SetRoleTemplatesResult.InvalidGrants(errors);
        }

        var now = timeProvider.GetUtcNow();
        var existing = await db.RoleTemplates.Where(template => template.ApplicationId == applicationId).ToListAsync(cancellationToken);
        var existingByName = existing.ToDictionary(template => template.Name, StringComparer.OrdinalIgnoreCase);
        var keepNames = templates.Select(template => template.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // A template is not something an organisation's role points at (a role is a copy), so removing one touches no organisation.
        var removed = existing.Where(template => !keepNames.Contains(template.Name)).Select(template => template.Id).ToList();
        db.RoleTemplatePermissions.RemoveRange(await db.RoleTemplatePermissions.Where(grant => removed.Contains(grant.TemplateId)).ToListAsync(cancellationToken));
        db.RoleTemplates.RemoveRange(existing.Where(template => removed.Contains(template.Id)));

        foreach (var input in templates)
        {
            if (existingByName.TryGetValue(input.Name, out var template))
            {
                // Changed in place, keeping its id; its grants are replaced as a whole.
                template.Name = input.Name;
                template.Description = input.Description;
                template.UpdatedAt = now;
                db.RoleTemplatePermissions.RemoveRange(await db.RoleTemplatePermissions.Where(grant => grant.TemplateId == template.Id).ToListAsync(cancellationToken));
            }
            else
            {
                template = new ApplicationRoleTemplate
                {
                    Id = Guid.CreateVersion7(),
                    ApplicationId = applicationId,
                    Name = input.Name,
                    Description = input.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.RoleTemplates.Add(template);
            }

            AddGrants(template.Id, input.Grants, catalogue);
        }

        audit.Record(AuditActions.RoleTemplatesSet, AuditActor.User(adminId), detail: application.Key);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: ApplicationRoleTemplateConfiguration.UniqueNamePerApplicationIndexName,
        })
        {
            // Not reachable while the row lock holds, and it must never be reported as success if it somehow is.
            throw new InvalidOperationException("A role template name was inserted twice for one application.", exception);
        }

        return new SetRoleTemplatesResult.Stored(await ReadAsync(applicationId, cancellationToken));
    }

    /// <summary>
    /// Copies the product's templates into every organisation that has an active subscription to it and does not have a role of that
    /// name yet. Nothing is overwritten. For products whose templates were written after organisations had already subscribed.
    /// </summary>
    public async Task<ApplyRoleTemplatesResult> ApplyToSubscribersAsync(Guid adminId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new ApplyRoleTemplatesResult.ApplicationNotFound();
        }

        var tenantIds = await db.Subscriptions
            .Where(subscription => subscription.ApplicationId == applicationId && subscription.Status == SubscriptionStatus.Active)
            .Select(subscription => subscription.TenantId)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        int created = 0, skipped = 0;
        foreach (var tenantId in tenantIds)
        {
            var applied = await RoleTemplateCopier.CopyToTenantAsync(db, tenantId, applicationId, now, cancellationToken);
            created += applied.Created;
            skipped += applied.Skipped;
            if (applied.Created > 0)
            {
                audit.Record(AuditActions.RoleTemplatesApplied, AuditActor.User(adminId), tenantId, application.Key);
            }
        }

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
            return new ApplyRoleTemplatesResult.Conflict();
        }

        return new ApplyRoleTemplatesResult.Applied(new ApplyRoleTemplatesResponse(tenantIds.Count, created, skipped));
    }

    /// <summary>Adds the rows for a product's templates, which the caller saves with the product (registration).</summary>
    public static void AddTemplates(
        AuthDbContext db, Guid applicationId, IReadOnlyList<ValidRoleTemplate> templates, IReadOnlyDictionary<string, Guid> catalogue, DateTimeOffset now)
    {
        foreach (var input in templates)
        {
            var template = new ApplicationRoleTemplate
            {
                Id = Guid.CreateVersion7(),
                ApplicationId = applicationId,
                Name = input.Name,
                Description = input.Description,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.RoleTemplates.Add(template);
            AddGrants(db, template.Id, input.Grants, catalogue);
        }
    }

    private void AddGrants(Guid templateId, IReadOnlyList<ValidRoleTemplateGrant> grants, IReadOnlyDictionary<string, Guid> catalogue) =>
        AddGrants(db, templateId, grants, catalogue);

    private static void AddGrants(AuthDbContext db, Guid templateId, IReadOnlyList<ValidRoleTemplateGrant> grants, IReadOnlyDictionary<string, Guid> catalogue)
    {
        foreach (var grant in grants)
        {
            db.RoleTemplatePermissions.Add(new ApplicationRoleTemplatePermission
            {
                Id = Guid.CreateVersion7(),
                TemplateId = templateId,
                PermissionId = catalogue[grant.Key],
                Level = grant.Level,
            });
        }
    }

    private async Task<RoleTemplatesResponse> ReadAsync(Guid applicationId, CancellationToken cancellationToken)
    {
        var templates = await db.RoleTemplates.AsNoTracking().Where(template => template.ApplicationId == applicationId).ToListAsync(cancellationToken);
        var grants = await (
            from grant in db.RoleTemplatePermissions.AsNoTracking()
            join template in db.RoleTemplates on grant.TemplateId equals template.Id
            join permission in db.Permissions on grant.PermissionId equals permission.Id
            where template.ApplicationId == applicationId
            select new { grant.TemplateId, permission.Key, grant.Level })
            .ToListAsync(cancellationToken);
        var byTemplate = grants.ToLookup(grant => grant.TemplateId);

        return new RoleTemplatesResponse([.. templates
            .OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .Select(template => new RoleTemplateDto(
                template.Id,
                template.Name,
                template.Description,
                [.. byTemplate[template.Id]
                    .OrderBy(grant => grant.Key, StringComparer.Ordinal)
                    .Select(grant => new RoleTemplateGrantDto(grant.Key, grant.Level.ToString()))]))]);
    }
}

/// <summary>Makes ordinary roles in an organisation from a product's templates.</summary>
public static class RoleTemplateCopier
{
    /// <summary>
    /// Adds, without saving, a role for each of the product's templates that the organisation has no role of that name for, with the template's
    /// grants. A role of that name, whatever it grants, is left exactly as it is. The caller saves, so the roles exist if and only if the
    /// subscription does.
    /// </summary>
    public static async Task<(int Created, int Skipped)> CopyToTenantAsync(
        AuthDbContext db, Guid tenantId, Guid applicationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var templates = await db.RoleTemplates.AsNoTracking().Where(template => template.ApplicationId == applicationId).ToListAsync(cancellationToken);
        if (templates.Count == 0)
        {
            return (0, 0);
        }

        var templateIds = templates.Select(template => template.Id).ToList();
        var grants = (await db.RoleTemplatePermissions.AsNoTracking().Where(grant => templateIds.Contains(grant.TemplateId)).ToListAsync(cancellationToken))
            .ToLookup(grant => grant.TemplateId);
        var taken = (await db.Roles.AsNoTracking().Where(role => role.TenantId == tenantId).Select(role => role.Name).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created = 0, skipped = 0;
        foreach (var template in templates.OrderBy(template => template.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!taken.Add(template.Name))
            {
                skipped++;
                continue;
            }

            var role = new Role { Id = Guid.CreateVersion7(), TenantId = tenantId, Name = template.Name, IsBuiltIn = false, CreatedAt = now };
            db.Roles.Add(role);
            foreach (var grant in grants[template.Id])
            {
                db.RolePermissions.Add(new RolePermission
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    RoleId = role.Id,
                    PermissionId = grant.PermissionId,
                    Level = grant.Level,
                });
            }

            created++;
        }

        return (created, skipped);
    }
}
