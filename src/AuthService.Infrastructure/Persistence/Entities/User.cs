namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A person's global identity: one email and one password across the whole platform, no matter how many
/// tenants they belong to (decision AUTH-117). The tenant is chosen later, at token exchange.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Stored trimmed and lowercased. Unique across the platform, case-insensitively (citext column).</summary>
    public required string Email { get; set; }

    /// <summary>Salted hash, never returned by any API. Null only for a person who has not set a password yet.</summary>
    public string? PasswordHash { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>Null until the email address is verified (TEN-11). A new signup starts unverified.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
