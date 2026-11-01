namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A product on the platform, for example WhatsApp SaaS (APP-01). Registered by a platform admin. A product gets its own
/// client credentials to call the Auth Service, and its own audience value that its tokens carry (TOK-01).
/// </summary>
public sealed class Application
{
    public Guid Id { get; set; }

    /// <summary>Short stable name, such as <c>whatsapp-saas</c>. Lowercase letters, digits and hyphens. Unique across the platform.</summary>
    public required string Key { get; set; }

    /// <summary>Display name shown to tenants.</summary>
    public required string Name { get; set; }

    /// <summary>The <c>aud</c> claim of tokens issued for this product. Unique, so no two products can accept each other's tokens.</summary>
    public required string JwtAudience { get; set; }

    /// <summary>Public identifier the product uses with its secret to authenticate to the Auth Service (APP-05).</summary>
    public required string ClientId { get; set; }

    /// <summary>
    /// False until the product's permission catalog is registered (APP-02): a tenant cannot subscribe to a product that has
    /// nothing to grant.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// True for the Auth Service's own product, which tenant admins use to work on their tenant (roles, members, subscriptions).
    /// It is created and kept in step by the service itself, is not offered to tenants to subscribe to, has no client credentials
    /// and cannot be changed through the admin API.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
