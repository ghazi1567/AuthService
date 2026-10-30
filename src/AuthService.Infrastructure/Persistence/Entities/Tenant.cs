namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>A business that uses the platform. People belong to it through <see cref="TenantMembership"/>.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public TenantStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
