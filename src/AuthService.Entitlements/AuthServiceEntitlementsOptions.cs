namespace AuthService.Entitlements;

/// <summary>Settings of <c>AddAuthServiceEntitlements</c>. Bind them from configuration, such as the section <c>AuthService:Entitlements</c>.</summary>
public sealed class AuthServiceEntitlementsOptions
{
    /// <summary>
    /// The product's webhook signing secrets: the one shown when its callback was registered, and during a rotation the previous one too
    /// (any one matching is enough). Required to receive webhooks.
    /// </summary>
    public IList<string> WebhookSecrets { get; } = [];

    /// <summary>How far a webhook's signed time may be from now, either way. Five minutes.</summary>
    public TimeSpan SignatureTolerance { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the product's limit definitions (for defaults) are kept before they are read again. Ten minutes.</summary>
    public TimeSpan LimitCacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long an event id is remembered, to drop a second delivery of the same event. 24 hours, the Auth Service's retry window.</summary>
    public TimeSpan DuplicateWindow { get; set; } = TimeSpan.FromHours(24);

    /// <summary>The largest webhook body accepted, in bytes. 256 KB.</summary>
    public int MaxBodyBytes { get; set; } = 256 * 1024;

    /// <summary>
    /// Called for every new event after the store has been updated, such as to react to a cancellation. Keep it quick: the Auth Service
    /// waits for the answer. An exception makes the answer an error, so the Auth Service sends the event again later.
    /// </summary>
    public Func<AuthWebhookEvent, IServiceProvider, CancellationToken, Task>? OnEvent { get; set; }
}
