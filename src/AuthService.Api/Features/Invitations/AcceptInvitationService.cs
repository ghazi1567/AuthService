using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Invitations;

/// <summary>Body of <c>POST /api/auth/invitations/accept</c>. A password is for a person new to the platform only.</summary>
public sealed record AcceptInvitationRequest(string? Token, string? Password);

public sealed record AcceptInvitationResponse(Guid TenantId, Guid MembershipId);

public abstract record AcceptInvitationResult
{
    public sealed record Accepted(AcceptInvitationResponse Response) : AcceptInvitationResult;

    /// <summary>Unknown, expired or already-used link, or the invitation was withdrawn. Deliberately not told apart.</summary>
    public sealed record Gone : AcceptInvitationResult;

    /// <summary>A new person must choose a password, and it was missing or not acceptable.</summary>
    public sealed record PasswordInvalid(string Reason) : AcceptInvitationResult;

    /// <summary>A person who already has an account keeps their password: accepting never sets or changes one.</summary>
    public sealed record PasswordNotAllowed : AcceptInvitationResult;

    /// <summary>The invited email already has an account, so the person must be logged in to accept.</summary>
    public sealed record LoginRequired : AcceptInvitationResult;

    /// <summary>Logged in as someone other than the invited person.</summary>
    public sealed record WrongUser : AcceptInvitationResult;
}

/// <summary>
/// TEN-08: accepts an invitation. A new person chooses a password and their email counts as verified, since the link proved they
/// own it. An existing user accepts while logged in as the invited email, and keeps their password.
/// </summary>
public sealed class AcceptInvitationService(
    AuthDbContext db,
    IPasswordHasher<User> passwordHasher,
    AuditLog audit,
    TimeProvider timeProvider)
{
    public async Task<AcceptInvitationResult> AcceptAsync(string token, string? password, Guid? callerUserId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = SecretToken.Hash(token);

        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.TokenHash == hash, cancellationToken);
        if (invitation is null || invitation.UsedAt is not null || invitation.ExpiresAt <= now)
        {
            return new AcceptInvitationResult.Gone();
        }

        var membership = await db.TenantMemberships.AsNoTracking()
            .SingleAsync(candidate => candidate.Id == invitation.MembershipId, cancellationToken);
        if (membership.Status != MembershipStatus.Invited)
        {
            return new AcceptInvitationResult.Gone();
        }

        var user = await db.Users.SingleAsync(candidate => candidate.Id == membership.UserId, cancellationToken);
        var isNewPerson = user.PasswordHash is null;

        string? newHash = null;
        if (isNewPerson)
        {
            if (PasswordPolicy.Validate(password) is { } reason)
            {
                return new AcceptInvitationResult.PasswordInvalid(reason);
            }

            // The slow hash is computed before the transaction, so locks are held only briefly.
            newHash = passwordHasher.HashPassword(user, password!);
        }
        else
        {
            // These are checked before the link is used up, so a mistake leaves the invitation as it was.
            if (callerUserId is null)
            {
                return new AcceptInvitationResult.LoginRequired();
            }

            if (callerUserId != user.Id)
            {
                return new AcceptInvitationResult.WrongUser();
            }

            if (password is not null)
            {
                return new AcceptInvitationResult.PasswordNotAllowed();
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the link with one conditional update, so two simultaneous acceptances cannot both succeed.
        var claimed = await db.Invitations
            .Where(candidate => candidate.Id == invitation.Id && candidate.UsedAt == null && candidate.ExpiresAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.UsedAt, now), cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AcceptInvitationResult.Gone();
        }

        // Only an Invited membership becomes Active: one disabled since the invitation was sent stays Disabled.
        var activated = await db.TenantMemberships
            .Where(candidate => candidate.Id == membership.Id && candidate.Status == MembershipStatus.Invited)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, MembershipStatus.Active), cancellationToken);
        if (activated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AcceptInvitationResult.Gone();
        }

        if (isNewPerson)
        {
            user.PasswordHash = newHash;
            user.EmailVerifiedAt ??= now;
        }

        audit.Record(AuditActions.InvitationAccepted, AuditActor.User(user.Id), membership.TenantId, membership.Id.ToString());
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new AcceptInvitationResult.Accepted(new AcceptInvitationResponse(membership.TenantId, membership.Id));
    }
}
