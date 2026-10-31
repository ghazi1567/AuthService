using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Email;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.EmailVerification;

public enum VerifyEmailResult
{
    Verified,

    /// <summary>The address was already verified, so nothing changed. Not an error.</summary>
    AlreadyVerified,

    /// <summary>Unknown, expired or already-used link, for an address that is still unverified.</summary>
    InvalidOrExpired,
}

/// <summary>TEN-11: proves a person controls the email address they signed up with.</summary>
public sealed class EmailVerificationService(
    AuthDbContext db,
    EmailOutbox outbox,
    IOptions<EmailVerificationOptions> options,
    AuditLog audit,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Creates a verification token and queues the email carrying it. It only adds to the database context: the caller's
    /// own <c>SaveChangesAsync</c> commits it with the change that caused it (a signup, a resend request).
    /// </summary>
    public void Issue(Guid userId, string email, DateTimeOffset now)
    {
        var token = SecretToken.NewValue();

        db.EmailVerificationTokens.Add(new EmailVerificationToken
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = SecretToken.Hash(token),
            CreatedAt = now,
            ExpiresAt = now.AddHours(options.Value.TokenHours),
        });

        outbox.Enqueue(EmailTemplate.Verification, email, new Dictionary<string, string> { ["token"] = token });
    }

    public async Task<VerifyEmailResult> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = SecretToken.Hash(token);

        var stored = await db.EmailVerificationTokens.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return VerifyEmailResult.InvalidOrExpired;
        }

        // Opening the link of an address that is already verified is harmless, so it is not an error.
        if (await IsVerifiedAsync(stored.UserId, cancellationToken))
        {
            return VerifyEmailResult.AlreadyVerified;
        }

        if (stored.UsedAt is not null || stored.ExpiresAt <= now)
        {
            return VerifyEmailResult.InvalidOrExpired;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the token with one conditional update. If the same link is opened twice at the same moment, the database
        // lets one request change the row; the other waits, sees zero rows, and finds the address already verified.
        var claimed = await db.EmailVerificationTokens
            .Where(candidate => candidate.Id == stored.Id && candidate.UsedAt == null && candidate.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UsedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return await IsVerifiedAsync(stored.UserId, cancellationToken)
                ? VerifyEmailResult.AlreadyVerified
                : VerifyEmailResult.InvalidOrExpired;
        }

        var verified = await db.Users
            .Where(user => user.Id == stored.UserId && user.EmailVerifiedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(user => user.EmailVerifiedAt, now), cancellationToken);

        if (verified > 0)
        {
            // In the same transaction as the change it records.
            audit.Record(AuditActions.EmailVerified, AuditActor.User(stored.UserId));
            await db.SaveChangesAsync(cancellationToken);
        }

        // Any other link sent to this address is no longer needed.
        await db.EmailVerificationTokens
            .Where(candidate => candidate.UserId == stored.UserId && candidate.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UsedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return VerifyEmailResult.Verified;
    }

    /// <summary>
    /// Sends a new link if the email belongs to an unverified account, and does nothing otherwise. It never says which,
    /// so it cannot be used to find out whether an address is registered.
    /// </summary>
    public async Task ResendAsync(string normalizedEmail, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Email == normalizedEmail)
            .Select(candidate => new { candidate.Id, candidate.Email, Verified = candidate.EmailVerifiedAt != null })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null || user.Verified)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var since = now - TimeSpan.FromSeconds(options.Value.ResendCooldownSeconds);
        if (await db.EmailVerificationTokens.AnyAsync(token => token.UserId == user.Id && token.CreatedAt > since, cancellationToken))
        {
            return;
        }

        // Earlier links stay valid until they expire, so a delayed first email still works.
        Issue(user.Id, user.Email, now);
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<bool> IsVerifiedAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.EmailVerifiedAt != null)
            .SingleAsync(cancellationToken);
}
