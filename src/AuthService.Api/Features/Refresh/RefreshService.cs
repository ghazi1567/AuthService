using AuthService.Api.Features.Accounts;
using AuthService.Api.Features.Audit;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Refresh;

public abstract record RefreshResult
{
    public sealed record Succeeded(RefreshResponse Response) : RefreshResult;

    /// <summary>Unknown, expired, revoked or already-used token. Deliberately not told apart.</summary>
    public sealed record InvalidRefreshToken : RefreshResult;

    public sealed record AccountDisabled : RefreshResult;
}

/// <summary>
/// TEN-03: exchanges a refresh token for a new access token and the next refresh token. Rotation is always on:
/// every token works once, and presenting a spent token revokes the whole family it belongs to (a replayed token
/// suggests it was stolen).
/// </summary>
public sealed class RefreshService(AuthDbContext db, IdentityTokenService tokens, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = IdentityTokenService.Hash(refreshToken);

        var stored = await db.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return new RefreshResult.InvalidRefreshToken();
        }

        // A spent or revoked token is being presented again: treat it as theft and end the whole session family.
        if (stored.RevokedAt is not null)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await RecordReuseAsync(stored.UserId, cancellationToken);
            return new RefreshResult.InvalidRefreshToken();
        }

        if (stored.ExpiresAt <= now)
        {
            return new RefreshResult.InvalidRefreshToken();
        }

        // Without this, someone whose access was removed could keep a session alive indefinitely by refreshing.
        var statuses = await db.TenantMemberships
            .Where(membership => membership.UserId == stored.UserId)
            .Select(membership => membership.Status)
            .ToListAsync(cancellationToken);
        if (AccountAccess.IsDisabled(statuses))
        {
            return new RefreshResult.AccountDisabled();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Claim the token with one conditional update. If two requests present the same token at the same moment,
        // the database lets exactly one of them change the row; the other sees zero rows and is treated as a replay.
        var claimed = await db.RefreshTokens
            .Where(token => token.Id == stored.Id && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);

        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            await RecordReuseAsync(stored.UserId, cancellationToken);
            return new RefreshResult.InvalidRefreshToken();
        }

        var next = tokens.CreateRefreshToken(stored.UserId, stored.FamilyId, now);
        db.RefreshTokens.Add(next.Entity);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var accessToken = tokens.CreateAccessToken(stored.UserId, now);

        return new RefreshResult.Succeeded(new RefreshResponse(
            accessToken.Value,
            "Bearer",
            (int)(accessToken.ExpiresAt - now).TotalSeconds,
            next.Value));
    }

    /// <summary>A spent token being presented again suggests it was stolen, so it is worth a trace.</summary>
    private async Task RecordReuseAsync(Guid userId, CancellationToken cancellationToken)
    {
        audit.Record(AuditActions.RefreshTokenReuseDetected, AuditActor.User(userId));
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken) =>
        db.RevokeRefreshTokenFamilyAsync(familyId, now, cancellationToken);
}
