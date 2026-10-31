namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A single-use link that lets someone who forgot their password choose a new one (TEN-10). Only its hash is stored, so a
/// database leak cannot be replayed as a password reset.
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the token, as 64 uppercase hex characters.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    /// Set when the token has been used, or when the password was reset with another link and this one is no longer
    /// needed. Null while it can still be used.
    /// </summary>
    public DateTimeOffset? UsedAt { get; set; }
}
