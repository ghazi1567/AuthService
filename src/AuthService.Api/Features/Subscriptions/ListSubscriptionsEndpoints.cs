using System.Security.Claims;
using AuthService.Api.Common;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

/// <summary>One of the caller's tenant's subscriptions, with the product it is for and how many seats are used.</summary>
public sealed record TenantSubscriptionItem(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    string Status,
    int? MaxSeats,
    int SeatsUsed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>SUB-03: a tenant lists its own subscriptions.</summary>
public static class ListSubscriptionsEndpoints
{
    public const string Route = "/api/auth/tenants/me/subscriptions";

    public static IEndpointRouteBuilder MapListSubscriptions(this IEndpointRouteBuilder app)
    {
        app.MapGet(Route, ListAsync).RequireTenantPermission(PermissionKeys.SubscriptionsRead, AccessLevel.ReadOnly);
        return app;
    }

    private static async Task<IResult> ListAsync(ClaimsPrincipal caller, AuthDbContext db, CancellationToken cancellationToken)
    {
        // The tenant is the token's `tid` and nothing else: "me" is not a placeholder for an id the client could change.
        var tenantId = caller.GetTenantId() ?? throw new InvalidOperationException("The tenant-context policy guarantees a tenant id.");

        // Every status, so the tenant can see a subscription that was suspended or cancelled. Disabled members use no seat.
        var items = await (
            from subscription in db.Subscriptions
            join application in db.Applications on subscription.ApplicationId equals application.Id
            where subscription.TenantId == tenantId && !application.IsBuiltIn
            orderby application.Name, application.Key
            select new
            {
                subscription.ApplicationId,
                application.Key,
                application.Name,
                subscription.Status,
                subscription.MaxSeats,
                SeatsUsed = (
                    from access in db.ApplicationAccess
                    join membership in db.TenantMemberships on new { access.TenantId, access.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
                    where access.TenantId == tenantId && access.ApplicationId == subscription.ApplicationId
                        && membership.Status != MembershipStatus.Disabled
                    select access.Id).Count(),
                subscription.CreatedAt,
                subscription.UpdatedAt,
            })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return Results.Ok(items.Select(item => new TenantSubscriptionItem(
            item.ApplicationId, item.Key, item.Name, item.Status.ToString(), item.MaxSeats, item.SeatsUsed, item.CreatedAt, item.UpdatedAt)));
    }
}
