namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A credential for a tenant's own backend to call a product, without a person logging in (KEY-01). It belongs to one tenant and is
/// limited to the scope it was created with: nothing, or one product and a set of its permissions. The raw key is shown once;
/// only a salted hash is stored. Its public part (<see cref="KeyId"/>) finds the row, and the secret part is checked against the hash.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The one product the key may be used with. Null for a key that only identifies the tenant and grants nothing.</summary>
    public Guid? ApplicationId { get; set; }

    /// <summary>A label the tenant gives it, such as "Billing backend", to tell keys apart.</summary>
    public required string Name { get; set; }

    /// <summary>The public, non-secret part of the raw key (24 lowercase hex characters). Safe to show; unique.</summary>
    public required string KeyId { get; set; }

    /// <summary>The random salt mixed into the hash of the secret part, base64.</summary>
    public required string SecretSalt { get; set; }

    /// <summary>SHA-256 of the salt followed by the secret part, base64. The secret is long and random, so a fast hash is enough.</summary>
    public required string SecretHash { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The key stops working at this time. Null for a key that does not expire; set on a key being rotated out.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>Set when the key is revoked. A revoked key is refused at once and for good.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
