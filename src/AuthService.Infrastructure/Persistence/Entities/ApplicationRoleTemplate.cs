namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A role a product ships with, such as "Marketer" (ONB-04). It is not a role of any organisation: when an organisation subscribes to the
/// product, each template becomes an ordinary custom role in that organisation (a copy, with the same name and grants), which its admins
/// then edit freely. Changing a template later does not change copies that were made, and a copy is never overwritten.
/// </summary>
public sealed class ApplicationRoleTemplate
{
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>Unique within its product. Becomes the name of the role in an organisation, so it follows the same length limit.</summary>
    public required string Name { get; set; }

    /// <summary>What the role is for, shown to the platform admin; the copy in an organisation has only a name.</summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One permission of the product's catalogue that a role template grants, at a level (never a yes or no).</summary>
public sealed class ApplicationRoleTemplatePermission
{
    public Guid Id { get; set; }

    public Guid TemplateId { get; set; }

    public Guid PermissionId { get; set; }

    public AccessLevel Level { get; set; }
}
