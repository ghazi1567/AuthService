using AuthService.Api.Common;
using AuthService.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Tokens;

/// <summary>The caller must be a platform admin (APP-04).</summary>
public sealed class PlatformAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Checks the platform-admin flag in the database on every request. The flag is deliberately not a claim in the token: that way
/// granting or revoking it takes effect at once, and no token, however it was made, can carry the privilege. The flag grants
/// access to the platform's own operations only; it opens no tenant's data.
/// </summary>
public sealed class PlatformAdminHandler(AuthDbContext db) : AuthorizationHandler<PlatformAdminRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PlatformAdminRequirement requirement)
    {
        if (context.User.GetUserId() is not { } userId)
        {
            return;
        }

        var isPlatformAdmin = await db.Users.AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.IsPlatformAdmin)
            .SingleOrDefaultAsync();

        if (isPlatformAdmin)
        {
            context.Succeed(requirement);
        }
    }
}
