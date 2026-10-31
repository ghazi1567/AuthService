namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A client secret of a product (APP-05). Only its hash is stored, so a database leak reveals no secret. A product can have
/// more than one at a time: when a platform admin rotates the secret, the old one keeps working for a grace period.
/// </summary>
public sealed class ApplicationSecret
{
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>SHA-256 of the secret, as 64 uppercase hex characters. The secret is random and long, so a fast hash is enough.</summary>
    public required string SecretHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The secret stops working at this time. Null for the current secret, set on a secret being rotated out.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
