namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>Joins a <see cref="User"/> to a <see cref="Tenant"/>. Roles and product access attach to this, not to the user.</summary>
public sealed class TenantMembership
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public MembershipStatus Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
