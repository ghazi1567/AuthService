using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Logout;

/// <summary>
/// TEN-04: ends the login session a refresh token belongs to. Only that session ends; the same person's sessions on other
/// devices are separate token families and keep working. Access tokens already issued simply expire on their own.
/// </summary>
public sealed class LogoutService(AuthDbContext db, TimeProvider timeProvider)
{
    /// <summary>
    /// Safe to repeat, and silent about unknown tokens, so the endpoint cannot be used to find out which tokens exist.
    /// </summary>
    public async Task LogoutAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var hash = IdentityTokenService.Hash(refreshToken);

        var familyId = await db.RefreshTokens.AsNoTracking()
            .Where(token => token.TokenHash == hash)
            .Select(token => (Guid?)token.FamilyId)
            .SingleOrDefaultAsync(cancellationToken);

        if (familyId is null)
        {
            return;
        }

        // The whole family, not just this token: after rotation a family has one live token, so this is the same thing
        // in the normal case, and it still ends the session if a client presents an older token from the same login.
        await db.RevokeRefreshTokenFamilyAsync(familyId.Value, timeProvider.GetUtcNow(), cancellationToken);
    }
}
