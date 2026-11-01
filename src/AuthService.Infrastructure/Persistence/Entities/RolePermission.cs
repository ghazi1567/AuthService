namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// One permission a role grants, at a specific <see cref="AccessLevel"/> (ROL-01). A grant is a level, never a yes or no. The
/// permission is referred to by its id, which stays the same when a product re-registers its catalog (APP-02), so a grant survives
/// a relabel.
/// </summary>
public sealed class RolePermission
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid RoleId { get; set; }

    public Guid PermissionId { get; set; }

    public AccessLevel Level { get; set; }
}
