using System.Text.RegularExpressions;

namespace AuthService.Web.Api;

/// <summary>A product as the platform's lists name it.</summary>
public sealed record ProductSummary(Guid Id, string Key, string Name);

/// <summary>
/// One event the platform is trying to tell a product about. The product's callback address and secret are never part of it.
/// <c>LastError</c> is a short reason such as <c>HTTP 503</c>.
/// </summary>
public sealed record WebhookItem(
    Guid Id,
    Guid ApplicationId,
    string ApplicationKey,
    string EventType,
    string Status,
    int Attempts,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset NextAttemptAt,
    DateTimeOffset DeadlineAt,
    DateTimeOffset? DeliveredAt);

public sealed record WebhookCursorItem(DateTimeOffset Before, Guid BeforeId);

/// <summary>A page of webhook events, newest first, and where the next one starts (null at the end).</summary>
public sealed record WebhookPage(IReadOnlyList<WebhookItem> Items, WebhookCursorItem? Next);

/// <summary>Plain words for the events the platform sends to products.</summary>
public static partial class WebhookLabels
{
    public static string Event(string eventType) => eventType switch
    {
        "ApplicationSubscribed" => "An organisation subscribed",
        "ApplicationUnsubscribed" => "An organisation unsubscribed",
        _ => Humanise(eventType),
    };

    private static string Humanise(string eventType)
    {
        var words = Words().Replace(eventType.Trim(), " ").Trim();
        return words.Length == 0 ? "An event" : char.ToUpperInvariant(words[0]) + words[1..].ToLowerInvariant();
    }

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|[_.-]+")]
    private static partial Regex Words();
}

/// <summary>One public key the platform publishes so products can check its tokens. Only the public parts; the id is what a token's header names.</summary>
public sealed record SigningKeyItem(string Kid, string Kty, string Crv, string Alg, string Use);

/// <summary>The platform's published keys (<c>GET /.well-known/jwks.json</c>).</summary>
public sealed record SigningKeySet(IReadOnlyList<SigningKeyItem> Keys);

/// <summary>The key that signs tokens as of now, after an emergency rotation.</summary>
public sealed record RotatedSigningKey(string KeyId);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/secret/rotate</c>: how long the secret being replaced keeps working (0 to 720 hours).</summary>
public sealed record RotateSecretRequest(int GraceHours);

/// <summary>
/// A product's new client secret, which comes back once and nowhere else. <see cref="PreviousSecretsExpireAt"/> is when the secrets being
/// replaced stop working. Printing it shows only the client id, so the secret can't reach a log by being printed.
/// </summary>
public sealed record RotatedSecret(Guid ApplicationId, string ClientId, string ClientSecret, DateTimeOffset PreviousSecretsExpireAt)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("ClientId = ").Append(ClientId);
        return true;
    }
}
