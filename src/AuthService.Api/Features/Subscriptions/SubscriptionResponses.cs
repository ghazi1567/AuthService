using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

public static class SubscriptionResponses
{
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
