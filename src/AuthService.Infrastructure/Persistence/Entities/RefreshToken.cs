namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A refresh token issued at login. Only its hash is stored, so a database leak cannot be replayed as a session.
/// <see cref="FamilyId"/> groups every token descended from one login, so rotation (TEN-03) can revoke the whole
/// family if an old token is reused.
/// </summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid FamilyId { get; set; }

    /// <summary>SHA-256 of the token, as 64 uppercase hex characters.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the token is revoked (logout, rotation or reuse detection). Null while it is usable.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}
