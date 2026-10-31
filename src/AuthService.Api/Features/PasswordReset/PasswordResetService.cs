using AuthService.Api.Common;
using AuthService.Api.Email;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.PasswordReset;

public enum ResetPasswordResult
{
    Reset,

    /// <summary>Unknown, expired or already-used link. Deliberately not told apart.</summary>
    InvalidOrExpired,
}

/// <summary>TEN-10: lets someone who forgot their password choose a new one through an emailed single-use link.</summary>
public sealed class PasswordResetService(
    AuthDbContext db,
    EmailOutbox outbox,
    IPasswordHasher<User> passwordHasher,
    IOptions<PasswordResetOptions> options,
    TimeProvider timeProvider,
    ILogger<PasswordResetService> logger)
{
    /// <summary>
    /// Sends a reset link if the email belongs to an account, and does nothing otherwise. It never says which, so it cannot
    /// be used to find out whether an address is registered. The email itself is sent later by the background worker.
    /// </summary>
    public async Task RequestAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Email == normalizedEmail)
            .Select(candidate => new { candidate.Id, candidate.Email })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var since = now - TimeSpan.FromSeconds(options.Value.RequestCooldownSeconds);
        if (await db.PasswordResetTokens.AnyAsync(token => token.UserId == user.Id && token.CreatedAt > since, cancellationToken))
        {
            return;
        }

        var token = SecretToken.NewValue();

        db.PasswordResetTokens.Add(new PasswordResetToken
        {
            Id = Guid.CreateVersion7(),
            UserId = user.Id,
            TokenHash = SecretToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(options.Value.TokenMinutes),
        });

        // Queued in the same save, so the email exists only if its token does.
        outbox.Enqueue(EmailTemplate.PasswordReset, user.Email, new Dictionary<string, string> { ["token"] = token });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ResetPasswordResult> ResetAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = SecretToken.Hash(token);

        var stored = await db.PasswordResetTokens.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);

        if (stored is null || stored.UsedAt is not null || stored.ExpiresAt <= now)
        {
            return ResetPasswordResult.InvalidOrExpired;
        }

        // The slow password hash is computed before the transaction, so locks are held only briefly.
        var user = await db.Users.SingleAsync(candidate => candidate.Id == stored.UserId, cancellationToken);
        var newHash = passwordHasher.HashPassword(user, newPassword);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the link with one conditional update. If it is used twice at the same moment, the database lets one request
        // change the row; the other sees zero rows and is told the link is no longer valid.
        var claimed = await db.PasswordResetTokens
            .Where(candidate => candidate.Id == stored.Id && candidate.UsedAt == null && candidate.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UsedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return ResetPasswordResult.InvalidOrExpired;
        }

        user.PasswordHash = newHash;

        // A reset ends any run of wrong passwords and any lockout (TEN-12).
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        await db.SaveChangesAsync(cancellationToken);

        // Whoever knew the old password, or held a session, is signed out everywhere.
        await db.RevokeAllRefreshTokensAsync(user.Id, now, cancellationToken);

        // Any other reset link sent to this account is no longer needed.
        await db.PasswordResetTokens
            .Where(candidate => candidate.UserId == user.Id && candidate.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UsedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // By user id, never by email. The audit log (AUD-01) will record this event too once it exists.
        logger.LogInformation("Password reset completed for account {UserId}; all its sessions were revoked.", user.Id);
        return ResetPasswordResult.Reset;
    }
}
