using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AuthService.Authorization;

namespace AuthService.Entitlements.Testing;

/// <summary>
/// A stand-in for the Auth Service in a product's tests. Register it as the <see cref="IAuthServiceClient"/> (it answers the entitlement
/// reads from what the test sets up), and use its event methods with <see cref="SignedWebhook"/> to post webhooks to the product exactly as
/// the Auth Service would. Every change to an organisation raises its version and change number, like the real service.
/// </summary>
/// <example>
/// <code>
/// var auth = new FakeAuthService();
/// auth.AddLimit("contacts.reachable", "Reachable contacts", 1_000);
/// auth.SetTenant(tenantId, new() { ["contacts.reachable"] = 50_000 }, planKey: "growth", planName: "Growth");
/// services.AddSingleton&lt;IAuthServiceClient&gt;(auth);
/// await client.SendAsync(SignedWebhook.Create("/auth-webhooks", auth.EntitlementsChangedEvent(tenantId), "whsec_test"));
/// </code>
/// </example>
public sealed class FakeAuthService : IAuthServiceClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<LimitDefinition> _limits = [];
    private readonly List<PlanSummary> _plans = [];
    private readonly Dictionary<Guid, EntitlementSnapshot> _tenants = [];
    private long _changeNumber;

    /// <summary>Creates the fake for a product.</summary>
    public FakeAuthService(Guid? productId = null, string productKey = "sample-product")
    {
        ProductId = productId ?? Guid.Parse("10000000-0000-4000-8000-000000000001");
        ProductKey = productKey;
    }

    /// <summary>The product the fake answers as.</summary>
    public Guid ProductId { get; }

    /// <summary>The product's key.</summary>
    public string ProductKey { get; }

    /// <summary>Every call made through <see cref="SendAsync"/>, as method and path.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Answers a call instead of the fake, given the method and path; null lets the fake answer. For failures and slow answers.</summary>
    public Func<HttpMethod, string, HttpResponseMessage?>? Respond { get; set; }

    /// <summary>Adds a limit to the product. <paramref name="defaultValue"/> null is unlimited.</summary>
    public FakeAuthService AddLimit(string key, string label, long? defaultValue, string? unit = null, string? description = null)
    {
        lock (_gate)
        {
            _limits.Add(new LimitDefinition(key, label, description, unit, new LimitValue(defaultValue)));
        }

        return this;
    }

    /// <summary>Adds a plan to the product.</summary>
    public FakeAuthService AddPlan(string key, string name, IDictionary<string, long?> values, bool isActive = true, string? description = null)
    {
        lock (_gate)
        {
            _plans.Add(new PlanSummary(key, name, description, isActive, values.ToDictionary(pair => pair.Key, pair => new LimitValue(pair.Value), StringComparer.Ordinal)));
        }

        return this;
    }

    /// <summary>
    /// Sets an organisation's entitlements as the Auth Service would compute them, raising its version and change number. Limits not given
    /// take their defaults, so the set is always complete.
    /// </summary>
    public EntitlementSnapshot SetTenant(
        Guid tenantId, IDictionary<string, long?>? entitlements = null, string? planKey = null, string? planName = null,
        IEnumerable<string>? overridden = null, string status = "Active")
    {
        lock (_gate)
        {
            var limits = _limits.ToDictionary(limit => limit.Key, limit => limit.Default, StringComparer.Ordinal);
            foreach (var (key, value) in entitlements ?? new Dictionary<string, long?>())
            {
                limits[key] = new LimitValue(value);
            }

            var version = _tenants.TryGetValue(tenantId, out var current) ? current.Version + 1 : 1;
            var snapshot = new EntitlementSnapshot(tenantId, ProductId, planKey, planName, version, limits, [.. overridden ?? []], status, ++_changeNumber);
            _tenants[tenantId] = snapshot;
            return snapshot;
        }
    }

    /// <summary>The organisation's entitlements as the fake holds them now.</summary>
    public EntitlementSnapshot? GetTenant(Guid tenantId)
    {
        lock (_gate)
        {
            return _tenants.GetValueOrDefault(tenantId);
        }
    }

    /// <summary>The body of an <c>EntitlementsChanged</c> event for the organisation as it is now.</summary>
    public string EntitlementsChangedEvent(Guid tenantId, Guid? eventId = null, DateTimeOffset? occurredAt = null) =>
        EventBody(AuthWebhookEventTypes.EntitlementsChanged, Require(tenantId), eventId, occurredAt, extra: null);

    /// <summary>The body of an <c>ApplicationSubscribed</c> event for the organisation as it is now.</summary>
    public string ApplicationSubscribedEvent(Guid tenantId, int? maxSeats = null, Guid? eventId = null, DateTimeOffset? occurredAt = null) =>
        EventBody(AuthWebhookEventTypes.ApplicationSubscribed, Require(tenantId), eventId, occurredAt, extra: new() { ["maxSeats"] = maxSeats });

    /// <summary>The body of an <c>ApplicationUnsubscribed</c> event (<paramref name="status"/> <c>Suspended</c> or <c>Cancelled</c>).</summary>
    public string ApplicationUnsubscribedEvent(Guid tenantId, string status, Guid? eventId = null, DateTimeOffset? occurredAt = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["eventId"] = eventId ?? Guid.NewGuid(),
            ["eventType"] = AuthWebhookEventTypes.ApplicationUnsubscribed,
            ["occurredAt"] = occurredAt ?? DateTimeOffset.UtcNow,
            ["tenantId"] = tenantId,
            ["applicationId"] = ProductId,
            ["status"] = status,
        }, Json);

    /// <inheritdoc />
    public Task<string> GetServiceTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake-service-token");

    /// <inheritdoc />
    public Task<AuthServiceProduct> GetProductAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuthServiceProduct(ProductId, ProductKey, ProductKey, $"{ProductKey}-api", $"{ProductKey}-client", true, []));

    /// <inheritdoc />
    public Task<AuthServiceSubscription> SubscribeTenantAsync(Guid tenantId, int? maxSeats = null, CancellationToken cancellationToken = default)
    {
        if (GetTenant(tenantId) is null)
        {
            SetTenant(tenantId);
        }

        return Task.FromResult(new AuthServiceSubscription(tenantId, ProductId, "Active", maxSeats, 0));
    }

    /// <inheritdoc />
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Requests.Add($"{method} {path}");
        }

        if (Respond?.Invoke(method, path) is { } answered)
        {
            return Task.FromResult(answered);
        }

        var uri = new Uri(new Uri("http://fake.auth"), path);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var route = uri.AbsolutePath;
        lock (_gate)
        {
            if (method == HttpMethod.Get && route == "/api/auth/applications/me/limits")
            {
                return Ok(Page(_limits.OrderBy(limit => limit.Key, StringComparer.Ordinal).Select(limit => new Dictionary<string, object?>
                {
                    ["key"] = limit.Key, ["label"] = limit.Label, ["description"] = limit.Description, ["unit"] = limit.Unit, ["defaultValue"] = limit.Default.Value,
                })));
            }

            if (method == HttpMethod.Get && route == "/api/auth/applications/me/plans")
            {
                return Ok(Page(_plans.OrderBy(plan => plan.Key, StringComparer.Ordinal).Select(plan => new Dictionary<string, object?>
                {
                    ["key"] = plan.Key, ["name"] = plan.Name, ["description"] = plan.Description, ["isActive"] = plan.IsActive,
                    ["values"] = plan.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
                })));
            }

            if (method == HttpMethod.Get && route == "/api/auth/applications/me/entitlements")
            {
                var after = long.TryParse(query["after"], NumberStyles.None, CultureInfo.InvariantCulture, out var from) ? from : 0;
                var limit = int.TryParse(query["limit"], NumberStyles.None, CultureInfo.InvariantCulture, out var take) ? take : 100;
                var rows = _tenants.Values.Where(snapshot => snapshot.ChangeNumber > after).OrderBy(snapshot => snapshot.ChangeNumber).Take(limit + 1).ToList();
                var page = rows.Take(limit).ToList();
                return Ok(new Dictionary<string, object?>
                {
                    ["items"] = page.Select(Item).ToList(),
                    ["next"] = rows.Count > limit ? new Dictionary<string, object?> { ["after"] = page[^1].ChangeNumber } : null,
                });
            }

            var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (method == HttpMethod.Get && parts is ["api", "auth", "tenants", var tenant, "subscriptions", "me", "entitlements"] && Guid.TryParse(tenant, out var tenantId))
            {
                return _tenants.TryGetValue(tenantId, out var snapshot)
                    ? Ok(Item(snapshot))
                    : Problem(HttpStatusCode.NotFound, "subscription_not_found");
            }
        }

        return Problem(HttpStatusCode.NotFound, "not_found");
    }

    private EntitlementSnapshot Require(Guid tenantId) =>
        GetTenant(tenantId) ?? throw new InvalidOperationException($"Call SetTenant for {tenantId} first.");

    private string EventBody(string eventType, EntitlementSnapshot snapshot, Guid? eventId, DateTimeOffset? occurredAt, Dictionary<string, object?>? extra)
    {
        var body = new Dictionary<string, object?>
        {
            ["eventId"] = eventId ?? Guid.NewGuid(),
            ["eventType"] = eventType,
            ["occurredAt"] = occurredAt ?? DateTimeOffset.UtcNow,
            ["schemaVersion"] = 1,
            ["tenantId"] = snapshot.TenantId,
            ["applicationId"] = ProductId,
            ["planKey"] = snapshot.PlanKey,
            ["planName"] = snapshot.PlanName,
            ["version"] = snapshot.Version,
            ["entitlements"] = snapshot.Limits.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
            ["overridden"] = snapshot.Overridden,
        };
        foreach (var (key, value) in extra ?? [])
        {
            body[key] = value;
        }

        return JsonSerializer.Serialize(body, Json);
    }

    private static Dictionary<string, object?> Item(EntitlementSnapshot snapshot) => new()
    {
        ["tenantId"] = snapshot.TenantId,
        ["applicationId"] = snapshot.ApplicationId,
        ["status"] = snapshot.Status,
        ["planKey"] = snapshot.PlanKey,
        ["planName"] = snapshot.PlanName,
        ["version"] = snapshot.Version,
        ["changeNumber"] = snapshot.ChangeNumber,
        ["entitlements"] = snapshot.Limits.ToDictionary(pair => pair.Key, pair => pair.Value.Value),
        ["overridden"] = snapshot.Overridden,
    };

    private static Dictionary<string, object?> Page<T>(IEnumerable<T> items) => new() { ["items"] = items.ToList(), ["next"] = null };

    private static Task<HttpResponseMessage> Ok(object body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
    });

    private static Task<HttpResponseMessage> Problem(HttpStatusCode status, string code) => Task.FromResult(new HttpResponseMessage(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"{{code}}","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    });
}

/// <summary>Builds webhook requests signed like the Auth Service's, for posting to a product in tests.</summary>
public static class SignedWebhook
{
    /// <summary>
    /// A <c>POST</c> to <paramref name="path"/> with <paramref name="body"/>, the event id and type read from it, a timestamp (now unless
    /// given) and the <c>v1=</c> signature made with <paramref name="secret"/>.
    /// </summary>
    public static HttpRequestMessage Create(string path, string body, string secret, DateTimeOffset? signedAt = null)
    {
        var timestamp = (signedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        using var document = JsonDocument.Parse(body);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (document.RootElement.TryGetProperty("eventId", out var id))
        {
            request.Headers.TryAddWithoutValidation(AuthWebhookVerifier.EventIdHeader, id.GetString());
        }

        if (document.RootElement.TryGetProperty("eventType", out var type))
        {
            request.Headers.TryAddWithoutValidation(AuthWebhookVerifier.EventTypeHeader, type.GetString());
        }

        request.Headers.TryAddWithoutValidation(AuthWebhookVerifier.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(AuthWebhookVerifier.SignatureHeader, AuthWebhookVerifier.Sign(secret, timestamp, body));
        return request;
    }
}
