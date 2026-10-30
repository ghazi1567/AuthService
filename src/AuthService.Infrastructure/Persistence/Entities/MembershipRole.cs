namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// Gives a membership a role. <see cref="TenantId"/> must equal the tenant of both the membership and the role;
/// the database enforces that, so a role from another tenant can never be assigned.
/// </summary>
public sealed class MembershipRole
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MembershipId { get; set; }

    public Guid RoleId { get; set; }

    public DateTimeOffset AssignedAt { get; set; }
}
