namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A per-tenant exception to one limit (ENT-03): this tenant gets this value whatever its plan says. A reason is required, because someone will
/// later ask why one customer has more than their plan.
/// </summary>
public sealed class SubscriptionOverride
{
    public Guid Id { get; set; }

    public Guid SubscriptionId { get; set; }

    public Guid LimitDefinitionId { get; set; }

    /// <summary>Null means unlimited.</summary>
    public long? Value { get; set; }

    /// <summary>Free text, up to 200 characters. Kept here and in the entitlement history, never in the audit log (decision 0013).</summary>
    public required string Reason { get; set; }

    public AuditActorType SetByType { get; set; }

    public Guid? SetById { get; set; }

    public DateTimeOffset SetAt { get; set; }
}

/// <summary>
/// One change to plans and entitlements, for the platform admin's history (ENT-03): who, when, what, the value before and after, and the reason.
/// Unlike the audit log it may hold the free-text reason of an override, and it is only ever shown to platform admins.
/// </summary>
public sealed class EntitlementChange
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>The tenant, for a change to one subscription (plan assigned, override set or cleared); null for a change to a limit or a plan.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The plan concerned, by key as it was then: the plan assigned, or the plan edited.</summary>
    public string? PlanKey { get; set; }

    /// <summary>A short code: limit_added, limit_changed, plan_created, plan_updated, plan_activated, plan_deactivated, plan_deleted, plan_assigned, override_set, override_cleared.</summary>
    public required string Change { get; set; }

    /// <summary>The limit concerned, for limit and override changes.</summary>
    public string? LimitKey { get; set; }

    /// <summary>The value before, as JSON (a number, null, or for a plan its values object); null when there was nothing before.</summary>
    public string? OldValue { get; set; }

    public string? NewValue { get; set; }

    public string? Reason { get; set; }

    public AuditActorType ActorType { get; set; }

    public Guid? ActorId { get; set; }
}
