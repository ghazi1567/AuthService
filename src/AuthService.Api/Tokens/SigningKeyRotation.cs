using System.Security.Cryptography;
using System.Text;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Api.Tokens;

/// <summary>
/// Keeps the signing keys in the database rotating on schedule (TOK-02). Every change happens under a database lock, so
/// several instances starting or refreshing at once cannot each create a key.
/// </summary>
public sealed class SigningKeyRotation(AuthDbContext db, KeyEncryption encryption, AuditLog audit, IOptions<IdentityTokenOptions> options)
{
    // An arbitrary number that identifies this lock to PostgreSQL's advisory lock.
    private const long AdvisoryLockId = 7_270_001;

    /// <summary>
    /// Makes sure a signing key exists and, when it is time, schedules the next one. A new key is published
    /// <c>SigningKeyPrePublishHours</c> before it starts signing, and the key it replaces stays published for
    /// <c>SigningKeyPostRetireHours</c> after it stops. If rotation is overdue (the service was down), the next key still
    /// waits the full pre-publish time, so no product ever sees a signature from a key it has not been told about.
    /// </summary>
    public async Task EnsureAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TakeLockAsync(cancellationToken);

        var keys = await db.SigningKeys.ToListAsync(cancellationToken);

        if (keys.Count == 0)
        {
            db.SigningKeys.Add(NewKey(now, publishFrom: now, signFrom: now));
            audit.Record(AuditActions.SigningKeyCreated, AuditActor.System, detail: "initial");
        }
        else
        {
            var current = keys
                .Where(key => key.SignFrom <= now && (key.SignUntil is null || key.SignUntil > now))
                .OrderByDescending(key => key.SignFrom)
                .FirstOrDefault();

            var nextAlreadyScheduled = current is not null && keys.Any(key => key.SignFrom > current.SignFrom);
            if (current is not null && !nextAlreadyScheduled)
            {
                var prePublish = TimeSpan.FromHours(settings.SigningKeyPrePublishHours);
                var rotateAt = current.SignFrom.AddDays(settings.SigningKeyRotationDays);

                if (now >= rotateAt - prePublish)
                {
                    var signFrom = rotateAt > now + prePublish ? rotateAt : now + prePublish;

                    current.SignUntil = signFrom;
                    current.PublishUntil = signFrom.AddHours(settings.SigningKeyPostRetireHours);
                    db.SigningKeys.Add(NewKey(now, publishFrom: now, signFrom: signFrom));
                    audit.Record(AuditActions.SigningKeyRotated, AuditActor.System, detail: "scheduled");
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Emergency rotation: a new key signs from this moment, and every earlier key (including one scheduled for later)
    /// stops signing and leaves the JWKS at once. Tokens signed by them stop validating immediately, which is the point
    /// when a key may be compromised.
    /// </summary>
    public async Task RotateNowAsync(DateTimeOffset now, AuditActor actor, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await TakeLockAsync(cancellationToken);

        foreach (var key in await db.SigningKeys.ToListAsync(cancellationToken))
        {
            if (key.SignUntil is null || key.SignUntil > now)
            {
                key.SignUntil = now;
            }

            if (key.PublishUntil is null || key.PublishUntil > now)
            {
                key.PublishUntil = now;
            }
        }

        db.SigningKeys.Add(NewKey(now, publishFrom: now, signFrom: now));

        audit.Record(AuditActions.SigningKeyRotated, actor, detail: "emergency");

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Released automatically when the transaction ends.
    private Task TakeLockAsync(CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AdvisoryLockId})", cancellationToken);

    private SigningKey NewKey(DateTimeOffset now, DateTimeOffset publishFrom, DateTimeOffset signFrom)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var point = ecdsa.ExportParameters(includePrivateParameters: false).Q;
        var x = Base64UrlEncoder.Encode(point.X!);
        var y = Base64UrlEncoder.Encode(point.Y!);

        return new SigningKey
        {
            Id = Guid.CreateVersion7(),
            KeyId = Thumbprint(x, y),
            PublicKeyX = x,
            PublicKeyY = y,
            EncryptedPrivateKey = encryption.Encrypt(ecdsa.ExportPkcs8PrivateKey()),
            CreatedAt = now,
            PublishFrom = publishFrom,
            SignFrom = signFrom,
        };
    }

    /// <summary>The key's RFC 7638 JWK thumbprint, used as its <c>kid</c>: the same public key always has the same id.</summary>
    private static string Thumbprint(string x, string y) =>
        Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes($$"""{"crv":"P-256","kty":"EC","x":"{{x}}","y":"{{y}}"}""")));
}
