namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>One permission an API key may use, at a level (KEY-01). Like a role's grant, it refers to the permission by id.</summary>
public sealed class ApiKeyPermission
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid ApiKeyId { get; set; }

    public Guid PermissionId { get; set; }

    public AccessLevel Level { get; set; }
}
