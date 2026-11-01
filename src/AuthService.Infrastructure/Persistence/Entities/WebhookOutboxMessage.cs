namespace AuthService.Infrastructure.Persistence.Entities;

public enum WebhookStatus
{
    Pending,
    Delivered,
    Failed,
}

/// <summary>
/// An event to tell a product about, such as a tenant subscribing (SUB-01, SUB-02). It is written in the same transaction as the
/// change that caused it, so the event exists if and only if the change does. Delivering it, signed and with retries, is SUB-06.
/// </summary>
public sealed class WebhookOutboxMessage
{
    /// <summary>The event id sent to the product, so it can discard a duplicate (delivery is at-least-once).</summary>
    public Guid Id { get; set; }

    public Guid ApplicationId { get; set; }

    /// <summary>For example <c>ApplicationSubscribed</c>.</summary>
    public required string EventType { get; set; }

    public required string PayloadJson { get; set; }

    public WebhookStatus Status { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    /// Delivery is retried until this time (24 hours after the event, or after a resend) and then given up on as Failed. The last
    /// attempt is made at the deadline itself.
    /// </summary>
    public DateTimeOffset DeadlineAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    /// <summary>What went wrong last time, as a short code such as <c>HTTP 503</c>. Never a response body or the product's URL.</summary>
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
