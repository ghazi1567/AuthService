using AuthService.Api.Features.Accounts;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.CurrentUser;

/// <summary>TEN-06: the caller's own profile and memberships.</summary>
public sealed class CurrentUserService(AuthDbContext db)
{
    /// <summary>Null if the user no longer exists, for example a token that outlived a deleted account.</summary>
    public async Task<CurrentUserResponse?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .Where(candidate => candidate.Id == userId)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Email,
                candidate.DisplayName,
                EmailVerified = candidate.EmailVerifiedAt != null,
                candidate.CreatedAt,
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return null;
        }

        var memberships = await db.ListMembershipsAsync(userId, cancellationToken);

        return new CurrentUserResponse(user.Id, user.Email, user.DisplayName, user.EmailVerified, user.CreatedAt, memberships);
    }
}
