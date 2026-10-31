using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Applications;

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/secret/rotate</c>. The whole body is optional.</summary>
public sealed record RotateSecretRequest(int? GraceHours);

/// <summary>
/// The new client secret, shown in this response and nowhere else, ever. <see cref="PreviousSecretsExpireAt"/> is when the
/// secrets being replaced stop working.
/// </summary>
public sealed record RotateSecretResponse(
    Guid ApplicationId,
    string ClientId,
    string ClientSecret,
    DateTimeOffset PreviousSecretsExpireAt);

public abstract record RotateSecretResult
{
    public sealed record Rotated(RotateSecretResponse Response) : RotateSecretResult;

    public sealed record ApplicationNotFound : RotateSecretResult;
}

/// <summary>Replaces a product's client secret while the old one keeps working for a grace period (APP-05).</summary>
public sealed class SecretRotationService(
    AuthDbContext db,
    AuditLog audit,
    IOptions<ApplicationCredentialOptions> options,
    TimeProvider timeProvider)
{
    public const int MaxGraceHours = 24 * 30;

    public async Task<RotateSecretResult> RotateAsync(Guid adminId, Guid applicationId, int? graceHours, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var application = await db.LockApplicationAsync(applicationId, cancellationToken);
        if (application is null)
        {
            return new RotateSecretResult.ApplicationNotFound();
        }

        var now = timeProvider.GetUtcNow();
        var expiresAt = now.AddHours(graceHours ?? options.Value.SecretGraceHours);

        // Every secret that would outlast the grace period now stops at its end. One already due to expire sooner (from an
        // earlier rotation) keeps its earlier time, so rotating again never extends an old secret's life.
        var replaced = await db.ApplicationSecrets
            .Where(secret => secret.ApplicationId == applicationId && (secret.ExpiresAt == null || secret.ExpiresAt > expiresAt))
            .ToListAsync(cancellationToken);
        foreach (var secret in replaced)
        {
            secret.ExpiresAt = expiresAt;
        }

        var clientSecret = SecretToken.NewValue();
        db.ApplicationSecrets.Add(new ApplicationSecret
        {
            Id = Guid.CreateVersion7(),
            ApplicationId = applicationId,
            SecretHash = SecretToken.Hash(clientSecret),
            CreatedAt = now,
        });

        // The product's key is a safe, readable detail; no secret, old or new, goes near the audit log.
        audit.Record(AuditActions.ApplicationSecretRotated, AuditActor.User(adminId), detail: application.Key);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new RotateSecretResult.Rotated(new RotateSecretResponse(applicationId, application.ClientId, clientSecret, expiresAt));
    }
}
