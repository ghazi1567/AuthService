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

    /// <summary>
    /// Wrong passwords entered since the last correct one (TEN-12). Reset to zero when the account is locked and on a
    /// correct password.
    /// </summary>
    public int FailedLoginCount { get; set; }

    /// <summary>Login is refused until this time. Null when the account is not locked.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    /// <summary>
    /// Whether this person operates the platform (APP-04): registers products, rotates signing keys, reads the whole audit log.
    /// False for everyone by default and settable only directly in the database: no endpoint and no request field can change it.
    /// It grants no access to any tenant's data.
    /// </summary>
    public bool IsPlatformAdmin { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
