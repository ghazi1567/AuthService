namespace AuthService.Infrastructure.Persistence.Entities;

/// <summary>
/// How many times one thing (an address, a person) has used one limited endpoint in its current window (PLT-04). Shared by every
/// instance of the service, so a limit means the same with one instance or ten. The key is stored as a hash, so no address or id
/// is kept; a row is replaced when its window ends and deleted shortly after.
/// </summary>
public sealed class RateLimitCounter
{
    /// <summary>SHA-256 of the policy and the caller (base64url, 43 characters).</summary>
    public required string KeyHash { get; set; }

    /// <summary>Uses in the current window, including the ones that were refused.</summary>
    public long Hits { get; set; }

    /// <summary>When the window ends and the count starts again.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
