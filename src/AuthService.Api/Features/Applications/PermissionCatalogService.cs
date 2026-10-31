using System.Text.RegularExpressions;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Api.Features.Applications;

/// <summary>A permission that passed validation, with the key normalised.</summary>
public sealed record ValidPermission(string Key, string Label, string? Description);

public abstract record RegisterPermissionsResult
{
    public sealed record Registered(RegisterPermissionsResponse Response) : RegisterPermissionsResult;

    public sealed record ApplicationNotFound : RegisterPermissionsResult;
}

public abstract record SetApplicationActiveResult
{
    public sealed record Updated(ApplicationStatusResponse Status) : SetApplicationActiveResult;

    public sealed record ApplicationNotFound : SetApplicationActiveResult;

    /// <summary>A product cannot be activated before it has at least one permission: a tenant would subscribe to nothing to grant.</summary>
    public sealed record NoPermissions : SetApplicationActiveResult;
}

public static partial class PermissionCatalogValidator
{
    public const int MaxPermissionsPerRequest = 200;
    public const int MaxKeyLength = 100;
    public const int MaxLabelLength = 200;
    public const int MaxDescriptionLength = 1000;

    public static bool TryValidate(
        RegisterPermissionsRequest request,
        out List<ValidPermission> valid,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = [];

        if (request.Permissions is not { Count: > 0 })
        {
            errors[nameof(RegisterPermissionsRequest.Permissions)] = ["At least one permission is required."];
            return false;
        }

        if (request.Permissions.Count > MaxPermissionsPerRequest)
        {
            errors[nameof(RegisterPermissionsRequest.Permissions)] = [$"At most {MaxPermissionsPerRequest} permissions per request."];
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Permissions.Count; index++)
        {
            var input = request.Permissions[index];
            var field = $"{nameof(RegisterPermissionsRequest.Permissions)}[{index}]";
            var valid_ = true;

            // Keys are case-insensitive: "Campaign.Create" and "campaign.create" are the same permission.
            var key = input?.Key?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || !KeyPattern().IsMatch(key))
            {
                errors[$"{field}.Key"] =
                    [$"Key must be 1 to {MaxKeyLength} lowercase letters, digits and . _ - characters, such as campaign.create."];
                valid_ = false;
            }
            else if (!seen.Add(key))
            {
                errors[$"{field}.Key"] = [$"Key '{key}' appears more than once in this request."];
                valid_ = false;
            }

            var label = input?.Label?.Trim();
            if (string.IsNullOrEmpty(label) || label.Length > MaxLabelLength)
            {
                errors[$"{field}.Label"] = [$"Label is required and must be at most {MaxLabelLength} characters."];
                valid_ = false;
            }

            var description = string.IsNullOrWhiteSpace(input?.Description) ? null : input.Description.Trim();
            if (description is { Length: > MaxDescriptionLength })
            {
                errors[$"{field}.Description"] = [$"Description must be at most {MaxDescriptionLength} characters."];
                valid_ = false;
            }

            if (valid_)
            {
                valid.Add(new ValidPermission(key!, label!, description));
            }
        }

        return errors.Count == 0;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$")]
    private static partial Regex KeyPattern();
}

/// <summary>APP-02: a product's permission catalog, and the switch that makes the product available once it has one.</summary>
public sealed class PermissionCatalogService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<RegisterPermissionsResult> RegisterAsync(
        Guid adminId, Guid applicationId, IReadOnlyList<ValidPermission> permissions, CancellationToken cancellationToken)
    {
        // Registrations of one product are serialised by a row lock, so two simultaneous ones cannot both insert the same new key.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new RegisterPermissionsResult.ApplicationNotFound();
        }

        var catalog = await db.Permissions.Where(permission => permission.ApplicationId == applicationId)
            .ToDictionaryAsync(permission => permission.Key, cancellationToken);

        var now = timeProvider.GetUtcNow();
        int created = 0, updated = 0, unchanged = 0;
        foreach (var input in permissions)
        {
            if (!catalog.TryGetValue(input.Key, out var permission))
            {
                permission = new Permission
                {
                    Id = Guid.CreateVersion7(),
                    ApplicationId = applicationId,
                    Key = input.Key,
                    Label = input.Label,
                    Description = input.Description,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Permissions.Add(permission);
                catalog[input.Key] = permission;
                created++;
            }
            else if (permission.Label != input.Label || permission.Description != input.Description)
            {
                // Updated in place, keeping its Id, which is what roles refer to.
                permission.Label = input.Label;
                permission.Description = input.Description;
                permission.UpdatedAt = now;
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        audit.Record(AuditActions.PermissionsRegistered, AuditActor.User(adminId), detail: application.Key);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: PermissionConfiguration.UniqueKeyPerApplicationIndexName,
        })
        {
            // Not reachable while the row lock holds, and it must never be reported as success if it somehow is.
            throw new InvalidOperationException("A permission key was inserted twice for one application.", exception);
        }

        var all = catalog.Values.OrderBy(permission => permission.Key, StringComparer.Ordinal)
            .Select(permission => new PermissionDto(permission.Id, permission.Key, permission.Label, permission.Description))
            .ToList();
        return new RegisterPermissionsResult.Registered(new RegisterPermissionsResponse(applicationId, created, updated, unchanged, all));
    }

    public async Task<SetApplicationActiveResult> SetActiveAsync(
        Guid adminId, Guid applicationId, bool isActive, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new SetApplicationActiveResult.ApplicationNotFound();
        }

        if (isActive && !await db.Permissions.AnyAsync(permission => permission.ApplicationId == applicationId, cancellationToken))
        {
            return new SetApplicationActiveResult.NoPermissions();
        }

        if (application.IsActive != isActive)
        {
            application.IsActive = isActive;
            audit.Record(isActive ? AuditActions.ApplicationActivated : AuditActions.ApplicationDeactivated,
                AuditActor.User(adminId), detail: application.Key);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new SetApplicationActiveResult.Updated(new ApplicationStatusResponse(application.Id, application.Key, application.IsActive));
    }
}
