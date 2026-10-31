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

    /// <summary>The subscription as a caller sees it, with seats counted from the database so the response is what is really stored.</summary>
    public static async Task<SubscriptionResponse> ToResponseAsync(
        this AuthDbContext db, TenantApplicationSubscription subscription, CancellationToken cancellationToken)
    {
        var seatsUsed = await db.ApplicationAccess.CountAsync(
            access => access.TenantId == subscription.TenantId && access.ApplicationId == subscription.ApplicationId, cancellationToken);

        return new SubscriptionResponse(
            subscription.TenantId, subscription.ApplicationId, subscription.Status.ToString(), subscription.MaxSeats, seatsUsed,
            subscription.CreatedAt, subscription.UpdatedAt);
    }
}
