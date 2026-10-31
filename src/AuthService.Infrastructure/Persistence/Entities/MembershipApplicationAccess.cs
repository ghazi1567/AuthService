namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A member's access to one product their tenant subscribes to (SUB-05). This is what "can use the product" means, and each row
/// is one seat. What the member may do inside the product is decided separately, by roles.
/// </summary>
public sealed class MembershipApplicationAccess
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MembershipId { get; set; }

    public Guid ApplicationId { get; set; }

    public DateTimeOffset GrantedAt { get; set; }
}
