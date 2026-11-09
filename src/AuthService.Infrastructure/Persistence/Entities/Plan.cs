namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A named template of limit values for one product, such as "Growth" (ENT-02). A tenant's subscription points at a plan, and each limit
/// the plan sets applies to it unless the tenant has an override; a limit the plan does not set falls back to the limit's default.
/// </summary>
public sealed class Plan
{
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Unique within its product, lowercase, such as <c>growth</c>. Never renamed: subscriptions and events refer to it.</summary>
    public required string Key { get; set; }

    /// <summary>Display name, such as "Growth".</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Whether the plan can be newly assigned. A deactivated plan stays on the subscriptions that already have it.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Set the first time the plan is assigned to a subscription and never cleared. A plan that was ever assigned cannot be deleted, only
    /// deactivated, so the history of what a tenant had always names a plan that still exists.
    /// </summary>
    public bool EverAssigned { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The value a plan gives one limit. Null means unlimited. A limit with no row here takes the limit's default.</summary>
public sealed class PlanValue
{
    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public Guid LimitDefinitionId { get; set; }

    public long? Value { get; set; }
}
