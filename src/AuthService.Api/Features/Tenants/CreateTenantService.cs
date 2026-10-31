using AuthService.Api.Features.Accounts;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Tenants;

public abstract record CreateTenantResult
{
    public sealed record Created(CreateTenantResponse Response) : CreateTenantResult;

    /// <summary>The token is valid but its user no longer exists.</summary>
    public sealed record UserNotFound : CreateTenantResult;

    public sealed record AccountDisabled : CreateTenantResult;
}

/// <summary>
/// TEN-09: lets a logged-in person create another tenant and become its Owner, without signing up again. The tenant, its
/// Owner role and the person's Active membership are saved in one transaction. Their existing memberships are untouched.
/// </summary>
public sealed class CreateTenantService(AuthDbContext db, TimeProvider timeProvider)
{
    public async Task<CreateTenantResult> CreateAsync(Guid userId, string tenantName, CancellationToken cancellationToken)
    {
        if (!await db.Users.AnyAsync(user => user.Id == userId, cancellationToken))
        {
            return new CreateTenantResult.UserNotFound();
        }

        // Without this, a person whose access was removed could give themselves a fresh Active membership with a token
        // issued before they were disabled, and so undo the disabling.
        var statuses = await db.TenantMemberships
            .Where(membership => membership.UserId == userId)
            .Select(membership => membership.Status)
            .ToListAsync(cancellationToken);
        if (AccountAccess.IsDisabled(statuses))
        {
            return new CreateTenantResult.AccountDisabled();
        }

        var provisioned = db.AddTenantWithOwner(userId, tenantName, timeProvider.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return new CreateTenantResult.Created(
            new CreateTenantResponse(provisioned.Tenant.Id, provisioned.Tenant.Name, provisioned.Membership.Id));
    }
}
