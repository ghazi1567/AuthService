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
    /// The memberships that use a seat at <paramref name="now"/>: an Active one, or an Invited one unless its invitation has expired
    /// (never accepted and past its lifetime). A Disabled member keeps the access row, so re-enabling can restore it, but does not use a
    /// seat; neither does an expired invitation. Nothing has to run to free that seat: the count simply stops including it, and
    /// resending the invitation takes the seat again (and is refused when the product is full). One definition, so every count agrees.
    /// An Invited membership with no invitation at all cannot be created through the API; it keeps its seat, as it always did.
    /// </summary>
    public static IQueryable<TenantMembership> SeatHolders(this AuthDbContext db, DateTimeOffset now) =>
        db.TenantMemberships.Where(membership =>
            membership.Status == MembershipStatus.Active
            || (membership.Status == MembershipStatus.Invited
                && !db.Invitations.Any(invitation => invitation.TenantId == membership.TenantId && invitation.MembershipId == membership.Id
                    && invitation.UsedAt == null && invitation.ExpiresAt <= now)));

    /// <summary>Seats in use: members of the tenant with access to the product whose membership holds a seat (see <see cref="SeatHolders"/>).</summary>
    public static Task<int> SeatsUsedAsync(this AuthDbContext db, Guid tenantId, Guid applicationId, DateTimeOffset now, CancellationToken cancellationToken) =>
        (from access in db.ApplicationAccess
         join membership in db.SeatHolders(now) on new { access.TenantId, access.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
         where access.TenantId == tenantId && access.ApplicationId == applicationId
         select access.Id)
        .CountAsync(cancellationToken);

    /// <summary>The subscription as a caller sees it, with seats counted from the database so the response is what is really stored.</summary>
    public static async Task<SubscriptionResponse> ToResponseAsync(
        this AuthDbContext db, TenantApplicationSubscription subscription, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var seatsUsed = await db.SeatsUsedAsync(subscription.TenantId, subscription.ApplicationId, now, cancellationToken);

        return new SubscriptionResponse(
            subscription.TenantId, subscription.ApplicationId, subscription.Status.ToString(), subscription.MaxSeats, seatsUsed,
            subscription.CreatedAt, subscription.UpdatedAt);
    }
}
