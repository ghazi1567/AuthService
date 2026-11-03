using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.TokenExchange;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.ApiKeys;

public sealed record ValidApiKeyRequest(string Name, Guid? ApplicationId, IReadOnlyList<(string Permission, AccessLevel Level)> Permissions);

public abstract record CreateApiKeyResult
{
    public sealed record Created(CreatedApiKeyResponse Response) : CreateApiKeyResult;

    /// <summary>A permission is unknown, or the product is not one the tenant is actively subscribed to. One answer for both.</summary>
    public sealed record InvalidScope(Dictionary<string, string[]> Errors) : CreateApiKeyResult;

    /// <summary>The key would be given more than the person creating it has. A key can never be a way round one's own roles.</summary>
    public sealed record ExceedsCreatorsPermissions(IReadOnlyList<string> Permissions) : CreateApiKeyResult;
}

public abstract record RotateApiKeyResult
{
    public sealed record Rotated(CreatedApiKeyResponse Response, DateTimeOffset PreviousKeyExpiresAt) : RotateApiKeyResult;

    public sealed record NotFound : RotateApiKeyResult;

    /// <summary>A revoked or expired key cannot be rotated: issue a new one instead.</summary>
    public sealed record NotActive : RotateApiKeyResult;
}

public static class ApiKeyValidator
{
    public const int MaxNameLength = 100;
    public const int MaxPermissions = 200;
    public const int MaxGraceHours = 24 * 30;

    public static bool TryValidate(CreateApiKeyRequest request, out ValidApiKeyRequest? valid, out Dictionary<string, string[]> errors)
    {
        errors = [];
        valid = null;

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors[nameof(CreateApiKeyRequest.Name)] = ["Name is required."];
        }
        else if (name.Length > MaxNameLength)
        {
            errors[nameof(CreateApiKeyRequest.Name)] = [$"Name must be at most {MaxNameLength} characters."];
        }

        var inputs = request.Permissions ?? [];
        if (request.ApplicationId is { } applicationId && applicationId == Guid.Empty)
        {
            errors[nameof(CreateApiKeyRequest.ApplicationId)] = ["ApplicationId is not valid."];
        }
        else if (request.ApplicationId is null && inputs.Count > 0)
        {
            errors[nameof(CreateApiKeyRequest.Permissions)] = ["Permissions need an ApplicationId: they are permissions of that product."];
        }
        else if (request.ApplicationId is not null && inputs.Count == 0)
        {
            errors[nameof(CreateApiKeyRequest.Permissions)] = ["A key for a product needs at least one permission."];
        }
        else if (inputs.Count > MaxPermissions)
        {
            errors[nameof(CreateApiKeyRequest.Permissions)] = [$"At most {MaxPermissions} permissions."];
        }

        var permissions = new List<(string, AccessLevel)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(inputs.Count, MaxPermissions); index++)
        {
            var field = $"{nameof(CreateApiKeyRequest.Permissions)}[{index}]";
            var key = inputs[index]?.Permission?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(key))
            {
                errors[$"{field}.Permission"] = ["Permission is required."];
                continue;
            }

            var level = AccessLevel.Full;
            var rawLevel = inputs[index]!.Level?.Trim();
            if (rawLevel is not null && (!Enum.TryParse(rawLevel, ignoreCase: true, out level) || !Enum.IsDefined(level) || rawLevel.All(character => char.IsDigit(character) || character == '-')))
            {
                errors[$"{field}.Level"] = ["Level must be None, ReadOnly, CreateRead or Full."];
                continue;
            }

            if (!seen.Add(key))
            {
                errors[$"{field}.Permission"] = [$"Permission '{key}' appears more than once."];
                continue;
            }

            permissions.Add((key, level));
        }

        if (errors.Count > 0)
        {
            return false;
        }

        valid = new ValidApiKeyRequest(name!, request.ApplicationId, permissions);
        return true;
    }
}

/// <summary>KEY-01 and KEY-02: a tenant creates, lists, rotates and revokes its API keys.</summary>
public sealed class ApiKeyService(
    AuthDbContext db,
    PermissionResolver resolver,
    IOptions<ApiKeyOptions> options,
    AuditLog audit,
    TimeProvider timeProvider)
{
    public async Task<CreateApiKeyResult> CreateAsync(Guid callerUserId, Guid tenantId, ValidApiKeyRequest input, CancellationToken cancellationToken)
    {
        var resolved = new List<(Permission Permission, AccessLevel Level)>();
        string? applicationKey = null;

        if (input.ApplicationId is { } applicationId)
        {
            // Only a product the tenant actually uses, and never the Auth Service's own. An unknown id and an unsubscribed one look the same.
            var application = await (
                from candidate in db.Applications
                join subscription in db.Subscriptions on candidate.Id equals subscription.ApplicationId
                where candidate.Id == applicationId && !candidate.IsBuiltIn
                    && subscription.TenantId == tenantId && subscription.Status == SubscriptionStatus.Active
                select new { candidate.Key })
                .AsNoTracking()
                .SingleOrDefaultAsync(cancellationToken);
            if (application is null)
            {
                return new CreateApiKeyResult.InvalidScope(new Dictionary<string, string[]>
                {
                    [nameof(CreateApiKeyRequest.ApplicationId)] = ["The application is unknown or the tenant has no active subscription to it."],
                });
            }

            applicationKey = application.Key;
            var catalog = await db.Permissions.AsNoTracking()
                .Where(permission => permission.ApplicationId == applicationId)
                .ToDictionaryAsync(permission => permission.Key, cancellationToken);

            var errors = new Dictionary<string, string[]>();
            for (var index = 0; index < input.Permissions.Count; index++)
            {
                if (catalog.TryGetValue(input.Permissions[index].Permission, out var permission))
                {
                    resolved.Add((permission, input.Permissions[index].Level));
                }
                else
                {
                    errors[$"{nameof(CreateApiKeyRequest.Permissions)}[{index}]"] = [$"Unknown permission '{input.Permissions[index].Permission}' for this application."];
                }
            }

            if (errors.Count > 0)
            {
                return new CreateApiKeyResult.InvalidScope(errors);
            }

            // A key is narrower than the person who makes it, never wider: each permission must be within what their own roles grant.
            var membershipId = await db.TenantMemberships.AsNoTracking()
                .Where(membership => membership.TenantId == tenantId && membership.UserId == callerUserId && membership.Status == MembershipStatus.Active)
                .Select(membership => (Guid?)membership.Id)
                .SingleOrDefaultAsync(cancellationToken);
            var held = membershipId is { } id
                ? await resolver.ResolveAsync(tenantId, id, applicationId, cancellationToken)
                : [];
            var beyond = resolved
                .Where(item => !held.TryGetValue(item.Permission.Key, out var level) || level < item.Level)
                .Select(item => item.Permission.Key)
                .Order(StringComparer.Ordinal)
                .ToList();
            if (beyond.Count > 0)
            {
                return new CreateApiKeyResult.ExceedsCreatorsPermissions(beyond);
            }
        }

        var now = timeProvider.GetUtcNow();
        var generated = ApiKeySecrets.Generate();
        var key = NewKey(tenantId, input.ApplicationId, input.Name, callerUserId, generated, now);
        db.ApiKeys.Add(key);
        foreach (var (permission, level) in resolved)
        {
            db.ApiKeyPermissions.Add(NewPermission(tenantId, key.Id, permission.Id, level));
        }

        audit.Record(AuditActions.ApiKeyCreated, AuditActor.User(callerUserId), tenantId, key.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);

        var scope = resolved.Select(item => new ApiKeyScopeDto(item.Permission.Key, item.Level.ToString())).OrderBy(item => item.Permission, StringComparer.Ordinal).ToList();
        return new CreateApiKeyResult.Created(new CreatedApiKeyResponse(ToSummary(key, applicationKey, scope, now), generated.RawKey));
    }

    /// <summary>One page of the tenant's keys, newest first (PLT-08).</summary>
    public async Task<PagedResponse<ApiKeySummary, TimeCursor>> ListAsync(Guid tenantId, PageQuery page, CancellationToken cancellationToken)
    {
        var rows = await page.NewestFirstAsync(KeyRows(tenantId), row => row.Key.CreatedAt, row => row.Key.Id, cancellationToken);
        return new PagedResponse<ApiKeySummary, TimeCursor>(await ToSummariesAsync(tenantId, rows.Rows, cancellationToken), rows.Next);
    }

    public async Task<ApiKeySummary?> GetAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken) =>
        (await LoadSummariesAsync(tenantId, keyId, cancellationToken)).SingleOrDefault();

    /// <summary>Revokes a key at once. Returns false when the tenant has no such key. Revoking a revoked key is a no-op.</summary>
    public async Task<bool> RevokeAsync(Guid callerUserId, Guid tenantId, Guid keyId, CancellationToken cancellationToken)
    {
        var key = await db.ApiKeys.SingleOrDefaultAsync(candidate => candidate.Id == keyId && candidate.TenantId == tenantId, cancellationToken);
        if (key is null)
        {
            return false;
        }

        if (key.RevokedAt is null)
        {
            key.RevokedAt = timeProvider.GetUtcNow();
            audit.Record(AuditActions.ApiKeyRevoked, AuditActor.User(callerUserId), tenantId, key.Id.ToString());
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    public async Task<RotateApiKeyResult> RotateAsync(
        Guid callerUserId, Guid tenantId, Guid keyId, int? graceHours, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var old = await db.ApiKeys
            .FromSqlInterpolated($"SELECT * FROM api_keys WHERE id = {keyId} AND tenant_id = {tenantId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (old is null)
        {
            return new RotateApiKeyResult.NotFound();
        }

        var now = timeProvider.GetUtcNow();
        if (old.RevokedAt is not null || old.ExpiresAt <= now)
        {
            return new RotateApiKeyResult.NotActive();
        }

        // The old key works until the end of the grace period. A key already being rotated out keeps its earlier end.
        var graceEnd = now.AddHours(graceHours ?? options.Value.RotationGraceHours);
        if (old.ExpiresAt is null || graceEnd < old.ExpiresAt)
        {
            old.ExpiresAt = graceEnd;
        }

        var generated = ApiKeySecrets.Generate();
        var replacement = NewKey(tenantId, old.ApplicationId, old.Name, callerUserId, generated, now);
        db.ApiKeys.Add(replacement);

        // The same scope, exactly: the new key can do what the old one could and nothing more.
        var grants = await db.ApiKeyPermissions.AsNoTracking().Where(grant => grant.ApiKeyId == old.Id).ToListAsync(cancellationToken);
        foreach (var grant in grants)
        {
            db.ApiKeyPermissions.Add(NewPermission(tenantId, replacement.Id, grant.PermissionId, grant.Level));
        }

        audit.Record(AuditActions.ApiKeyRotated, AuditActor.User(callerUserId), tenantId, replacement.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var summary = (await LoadSummariesAsync(tenantId, replacement.Id, cancellationToken)).Single();
        return new RotateApiKeyResult.Rotated(new CreatedApiKeyResponse(summary, generated.RawKey), old.ExpiresAt!.Value);
    }

    // Initialised by member, not by constructor: the database query can then be ordered and filtered by these members.
    private sealed record KeyRow
    {
        public required ApiKey Key { get; init; }
        public string? ApplicationKey { get; init; }
    }

    private IQueryable<KeyRow> KeyRows(Guid tenantId) =>
        from key in db.ApiKeys.AsNoTracking()
        join application in db.Applications on key.ApplicationId equals application.Id into applications
        from application in applications.DefaultIfEmpty()
        where key.TenantId == tenantId
        select new KeyRow { Key = key, ApplicationKey = application.Key };

    private async Task<List<ApiKeySummary>> LoadSummariesAsync(Guid tenantId, Guid keyId, CancellationToken cancellationToken) =>
        await ToSummariesAsync(tenantId, await KeyRows(tenantId).Where(row => row.Key.Id == keyId).ToListAsync(cancellationToken), cancellationToken);

    /// <summary>The scopes of just these keys, in one read.</summary>
    private async Task<List<ApiKeySummary>> ToSummariesAsync(Guid tenantId, IReadOnlyList<KeyRow> keys, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var ids = keys.Select(row => row.Key.Id).ToList();

        var scopes = (await (
                from grant in db.ApiKeyPermissions.AsNoTracking()
                join permission in db.Permissions on grant.PermissionId equals permission.Id
                where grant.TenantId == tenantId && ids.Contains(grant.ApiKeyId)
                select new { grant.ApiKeyId, permission.Key, grant.Level })
                .ToListAsync(cancellationToken))
            .ToLookup(row => row.ApiKeyId, row => new ApiKeyScopeDto(row.Key, row.Level.ToString()));

        return [.. keys.Select(row => ToSummary(
            row.Key, row.ApplicationKey, [.. scopes[row.Key.Id].OrderBy(item => item.Permission, StringComparer.Ordinal)], now))];
    }

    private static ApiKeySummary ToSummary(ApiKey key, string? applicationKey, IReadOnlyList<ApiKeyScopeDto> scope, DateTimeOffset now) => new(
        key.Id, key.Name, ApiKeySecrets.Mask(key.KeyId), key.ApplicationId, applicationKey, scope, StatusOf(key, now),
        key.CreatedAt, key.ExpiresAt, key.RevokedAt);

    public static string StatusOf(ApiKey key, DateTimeOffset now) =>
        key.RevokedAt is not null ? "Revoked"
        : key.ExpiresAt is { } end ? (end <= now ? "Expired" : "Expiring")
        : "Active";

    private static ApiKey NewKey(Guid tenantId, Guid? applicationId, string name, Guid createdBy, ApiKeySecrets.Generated generated, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ApplicationId = applicationId,
        Name = name,
        KeyId = generated.KeyId,
        SecretSalt = generated.Salt,
        SecretHash = generated.Hash,
        CreatedByUserId = createdBy,
        CreatedAt = now,
    };

    private static ApiKeyPermission NewPermission(Guid tenantId, Guid keyId, Guid permissionId, AccessLevel level) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ApiKeyId = keyId,
        PermissionId = permissionId,
        Level = level,
    };
}
