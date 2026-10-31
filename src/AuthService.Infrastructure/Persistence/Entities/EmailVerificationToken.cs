namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A single-use link that proves a person controls an email address (TEN-11). Only its hash is stored, so a database leak
/// cannot be replayed as a verification.
/// </summary>
public sealed class EmailVerificationToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the token, as 64 uppercase hex characters.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set when the token has been used, or when the address was verified some other way and the token is no longer
    /// needed. Null while it can still be used.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }
}
