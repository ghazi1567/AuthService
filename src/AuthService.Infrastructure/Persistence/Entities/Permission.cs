namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// One controllable action of a product, such as <c>campaign.create</c>, from its permission catalog (APP-02). The product owns
/// and maintains its catalog. A permission is never deleted or recreated: re-registering a key updates the row in place, so
/// roles that grant it keep pointing at the same permission (ROL-01).
/// </summary>
public sealed class Permission
{
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Unique within its product, not across the platform.</summary>
    public required string Key { get; set; }

    /// <summary>Short human-readable name shown when a tenant admin builds a role.</summary>
    public required string Label { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
