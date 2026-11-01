using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

public static class SubscriptionResponses
{
    /// <summary>
    /// Makes changes to one tenant's subscription to one product take turns, until the surrounding transaction ends. Call it
    /// inside a transaction, before reading the subscription.
    /// </summary>
    public static Task LockSubscriptionAsync(this AuthDbContext db, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({"subscription:" + tenantId + ":" + applicationId}, 0))", cancellationToken);

    /// <summary>
    /// Seats in use: members of the tenant with access to the product whose membership is Invited or Active. A Disabled member
    /// keeps the access row, so re-enabling can restore it, but does not use a seat.
    /// </summary>
    public static Task<int> SeatsUsedAsync(this AuthDbContext db, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
        (from access in db.ApplicationAccess
         join membership in db.TenantMemberships on new { access.TenantId, access.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
         where access.TenantId == tenantId && access.ApplicationId == applicationId && membership.Status != MembershipStatus.Disabled
         select access.Id)
        .CountAsync(cancellationToken);

    /// <summary>The subscription as a caller sees it, with seats counted from the database so the response is what is really stored.</summary>
    public static async Task<SubscriptionResponse> ToResponseAsync(
        this AuthDbContext db, TenantApplicationSubscription subscription, CancellationToken cancellationToken)
    {
        var seatsUsed = await db.SeatsUsedAsync(subscription.TenantId, subscription.ApplicationId, cancellationToken);

        return new SubscriptionResponse(
            subscription.TenantId, subscription.ApplicationId, subscription.Status.ToString(), subscription.MaxSeats, seatsUsed,
            subscription.CreatedAt, subscription.UpdatedAt);
    }
}
