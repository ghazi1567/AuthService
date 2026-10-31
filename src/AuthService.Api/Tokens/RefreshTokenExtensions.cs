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
}
