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

    /// <summary>The plan the tenant is on for this product (ENT-03), or null for none: then every limit takes its default.</summary>
    public Guid? PlanId { get; set; }

    /// <summary>Rises by one every time the tenant's effective limits change (ENT-03). 0 means they were never computed. A product applies an
    /// entitlement snapshot only when its version is higher than the one it holds.</summary>
    public long EntitlementsVersion { get; set; }

    /// <summary>A number from one sequence for the whole service, taken each time the version rises, so a product can page through every
    /// tenant in change order and ask for what changed since a point (ENT-05).</summary>
    public long EntitlementsChangeSeq { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
