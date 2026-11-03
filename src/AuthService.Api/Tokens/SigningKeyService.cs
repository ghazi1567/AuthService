using System.Security.Cryptography;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

/// <summary>A public key as published in the JWKS.</summary>
public sealed record PublishedKey(string KeyId, string X, string Y);

/// <summary>
/// The signing keys, held in memory and reloaded from the database on a schedule (TOK-02). It answers three questions: which
/// key signs now, which keys may a token have been signed with, and which keys are published. All three depend on the
/// clock, so each answer is worked out when asked, not when the keys were loaded.
/// </summary>
public sealed class SigningKeyService(
    IServiceScopeFactory scopeFactory,
    KeyEncryption encryption,
    TimeProvider timeProvider,
    ILogger<SigningKeyService> logger)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile KeyEntry[] _entries = [];

    /// <summary>The key that signs a token issued right now.</summary>
    public SecurityKey CurrentSigningKey =>
        FindCurrent(timeProvider.GetUtcNow())?.Private
            ?? throw new InvalidOperationException("There is no signing key to sign with. The signing keys have not been loaded.");

    /// <summary>Whether there is a key that can sign right now. Without one no token can be issued (PLT-05 readiness).</summary>
    public bool HasCurrentKey => FindCurrent(timeProvider.GetUtcNow())?.Private is not null;

    /// <summary>How long the key that signs now has been signing, or null when there is none (PLT-05 metrics).</summary>
    public TimeSpan? CurrentKeyAge => FindCurrent(timeProvider.GetUtcNow()) is { } current ? timeProvider.GetUtcNow() - current.SignFrom : null;

    private KeyEntry? FindCurrent(DateTimeOffset now) => _entries
        .Where(entry => entry.SignFrom <= now && (entry.SignUntil is null || entry.SignUntil > now))
        .OrderByDescending(entry => entry.SignFrom)
        .FirstOrDefault();

    /// <summary>
    /// The keys a token with this <c>kid</c> may have been signed with: the published key with that id, if there is one.
    /// A token with no <c>kid</c>, or an unknown or no-longer-published one, matches nothing and so fails validation.
    /// </summary>
    public IEnumerable<SecurityKey> GetValidationKeys(string? kid)
    {
        if (string.IsNullOrEmpty(kid))
        {
            return [];
        }

        var now = timeProvider.GetUtcNow();
        return _entries.Where(entry => entry.KeyId == kid && entry.IsPublished(now)).Select(entry => (SecurityKey)entry.Public);
    }

    /// <summary>The keys in the JWKS right now: the current key, any key published ahead of its turn, and any retired key still in its overlap.</summary>
    public IReadOnlyList<PublishedKey> GetPublishedKeys()
    {
        var now = timeProvider.GetUtcNow();

        return _entries
            .Where(entry => entry.IsPublished(now))
            .OrderBy(entry => entry.SignFrom)
            .Select(entry => new PublishedKey(entry.KeyId, entry.X, entry.Y))
            .ToList();
    }

    /// <summary>
    /// Reloads the keys from the database, first creating the first key or scheduling the next if that is due. Called at
    /// startup, then on a schedule by <see cref="SigningKeyWorker"/>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = timeProvider.GetUtcNow();

            await scope.ServiceProvider.GetRequiredService<SigningKeyRotation>().EnsureAsync(now, cancellationToken);
            await LoadAsync(scope.ServiceProvider.GetRequiredService<AuthDbContext>(), now, cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Emergency rotation (see <see cref="SigningKeyRotation.RotateNowAsync"/>), then reloads at once.</summary>
    public async Task RotateNowAsync(AuditActor actor, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var now = timeProvider.GetUtcNow();

            await scope.ServiceProvider.GetRequiredService<SigningKeyRotation>().RotateNowAsync(now, actor, cancellationToken);
            await LoadAsync(scope.ServiceProvider.GetRequiredService<AuthDbContext>(), now, cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }

        logger.LogWarning("Signing keys were rotated immediately (emergency rotation).");
    }

    private async Task LoadAsync(AuthDbContext db, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A key that has left the JWKS can never matter again, so it is not kept.
        var rows = await db.SigningKeys.AsNoTracking()
            .Where(key => key.PublishUntil == null || key.PublishUntil > now)
            .ToListAsync(cancellationToken);

        var previous = _entries.ToDictionary(entry => entry.KeyId);
        var loaded = new List<KeyEntry>(rows.Count);

        foreach (var row in rows)
        {
            // Only a key that is signing, or will sign, needs its private half; the others only validate.
            var needsPrivate = row.SignUntil is null || row.SignUntil > now;

            previous.TryGetValue(row.KeyId, out var known);
            var publicKey = known?.Public ?? CreatePublicKey(row.KeyId, row.PublicKeyX, row.PublicKeyY);
            var privateKey = needsPrivate ? known?.Private ?? CreatePrivateKey(row.KeyId, row.EncryptedPrivateKey) : null;

            loaded.Add(new KeyEntry(row.KeyId, row.PublicKeyX, row.PublicKeyY, row.PublishFrom, row.SignFrom, row.SignUntil, row.PublishUntil, publicKey, privateKey));
        }

        _entries = [.. loaded];
    }

    private static ECDsaSecurityKey CreatePublicKey(string keyId, string x, string y)
    {
        var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = Base64UrlEncoder.DecodeBytes(x), Y = Base64UrlEncoder.DecodeBytes(y) },
        });

        return new ECDsaSecurityKey(ecdsa) { KeyId = keyId };
    }

    private ECDsaSecurityKey CreatePrivateKey(string keyId, string encryptedPrivateKey)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(encryption.Decrypt(encryptedPrivateKey), out _);

        return new ECDsaSecurityKey(ecdsa) { KeyId = keyId };
    }

    private sealed record KeyEntry(
        string KeyId,
        string X,
        string Y,
        DateTimeOffset PublishFrom,
        DateTimeOffset SignFrom,
        DateTimeOffset? SignUntil,
        DateTimeOffset? PublishUntil,
        ECDsaSecurityKey Public,
        ECDsaSecurityKey? Private)
    {
        public bool IsPublished(DateTimeOffset now) => PublishFrom <= now && (PublishUntil is null || PublishUntil > now);
    }
}
