using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ent04;

/// <summary>
/// ENT-04 (AUTH-281) What products are told about entitlements: one <c>EntitlementsChanged</c> event each time a subscription's version rises,
/// carrying the complete set, and an <c>ApplicationSubscribed</c> event that now carries the plan and entitlements too. Proposal items 3 (event),
/// 4 and 21. The events are read from the outbox here; <see cref="EntitlementEventDeliveryTests"/> shows one delivered and signed.
/// </summary>
public sealed class EntitlementEventTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ENT04_AssigningAPlanSendsExactlyOneEventWithTheWholeSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var before = await EventCountAsync(ct);

        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = "growth" }, setup.AdminToken, ct);

        var events = await EventsAsync(WebhookEvents.EntitlementsChanged, ct);
        events.Count.ShouldBe(1);
        (await EventCountAsync(ct)).ShouldBe(before + 1, "nothing else was sent");
        var e = events[0];
        e.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        e.GetProperty("tenantId").GetGuid().ShouldBe(setup.TenantId);
        e.GetProperty("applicationId").GetGuid().ShouldBe(setup.Product.Id);
        e.GetProperty("planKey").GetString().ShouldBe("growth");
        e.GetProperty("planName").GetString().ShouldBe("Growth");
        e.GetProperty("version").GetInt64().ShouldBe(2, "the subscription started at 1");
        var set = e.GetProperty("entitlements");
        // The outbox keeps the payload as jsonb, which does not keep key order, so the keys are compared as a set.
        set.EnumerateObject().Select(p => p.Name).Order().ToArray().ShouldBe(["contacts.reachable", "journeys.live", "seats", "storage.assets"], "every limit, never a difference");
        set.GetProperty("contacts.reachable").GetInt64().ShouldBe(50_000);
        set.GetProperty("journeys.live").GetInt64().ShouldBe(0, "a limit the plan leaves out is its default");
        set.GetProperty("storage.assets").ValueKind.ShouldBe(JsonValueKind.Null, "null is unlimited");
        e.GetProperty("overridden").GetArrayLength().ShouldBe(0);
        e.GetProperty("eventId").GetGuid().ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task ENT04_APlanEditSendsOneEventToEachTenantWhoseValueChangedAndNoneToATenantWithAnOverride()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var beta = await SubscribeTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);
        var gamma = await SubscribeTenantAsync(setup, "Gamma Ltd", "owner@gamma.com", ct);
        foreach (var tenant in new[] { setup.TenantId, beta, gamma })
        {
            await PutAsync(PlanRoute(setup, tenant), new { planKey = "growth" }, setup.AdminToken, ct);
        }

        await PutAsync($"{SubscriptionRoute(setup, gamma)}/overrides/contacts.reachable", new { value = 75_000, reason = "Goodwill" }, setup.AdminToken, ct);
        await ClearOutboxAsync(ct);

        await PutAsync($"/api/auth/admin/applications/{setup.Product.Id}/plans/growth", new { name = "Growth", values = Growth(60_000) }, setup.AdminToken, ct);

        var events = await EventsAsync(WebhookEvents.EntitlementsChanged, ct);
        events.Select(e => e.GetProperty("tenantId").GetGuid()).Order().ShouldBe(new[] { setup.TenantId, beta }.Order(), "exactly one each, none for Gamma");
        events.ShouldAllBe(e => e.GetProperty("entitlements").GetProperty("contacts.reachable").GetInt64() == 60_000);
    }

    [Fact]
    public async Task ENT04_AnOverrideIsNamedInTheEventAndClearingItSendsAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = "growth" }, setup.AdminToken, ct);
        await ClearOutboxAsync(ct);

        await PutAsync($"{SubscriptionRoute(setup, setup.TenantId)}/overrides/contacts.reachable", new { value = 75_000, reason = "Goodwill" }, setup.AdminToken, ct);
        await SendAsync(HttpMethod.Delete, $"{SubscriptionRoute(setup, setup.TenantId)}/overrides/contacts.reachable", setup.AdminToken, ct);

        var events = await EventsAsync(WebhookEvents.EntitlementsChanged, ct);
        events.Count.ShouldBe(2);
        events[0].GetProperty("overridden").EnumerateArray().Select(k => k.GetString()).ShouldBe(["contacts.reachable"]);
        events[0].GetProperty("entitlements").GetProperty("contacts.reachable").GetInt64().ShouldBe(75_000);
        events[1].GetProperty("overridden").GetArrayLength().ShouldBe(0);
        events[1].GetProperty("version").GetInt64().ShouldBe(events[0].GetProperty("version").GetInt64() + 1, "versions only rise");
    }

    [Fact]
    public async Task ENT04_AChangeNobodyCanSeeSendsNoEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await ClearOutboxAsync(ct);

        await PostAsync(LimitsRoute(setup), new { limits = new[] { new { key = "journeys.live", label = "Journeys running", unit = "journeys", defaultValue = 0 } } }, setup.AdminToken, ct);
        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = (string?)null }, setup.AdminToken, ct);

        (await EventCountAsync(ct)).ShouldBe(0, "a relabel and removing a plan the tenant never had change nothing a product holds");
    }

    [Fact]
    public async Task ENT04_AddingALimitTellsEveryTenantOfTheProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var beta = await SubscribeTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);
        await ClearOutboxAsync(ct);

        await PostAsync(LimitsRoute(setup), new { limits = new[] { new { key = "apikeys.max", label = "API keys", unit = "keys", defaultValue = 2 } } }, setup.AdminToken, ct);

        var events = await EventsAsync(WebhookEvents.EntitlementsChanged, ct);
        events.Select(e => e.GetProperty("tenantId").GetGuid()).Order().ShouldBe(new[] { setup.TenantId, beta }.Order());
        events.ShouldAllBe(e => e.GetProperty("entitlements").GetProperty("apikeys.max").GetInt64() == 2);
    }

    [Fact]
    public async Task ENT04_SubscribingSendsOneApplicationSubscribedThatCarriesTheEntitlementsAndNoSeparateChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await ClearOutboxAsync(ct);

        var owner = await SignUpVerifiedAsync("Delta Ltd", "owner@delta.com", ct);
        var response = await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { planKey = "Growth", maxSeats = 9 }, setup.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        body.PlanKey.ShouldBe("growth");
        body.MaxSeats.ShouldBe(9, "the seat limit asked for becomes a seats override, and maxSeats follows it");
        body.Entitlements!["contacts.reachable"].ShouldBe(50_000);
        body.Entitlements["seats"].ShouldBe(9);

        (await EventsAsync(WebhookEvents.EntitlementsChanged, ct)).ShouldBeEmpty();
        var e = (await EventsAsync(WebhookEvents.ApplicationSubscribed, ct)).ShouldHaveSingleItem();
        e.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        e.GetProperty("planKey").GetString().ShouldBe("growth");
        e.GetProperty("planName").GetString().ShouldBe("Growth");
        e.GetProperty("version").GetInt64().ShouldBe(body.EntitlementsVersion!.Value);
        e.GetProperty("maxSeats").GetInt32().ShouldBe(9);
        e.GetProperty("entitlements").GetProperty("seats").GetInt64().ShouldBe(9);
        e.GetProperty("overridden").EnumerateArray().Select(k => k.GetString()).ShouldBe(["seats"]);
    }

    [Fact]
    public async Task ENT04_ApplicationSubscribedKeepsEveryFieldItHadForAProductWithoutLimits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "support", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { maxSeats = 3 }, product.ServiceToken, ct);

        var e = (await EventsAsync(WebhookEvents.ApplicationSubscribed, ct)).ShouldHaveSingleItem();
        e.GetProperty("eventType").GetString().ShouldBe("ApplicationSubscribed");
        e.GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId);
        e.GetProperty("applicationId").GetGuid().ShouldBe(product.Id);
        e.GetProperty("maxSeats").GetInt32().ShouldBe(3, "a product without a seats limit still sets the number itself");
        e.TryGetProperty("occurredAt", out _).ShouldBeTrue();
        e.GetProperty("eventId").GetGuid().ShouldNotBe(Guid.Empty);
        e.GetProperty("entitlements").EnumerateObject().ShouldBeEmpty("the new fields are only added");
        e.GetProperty("planKey").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("platinum", HttpStatusCode.NotFound, "plan_not_found")]
    [InlineData("retired", HttpStatusCode.Conflict, "plan_inactive")]
    public async Task ENT04_SubscribingOnAPlanThatCannotBeAssignedIsRefusedAndNothingIsWritten(string planKey, HttpStatusCode status, string code)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PostAsync($"/api/auth/admin/applications/{setup.Product.Id}/plans", new { key = "retired", name = "Retired", isActive = false, values = new { } }, setup.AdminToken, ct);
        await ClearOutboxAsync(ct);
        var owner = await SignUpVerifiedAsync("Delta Ltd", "owner@delta.com", ct);

        var response = await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { planKey }, setup.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(status);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe(code);
        (await EventCountAsync(ct)).ShouldBe(0);
        await WithDbAsync(async db => (await db.Subscriptions.CountAsync(s => s.TenantId == owner.TenantId, ct)).ShouldBe(0));
    }

    private sealed record Setup(string AdminToken, TestProduct Product, Guid TenantId);

    private async Task<Setup> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/limits", new
        {
            limits = new object[]
            {
                new { key = "contacts.reachable", label = "Reachable contacts", unit = "contacts", defaultValue = 0 },
                new { key = "seats", label = "Users", unit = "users", defaultValue = 1 },
                new { key = "journeys.live", label = "Live journeys", unit = "journeys", defaultValue = 0 },
                new { key = "storage.assets", label = "Asset storage", unit = "MB", defaultValue = 0 },
            },
        }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/plans", new { key = "growth", name = "Growth", values = Growth(50_000) }, adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var setup = new Setup(adminToken, product, Guid.Empty);
        return setup with { TenantId = await SubscribeTenantAsync(setup, "Acme Ltd", "owner@acme.com", ct) };
    }

    private async Task<Guid> SubscribeTenantAsync(Setup setup, string name, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(name, email, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    private static Dictionary<string, long?> Growth(long contacts) => new() { ["contacts.reachable"] = contacts, ["seats"] = 5, ["storage.assets"] = null };

    /// <summary>The payloads of the queued events of one type, oldest first.</summary>
    private async Task<List<JsonElement>> EventsAsync(string eventType, CancellationToken ct)
    {
        var payloads = new List<string>();
        await WithDbAsync(async db => payloads = await db.WebhookOutbox.AsNoTracking()
            .Where(message => message.EventType == eventType)
            .OrderBy(message => message.Id)
            .Select(message => message.PayloadJson)
            .ToListAsync(ct));
        return payloads.Select(payload => JsonDocument.Parse(payload).RootElement.Clone()).ToList();
    }

    private async Task<int> EventCountAsync(CancellationToken ct)
    {
        var count = 0;
        await WithDbAsync(async db => count = await db.WebhookOutbox.CountAsync(ct));
        return count;
    }

    private Task ClearOutboxAsync(CancellationToken ct) => WithDbAsync(db => db.WebhookOutbox.ExecuteDeleteAsync(ct));

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return Client.SendAsync(request, ct);
    }

    private static string SubscriptionRoute(Setup setup, Guid tenantId) => $"/api/auth/admin/tenants/{tenantId}/subscriptions/{setup.Product.Id}";

    private static string PlanRoute(Setup setup, Guid tenantId) => SubscriptionRoute(setup, tenantId) + "/plan";

    private static string LimitsRoute(Setup setup) => $"/api/auth/admin/applications/{setup.Product.Id}/limits";
}
