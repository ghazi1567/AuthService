namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A tenant's right to use one product (SUB-01). There is at most one per tenant and product: subscribing again reuses the row,
/// so a tenant that was suspended or cancelled and comes back keeps one history, not many.
/// </summary>
public sealed class TenantApplicationSubscription
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ApplicationId { get; set; }

    public SubscriptionStatus Status { get; set; }

    /// <summary>The most people who can have access to the product at once. Null means unlimited. Set by the product (SUB-04); billing stays in the product.</summary>
    public int? MaxSeats { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
