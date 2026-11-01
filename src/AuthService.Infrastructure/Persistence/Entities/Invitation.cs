namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// A single-use link that lets an invited person join a tenant (TEN-08). It belongs to an Invited membership; accepting it makes
/// the membership Active. Only its hash is stored, so a database leak cannot be replayed as an acceptance.
/// </summary>
public sealed class Invitation
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid MembershipId { get; set; }

    /// <summary>The person who sent the invitation.</summary>
    public Guid InvitedByUserId { get; set; }

    /// <summary>SHA-256 of the token, as 64 uppercase hex characters.</summary>
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set when the invitation is accepted. Null while it can still be used.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
