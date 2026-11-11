using System.Text.Json;

namespace AuthService.Entitlements;

/// <summary>An event the Auth Service sent to the product. Drop one whose <see cref="EventId"/> you have already handled.</summary>
/// <param name="EventId">The event's id; the same event delivered twice has the same id.</param>
/// <param name="EventType">The event's type, such as <c>EntitlementsChanged</c>.</param>
/// <param name="OccurredAt">When it happened.</param>
/// <param name="SchemaVersion">The version of the event's shape. Events from before ENT-04 have none and read as 1.</param>
/// <param name="TenantId">The organisation it is about.</param>
/// <param name="ApplicationId">The product.</param>
public abstract record AuthWebhookEvent(Guid EventId, string EventType, DateTimeOffset OccurredAt, int SchemaVersion, Guid TenantId, Guid ApplicationId);

/// <summary>An organisation subscribed, or came back. Carries its entitlements when the product has limits.</summary>
public sealed record ApplicationSubscribedEvent(
    Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, Guid TenantId, Guid ApplicationId, int? MaxSeats, EntitlementSnapshot? Entitlements)
    : AuthWebhookEvent(EventId, AuthWebhookEventTypes.ApplicationSubscribed, OccurredAt, SchemaVersion, TenantId, ApplicationId);

/// <summary>An organisation's subscription was suspended or cancelled (<see cref="Status"/>).</summary>
public sealed record ApplicationUnsubscribedEvent(
    Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, Guid TenantId, Guid ApplicationId, string Status)
    : AuthWebhookEvent(EventId, AuthWebhookEventTypes.ApplicationUnsubscribed, OccurredAt, SchemaVersion, TenantId, ApplicationId);

/// <summary>An organisation's entitlements changed: the complete set at the new version.</summary>
public sealed record EntitlementsChangedEvent(
    Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, Guid TenantId, Guid ApplicationId, EntitlementSnapshot Entitlements)
    : AuthWebhookEvent(EventId, AuthWebhookEventTypes.EntitlementsChanged, OccurredAt, SchemaVersion, TenantId, ApplicationId);

/// <summary>An event type this version of the package does not know. Acknowledge it; a newer package may handle it.</summary>
public sealed record UnknownAuthWebhookEvent(
    Guid EventId, string EventType, DateTimeOffset OccurredAt, int SchemaVersion, Guid TenantId, Guid ApplicationId, JsonElement Payload)
    : AuthWebhookEvent(EventId, EventType, OccurredAt, SchemaVersion, TenantId, ApplicationId);

/// <summary>The event types the Auth Service sends.</summary>
public static class AuthWebhookEventTypes
{
    /// <summary>An organisation subscribed.</summary>
    public const string ApplicationSubscribed = "ApplicationSubscribed";

    /// <summary>A subscription was suspended or cancelled.</summary>
    public const string ApplicationUnsubscribed = "ApplicationUnsubscribed";

    /// <summary>An organisation's entitlements changed.</summary>
    public const string EntitlementsChanged = "EntitlementsChanged";
}

/// <summary>A webhook body that is not an Auth Service event this package can read.</summary>
public sealed class AuthWebhookException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads an Auth Service event from its JSON body. Fields it does not know are ignored, so new optional fields never break it. A
/// <c>schemaVersion</c> above <see cref="SupportedSchemaVersion"/> means a shape this package cannot read and is refused.
/// </summary>
public static class AuthWebhookParser
{
    /// <summary>The highest schema version this package reads.</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>Reads the event in <paramref name="body"/>.</summary>
    /// <exception cref="AuthWebhookException">The body is not an event, misses a required field, or has a newer schema version.</exception>
    public static AuthWebhookEvent Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException exception)
        {
            throw new AuthWebhookException("The webhook body is not JSON.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new AuthWebhookException("The webhook body is not a JSON object.");
            }

            var schemaVersion = root.TryGetProperty("schemaVersion", out var schema) && schema.ValueKind == JsonValueKind.Number ? schema.GetInt32() : 1;
            if (schemaVersion > SupportedSchemaVersion)
            {
                throw new AuthWebhookException($"The event has schema version {schemaVersion}; this package reads up to {SupportedSchemaVersion}. Update AuthService.Entitlements.");
            }

            var eventId = RequiredGuid(root, "eventId");
            var eventType = root.TryGetProperty("eventType", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : throw Missing("eventType");
            var occurredAt = root.TryGetProperty("occurredAt", out var occurred) && occurred.TryGetDateTimeOffset(out var at) ? at : throw Missing("occurredAt");
            var tenantId = RequiredGuid(root, "tenantId");
            var applicationId = RequiredGuid(root, "applicationId");

            return eventType switch
            {
                AuthWebhookEventTypes.EntitlementsChanged => new EntitlementsChangedEvent(
                    eventId, occurredAt, schemaVersion, tenantId, applicationId, ReadSnapshot(root, tenantId, applicationId) ?? throw Missing("entitlements")),
                AuthWebhookEventTypes.ApplicationSubscribed => new ApplicationSubscribedEvent(
                    eventId, occurredAt, schemaVersion, tenantId, applicationId,
                    root.TryGetProperty("maxSeats", out var seats) && seats.ValueKind == JsonValueKind.Number ? seats.GetInt32() : null,
                    ReadSnapshot(root, tenantId, applicationId, "Active")),
                AuthWebhookEventTypes.ApplicationUnsubscribed => new ApplicationUnsubscribedEvent(
                    eventId, occurredAt, schemaVersion, tenantId, applicationId,
                    root.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String ? status.GetString()! : throw Missing("status")),
                _ => new UnknownAuthWebhookEvent(eventId, eventType, occurredAt, schemaVersion, tenantId, applicationId, root.Clone()),
            };
        }
    }

    /// <summary>
    /// The entitlement fields of an event or a read (<c>planKey</c>, <c>planName</c>, <c>version</c>, <c>entitlements</c>, <c>overridden</c>), or
    /// null when the object has no <c>entitlements</c> (an <c>ApplicationSubscribed</c> for a product without limits, or from before ENT-04).
    /// </summary>
    internal static EntitlementSnapshot? ReadSnapshot(JsonElement root, Guid tenantId, Guid applicationId, string? status = null, long? changeNumber = null)
    {
        if (!root.TryGetProperty("entitlements", out var set) || set.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var limits = new Dictionary<string, LimitValue>(StringComparer.Ordinal);
        foreach (var limit in set.EnumerateObject())
        {
            limits[limit.Name] = limit.Value.ValueKind switch
            {
                JsonValueKind.Null => LimitValue.Unlimited,
                JsonValueKind.Number when limit.Value.TryGetInt64(out var number) => new LimitValue(number),
                _ => throw new AuthWebhookException($"The limit '{limit.Name}' is neither a whole number nor null."),
            };
        }

        var overridden = root.TryGetProperty("overridden", out var keys) && keys.ValueKind == JsonValueKind.Array
            ? keys.EnumerateArray().Where(key => key.ValueKind == JsonValueKind.String).Select(key => key.GetString()!).ToArray()
            : [];
        var version = root.TryGetProperty("version", out var versionElement) && versionElement.TryGetInt64(out var number2) ? number2 : throw Missing("version");
        if (root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
        {
            status = statusElement.GetString();
        }

        return new EntitlementSnapshot(tenantId, applicationId, OptionalString(root, "planKey"), OptionalString(root, "planName"), version, limits, overridden, status, changeNumber);
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid RequiredGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : throw Missing(name);

    private static AuthWebhookException Missing(string name) => new($"The event has no valid '{name}'.");
}
