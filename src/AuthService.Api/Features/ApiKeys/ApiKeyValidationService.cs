using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.ApiKeys;

public abstract record ValidateApiKeyResult
{
    public sealed record Valid(ValidatedApiKey Key) : ValidateApiKeyResult;

    /// <summary>
    /// Unknown, malformed, revoked, expired, belonging to a suspended tenant, or made for another product. One answer for all of them,
    /// and never anything like an anonymous caller: a product must treat it as no credential at all.
    /// </summary>
    public sealed record Invalid : ValidateApiKeyResult;

    /// <summary>The key is genuine but not for this: its tenant no longer has the product, or the action is outside its scope.</summary>
    public sealed record Forbidden(string Code, string Detail) : ValidateApiKeyResult;
}

/// <summary>
/// KEY-03: a product asks what a presented API key is. The answer is the tenant it belongs to and the scope it was created with, and
/// never more: a key made for one product is useless to another, and an action outside its permissions is refused.
/// </summary>
public sealed class ApiKeyValidationService(AuthDbContext db, TimeProvider timeProvider)
{
    // A real key is never this, so checking an unknown one costs the same hash as checking a known one.
    private static readonly string DummySalt = Convert.ToBase64String(new byte[16]);
    private static readonly string DummyHash = Convert.ToBase64String(new byte[32]);

    public async Task<ValidateApiKeyResult> ValidateAsync(
        Guid callerApplicationId, string? rawKey, string? permission, AccessLevel? requiredLevel, CancellationToken cancellationToken)
    {
        if (!ApiKeySecrets.TryParse(rawKey, out var keyId, out var secret))
        {
            return new ValidateApiKeyResult.Invalid();
        }

        var key = await db.ApiKeys.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.KeyId == keyId, cancellationToken);
        var secretMatches = ApiKeySecrets.Verify(key?.SecretSalt ?? DummySalt, key?.SecretHash ?? DummyHash, secret);
        var now = timeProvider.GetUtcNow();

        if (key is null || !secretMatches || key.RevokedAt is not null || key.ExpiresAt <= now)
        {
            return new ValidateApiKeyResult.Invalid();
        }

        var tenantActive = await db.Tenants.AnyAsync(tenant => tenant.Id == key.TenantId && tenant.Status != TenantStatus.Suspended, cancellationToken);
        if (!tenantActive || (key.ApplicationId is { } owner && owner != callerApplicationId))
        {
            return new ValidateApiKeyResult.Invalid();
        }

        var scope = new List<ApiKeyScopeDto>();
        if (key.ApplicationId is { } applicationId)
        {
            var subscribed = await db.Subscriptions.AnyAsync(
                subscription => subscription.TenantId == key.TenantId && subscription.ApplicationId == applicationId
                    && subscription.Status == SubscriptionStatus.Active, cancellationToken);
            if (!subscribed)
            {
                return new ValidateApiKeyResult.Forbidden("subscription_not_active", "The key's tenant has no active subscription to this application.");
            }

            scope = [.. (await (
                    from grant in db.ApiKeyPermissions.AsNoTracking()
                    join granted in db.Permissions on grant.PermissionId equals granted.Id
                    where grant.ApiKeyId == key.Id
                    select new { granted.Key, grant.Level })
                    .ToListAsync(cancellationToken))
                .Select(row => new ApiKeyScopeDto(row.Key, row.Level.ToString()))
                .OrderBy(item => item.Permission, StringComparer.Ordinal)];
        }

        if (!string.IsNullOrWhiteSpace(permission))
        {
            // The action the product is about to perform must be inside what the key was created for, at the level it needs.
            var wanted = requiredLevel ?? AccessLevel.ReadOnly;
            var granted = scope.FirstOrDefault(item => item.Permission == permission.Trim().ToLowerInvariant());
            if (granted is null || Enum.Parse<AccessLevel>(granted.Level) < wanted)
            {
                return new ValidateApiKeyResult.Forbidden("outside_key_scope", "This key does not allow that action.");
            }
        }

        return new ValidateApiKeyResult.Valid(new ValidatedApiKey(key.TenantId, key.Id, key.Name, key.ApplicationId, scope));
    }
}
