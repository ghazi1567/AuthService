using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Ent04;

/// <summary>
/// ENT-04 (AUTH-281) An <c>EntitlementsChanged</c> event goes out exactly like the other events: the real worker signs it with the product's
/// secret and posts it with the same headers (SUB-06), here to a fake product.
/// </summary>
public sealed class EntitlementEventDeliveryTests : AuthApiTestBase
{
    private const string CallbackUrl = "https://product.test/hooks";
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
    private readonly FakeWebhookReceiver _product;

    public EntitlementEventDeliveryTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
        _product = new FakeWebhookReceiver(_clock);
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:DispatcherEnabled"] = "true" },
        ConfigureServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _product);
        },
    };

    [Fact]
    public async Task ENT04_AnEntitlementsChangedEventIsDeliveredSignedWithItsTypeInTheHeaders()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var registered = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(CallbackUrl, false, null), product.ServiceToken, ct);
        var secret = (await registered.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!.SigningSecret!;
        await PostAsync($"/api/auth/admin/applications/{product.Id}/limits",
            new { limits = new[] { new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 0 } } }, adminToken, ct);
        await PostAsync($"/api/auth/admin/applications/{product.Id}/plans",
            new { key = "growth", name = "Growth", values = new Dictionary<string, long?> { ["contacts.reachable"] = 50_000 } }, adminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct);
        await Eventually.UntilAsync(() => _product.Requests.Count >= 1, "ApplicationSubscribed arrives");

        await PutAsync($"/api/auth/admin/tenants/{owner.TenantId}/subscriptions/{product.Id}/plan", new { planKey = "growth" }, adminToken, ct);

        await Eventually.UntilAsync(() => _product.Requests.Count >= 2, "EntitlementsChanged arrives");
        var request = _product.Requests[1];
        request.Headers["X-Auth-Event-Type"].ShouldBe("EntitlementsChanged");
        var timestamp = long.Parse(request.Headers["X-Auth-Timestamp"]);
        var signature = "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{request.Body}"))).ToLowerInvariant();
        request.Headers["X-Auth-Signature"].ShouldBe(signature);
        using var payload = JsonDocument.Parse(request.Body);
        request.Headers["X-Auth-Event-Id"].ShouldBe(payload.RootElement.GetProperty("eventId").GetGuid().ToString());
        payload.RootElement.GetProperty("entitlements").GetProperty("contacts.reachable").GetInt64().ShouldBe(50_000);
        payload.RootElement.GetProperty("version").GetInt64().ShouldBe(2);
    }
}
