namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>A named set of permissions defined by one tenant. Never global.</summary>
public sealed class Role
{
    /// <summary>Name of the role created automatically for every new tenant's first member.</summary>
    public const string OwnerName = "Owner";

    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>Unique per tenant.</summary>
    public required string Name { get; set; }

    /// <summary>True only for the auto-created Owner role, which can never be edited, downgraded or deleted.</summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
