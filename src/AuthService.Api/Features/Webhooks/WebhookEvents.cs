using System.Text.Json;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Webhooks;

public static class WebhookEvents
{
    public const string ApplicationSubscribed = "ApplicationSubscribed";
    public const string ApplicationUnsubscribed = "ApplicationUnsubscribed";

    /// <summary>
    /// Builds an event for the outbox. The caller adds it to its own context, so it is committed in the same transaction as the
    /// change that caused it. The event id is the outbox id and is also inside the payload, so a product can drop a duplicate: delivery
    /// is at-least-once.
    /// </summary>
    public static WebhookOutboxMessage Create(
        Guid applicationId, string eventType, DateTimeOffset now, int retryWindowHours, Func<Guid, object> payload)
    {
        var eventId = Guid.CreateVersion7();
        return new WebhookOutboxMessage
        {
            Id = eventId,
            ApplicationId = applicationId,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload(eventId)),
            Status = WebhookStatus.Pending,
            NextAttemptAt = now,
            DeadlineAt = now.AddHours(retryWindowHours),
            CreatedAt = now,
        };
    }
}
