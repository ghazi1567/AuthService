namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>Who performed an audited action.</summary>
public enum AuditActorType
{
    /// <summary>Nobody could be identified, for example a login attempt for an email that is not registered.</summary>
    Anonymous,

    User,

    /// <summary>A product acting with its client credentials (APP-05).</summary>
    Product,

    ApiKey,

    /// <summary>The service itself, for example a scheduled signing-key rotation.</summary>
    System,
}

/// <summary>
/// One recorded security or admin event: who did what, when, in which tenant and from which address (AUD-01). Entries are
/// append-only, enforced by the database, and never hold a password, token or secret: only codes and ids. They are deleted
/// automatically after the retention period.
/// </summary>
public sealed class AuditEntry
{
    public Guid Id { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>What happened, as a short stable code such as <c>auth.login.succeeded</c>.</summary>
    public required string Action { get; set; }

    public AuditActorType ActorType { get; set; }

    /// <summary>The user, product or API key that acted. Null for anonymous and system actors.</summary>
    public Guid? ActorId { get; set; }

    /// <summary>The tenant the action concerns, if any. Login, for example, concerns no single tenant.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>The caller's IP address, as the service saw it (a reverse proxy's address until forwarded headers are set up).</summary>
    public string? SourceIp { get; set; }

    /// <summary>A short code adding detail, such as <c>wrong_password</c>. Lowercase letters, digits, dots and underscores only.</summary>
    public string? Detail { get; set; }
}
