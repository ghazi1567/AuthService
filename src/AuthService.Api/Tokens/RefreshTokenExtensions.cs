using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Tokens;

public static class RefreshTokenExtensions
{
    /// <summary>
    /// Ends one login session: revokes every still-usable refresh token in the family. Safe to repeat. A family belongs to
    /// a single login, so other devices' sessions are untouched.
    /// </summary>
    public static Task<int> RevokeRefreshTokenFamilyAsync(
        this AuthDbContext db,
        Guid familyId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(token => token.FamilyId == familyId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);

    /// <summary>
    /// Ends every login session of one person on every device, for example after a password reset. Safe to repeat.
    /// </summary>
    public static Task<int> RevokeAllRefreshTokensAsync(
        this AuthDbContext db,
        Guid userId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        db.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
}
