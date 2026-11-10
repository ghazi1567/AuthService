namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A named number a product can limit, such as <c>contacts.reachable</c> (ENT-01). The product owns what it means and counts its own
/// usage; the Auth Service only knows the key, a label, a display unit and the value a tenant gets when nothing else says otherwise.
/// Like a permission, a limit is never deleted or renamed: plans and overrides refer to it by key.
/// </summary>
public sealed class LimitDefinition
{
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Unique within its product, lowercase.</summary>
    public required string Key { get; set; }

    /// <summary>Short human-readable name shown in the portal, such as "Reachable contacts".</summary>
    public required string Label { get; set; }

    public string? Description { get; set; }

    /// <summary>Display text only, such as "contacts" or "MB"; never used in a calculation. <c>flag</c> marks an on/off feature (0 or 1).</summary>
    public string? Unit { get; set; }

    /// <summary>The value a tenant gets when its plan does not set this limit and it has no override. Null means unlimited.</summary>
    public long? DefaultValue { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
