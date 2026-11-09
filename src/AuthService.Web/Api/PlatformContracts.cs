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

/// <summary>A product as the platform admin's list shows it: where it stands, and nothing it keeps private.</summary>
public sealed record AdminProductItem(Guid Id, string Key, string Name, bool IsActive, int PermissionCount, string? LaunchUrl, DateTimeOffset CreatedAt);

/// <summary>
/// Body of <c>POST /api/auth/admin/applications</c>. The launch address, the permissions and the wish to make the product available are
/// optional (ONB-01): sent together the API saves them with the product, all or nothing.
/// </summary>
public sealed record RegisterProductRequest(
    string Key,
    string Name,
    string JwtAudience,
    string? LaunchUrl = null,
    IReadOnlyList<PermissionToAdd>? Permissions = null,
    bool? Activate = null);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/name</c>.</summary>
public sealed record RenameProductRequest(string Name);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/subscriptions</c>: an existing organisation to subscribe, with an optional seat limit.</summary>
public sealed record SubscribeOrganisationRequest(Guid TenantId, int? MaxSeats);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/test-organisations</c>.</summary>
public sealed record CreateTestOrganisationRequest(string Name, string OwnerEmail, int? MaxSeats);

/// <summary>An organisation's subscription to a product, as the API answers when an admin subscribes it.</summary>
public sealed record AdminSubscription(Guid TenantId, Guid ApplicationId, string Status, int? MaxSeats, int SeatsUsed);

/// <summary>The test organisation that was created, with its owner and its subscription.</summary>
public sealed record TestOrganisationCreated(Guid TenantId, string TenantName, string OwnerEmail, AdminSubscription Subscription);

/// <summary>
/// A newly registered product with its client credentials. The secret is in this response and nowhere else; printing it shows only the
/// client id, so it can't reach a log by being printed.
/// </summary>
public sealed record RegisteredProduct(
    Guid Id,
    string Key,
    string Name,
    string JwtAudience,
    bool IsActive,
    string ClientId,
    string ClientSecret,
    DateTimeOffset CreatedAt,
    string? LaunchUrl = null,
    int PermissionCount = 0)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Key = ").Append(Key).Append(", ClientId = ").Append(ClientId);
        return true;
    }
}

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/active</c>.</summary>
public sealed record SetProductActiveRequest(bool IsActive);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/launch-url</c>: the address, or null to clear it.</summary>
public sealed record SetLaunchUrlRequest(string? Url);

/// <summary>One permission of a product's catalog, as the platform admin reads it.</summary>
public sealed record AdminPermissionItem(string Key, string Label, string? Description, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>One permission to add to a product's catalog, or to update if its key is already there.</summary>
public sealed record PermissionToAdd(string Key, string Label, string? Description);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/permissions</c>.</summary>
public sealed record AddPermissionsRequest(IReadOnlyList<PermissionToAdd> Permissions);

/// <summary>What adding permissions did: how many were new, how many had their words changed, how many were already as given.</summary>
public sealed record AddPermissionsResult(Guid ApplicationId, int Created, int Updated, int Unchanged);
