using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Entitlements;
using AuthService.Api.Features.Subscriptions;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ent03;

/// <summary>
/// ENT-03 (AUTH-280) A tenant's plan, its per-tenant overrides and its effective limits: override, else plan value, else default. Every change
/// that alters the effective set raises the subscription's version once; every change is in the entitlement history. Proposal items 3 (state),
/// 5, 6, 7, 8, 9, 10, 11 and 20.
/// </summary>
public sealed class SubscriptionEntitlementTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ENT03_ANewSubscriptionStartsAtVersionOneWithEveryLimitAtItsDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var entitlements = await ReadAsync(setup, ct);

        entitlements.Version.ShouldBe(1);
        entitlements.PlanKey.ShouldBeNull();
        Value(entitlements, "contacts.reachable").ShouldBe(0);
        Source(entitlements, "contacts.reachable").ShouldBe("Default");
        Value(entitlements, "journeys.live").ShouldBe(0);
    }

    [Fact]
    public async Task ENT03_AssigningAPlanGivesItsValuesAndRaisesTheVersionByOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var response = await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entitlements = (await response.Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;
        entitlements.PlanKey.ShouldBe("growth");
        entitlements.PlanName.ShouldBe("Growth");
        entitlements.Version.ShouldBe(2);
        Value(entitlements, "contacts.reachable").ShouldBe(50_000);
        Source(entitlements, "contacts.reachable").ShouldBe("Plan");
        Value(entitlements, "journeys.live").ShouldBe(0, "Growth does not set it, so it takes the limit's default, not unlimited");
        Source(entitlements, "journeys.live").ShouldBe("Default");
        Value(entitlements, "storage.assets").ShouldBeNull("null is unlimited");
    }

    [Fact]
    public async Task ENT03_AssigningTheSamePlanAgainChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);

        var again = (await (await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct)).Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;

        again.Version.ShouldBe(2);
        await WithDbAsync(async db => (await db.EntitlementChanges.CountAsync(change => change.Change == EntitlementChanges.PlanAssigned, ct)).ShouldBe(1));
    }

    [Fact]
    public async Task ENT03_APlanThatWasAssignedCannotBeDeletedButCanBeDeactivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(PlanRoute(setup), new { planKey = (string?)null }, setup.AdminToken, ct);

        var deleted = await SendAsync(HttpMethod.Delete, $"{PlansRoute(setup)}/growth", setup.AdminToken, ct);
        var deactivated = await PutAsync($"{PlansRoute(setup)}/growth/active", new { isActive = false }, setup.AdminToken, ct);

        deleted.StatusCode.ShouldBe(HttpStatusCode.Conflict, "even after the tenant moved off it, the plan was assigned once");
        (await ReadProblemAsync(deleted, ct)).Code.ShouldBe("plan_in_use");
        deactivated.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ENT03_ADeactivatedPlanCannotBeNewlyAssignedButStaysWhereItIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync($"{PlansRoute(setup)}/growth/active", new { isActive = false }, setup.AdminToken, ct);
        await PutAsync($"{PlansRoute(setup)}/scale/active", new { isActive = false }, setup.AdminToken, ct);

        var refused = await PutAsync(PlanRoute(setup), new { planKey = "scale" }, setup.AdminToken, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(refused, ct)).Code.ShouldBe("plan_inactive");
        (await ReadAsync(setup, ct)).PlanKey.ShouldBe("growth", "tenants that have a deactivated plan keep it");
    }

    [Fact]
    public async Task ENT03_AnOverrideWinsOverThePlanAndSurvivesAPlanEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var other = await SubscribeAnotherTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(PlanRoute(setup with { TenantId = other }), new { planKey = "growth" }, setup.AdminToken, ct);
        (await PutAsync(OverrideRoute(setup, "contacts.reachable"), new { value = 75_000, reason = "Goodwill after the outage" }, setup.AdminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var versionBefore = (await ReadAsync(setup, ct)).Version;
        var otherVersionBefore = (await ReadAsync(setup with { TenantId = other }, ct)).Version;

        (await PutAsync($"{PlansRoute(setup)}/growth", new { name = "Growth", values = GrowthValues(contacts: 60_000) }, setup.AdminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var withOverride = await ReadAsync(setup, ct);
        Value(withOverride, "contacts.reachable").ShouldBe(75_000);
        Source(withOverride, "contacts.reachable").ShouldBe("Override");
        withOverride.Overrides.ShouldHaveSingleItem().LimitKey.ShouldBe("contacts.reachable");
        withOverride.Version.ShouldBe(versionBefore, "nothing this tenant can see changed");
        var withoutOverride = await ReadAsync(setup with { TenantId = other }, ct);
        Value(withoutOverride, "contacts.reachable").ShouldBe(60_000);
        withoutOverride.Version.ShouldBe(otherVersionBefore + 1, "a tenant whose number changed gets exactly one new version");
    }

    [Fact]
    public async Task ENT03_ClearingAnOverrideFallsBackToThePlanAndRaisesTheVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(OverrideRoute(setup, "contacts.reachable"), new { value = 75_000, reason = "Trial extension" }, setup.AdminToken, ct);
        var before = (await ReadAsync(setup, ct)).Version;

        var cleared = await SendAsync(HttpMethod.Delete, OverrideRoute(setup, "contacts.reachable"), setup.AdminToken, ct);

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entitlements = (await cleared.Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;
        Value(entitlements, "contacts.reachable").ShouldBe(50_000);
        entitlements.Overrides.ShouldBeEmpty();
        entitlements.Version.ShouldBe(before + 1);
        (await SendAsync(HttpMethod.Delete, OverrideRoute(setup, "contacts.reachable"), setup.AdminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("""{"value":75000}""", "Reason is required")]
    [InlineData("""{"value":75000,"reason":"   "}""", "Reason is required")]
    [InlineData("""{"reason":"Goodwill"}""", "Value is required")]
    [InlineData("""{"value":-5,"reason":"Goodwill"}""", "at least 0")]
    public async Task ENT03_AnOverrideNeedsAValueAndAReason(string json, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var response = await PutAsync(OverrideRoute(setup, "contacts.reachable"), JsonDocument.Parse(json).RootElement, setup.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(expected);
        await WithDbAsync(async db => (await db.SubscriptionOverrides.CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ENT03_AnUnlimitedOverrideIsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var response = await PutAsync(OverrideRoute(setup, "journeys.live"), new { value = (long?)null, reason = "Enterprise pilot" }, setup.AdminToken, ct);

        var entitlements = (await response.Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;
        Value(entitlements, "journeys.live").ShouldBeNull();
        Source(entitlements, "journeys.live").ShouldBe("Override");
    }

    [Fact]
    public async Task ENT03_AnOverrideForALimitTheProductDoesNotHaveIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var response = await PutAsync(OverrideRoute(setup, "rockets.launched"), new { value = 5, reason = "Why not" }, setup.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("limit_unknown");
    }

    [Fact]
    public async Task ENT03_TheSeatsLimitBecomesTheSubscriptionsSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var assigned = (await (await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct)).Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;
        var overridden = (await (await PutAsync(OverrideRoute(setup, "seats"), new { value = 8, reason = "Two extra seats sold" }, setup.AdminToken, ct)).Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;

        assigned.MaxSeats.ShouldBe(5, "Growth sets seats to 5");
        overridden.MaxSeats.ShouldBe(8);
        await WithDbAsync(async db => (await db.Subscriptions.SingleAsync(ct)).MaxSeats.ShouldBe(8));
    }

    [Fact]
    public async Task ENT03_TheProductsSeatLimitCallWritesASeatsOverrideWhenTheProductHasThatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);

        var response = await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(setup.TenantId, setup.Product.Id), """{"maxSeats": 12}""", setup.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var entitlements = await ReadAsync(setup, ct);
        entitlements.MaxSeats.ShouldBe(12);
        Value(entitlements, "seats").ShouldBe(12);
        var entry = entitlements.Overrides.ShouldHaveSingleItem();
        entry.SetByType.ShouldBe("Product");
        entry.Reason.ShouldBe(SeatLimitService.SeatsOverrideReason);
    }

    [Fact]
    public async Task ENT03_ChangingALimitDefaultReachesTenantsWithNoOtherValueForIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var before = (await ReadAsync(setup, ct)).Version;

        await PostAsync(LimitsRoute(setup), new { limits = new[] { new { key = "journeys.live", label = "Live journeys", unit = "journeys", defaultValue = 3 } } }, setup.AdminToken, ct);

        var entitlements = await ReadAsync(setup, ct);
        Value(entitlements, "journeys.live").ShouldBe(3);
        entitlements.Version.ShouldBe(before + 1);
    }

    [Fact]
    public async Task ENT03_RelabellingALimitDoesNotChangeAnyVersion()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var before = (await ReadAsync(setup, ct)).Version;

        await PostAsync(LimitsRoute(setup), new { limits = new[] { new { key = "journeys.live", label = "Journeys running", unit = "journeys", defaultValue = 0 } } }, setup.AdminToken, ct);

        (await ReadAsync(setup, ct)).Version.ShouldBe(before);
    }

    [Fact]
    public async Task ENT03_TheHistoryShowsWhoChangedWhatWhenAndWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(OverrideRoute(setup, "contacts.reachable"), new { value = 75_000, reason = "Goodwill after the outage" }, setup.AdminToken, ct);

        var page = await (await GetAsync($"/api/auth/admin/entitlement-history?tenantId={setup.TenantId}", setup.AdminToken, ct)).Content.ReadPageItemsAsync<EntitlementHistoryItem>(Json, ct);

        page.Select(item => item.Change).ShouldBe([EntitlementChanges.OverrideSet, EntitlementChanges.PlanAssigned], "newest first");
        var overrideSet = page[0];
        overrideSet.LimitKey.ShouldBe("contacts.reachable");
        overrideSet.OldValue.ShouldBeNull("there was no override before");
        overrideSet.NewValue!.Value.GetInt64().ShouldBe(75_000);
        overrideSet.Reason.ShouldBe("Goodwill after the outage");
        overrideSet.ActorType.ShouldBe("User");
        overrideSet.ActorId.ShouldBe(setup.AdminId);
        page[1].NewValue!.Value.GetString().ShouldBe("growth");
        page[1].OldValue.ShouldBeNull("the tenant had no plan before");

        var planEdits = await (await GetAsync($"/api/auth/admin/entitlement-history?applicationId={setup.Product.Id}&planKey=growth", setup.AdminToken, ct)).Content.ReadPageItemsAsync<EntitlementHistoryItem>(Json, ct);
        planEdits.Select(item => item.Change).ShouldContain(EntitlementChanges.PlanCreated);
        await WithDbAsync(async db =>
        {
            var audit = await db.AuditEntries.SingleAsync(entry => entry.Action == AuditActions.SubscriptionOverrideSet, ct);
            audit.Detail.ShouldBe("marketing", "the audit log keeps short codes only; the reason is in the history");
        });
    }

    [Fact]
    public async Task ENT03_OnlyAPlatformAdminReadsOrChangesATenantsEntitlements()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var personToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;

        (await GetAsync(EntitlementsRoute(setup), personToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(PlanRoute(setup), new { planKey = "growth" }, personToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(PlanRoute(setup), new { planKey = "growth" }, setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync("/api/auth/admin/entitlement-history", personToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ENT03_ATenantThatIsNotSubscribedOrAPlanThatDoesNotExistIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var notSubscribed = await GetAsync(EntitlementsRoute(setup with { TenantId = Guid.NewGuid() }), setup.AdminToken, ct);
        var noPlan = await PutAsync(PlanRoute(setup), new { planKey = "platinum" }, setup.AdminToken, ct);

        (await ReadProblemAsync(notSubscribed, ct)).Code.ShouldBe("subscription_not_found");
        (await ReadProblemAsync(noPlan, ct)).Code.ShouldBe("plan_not_found");
    }

    private sealed record Setup(string AdminToken, Guid AdminId, TestProduct Product, Guid TenantId);

    /// <summary>A product with limits and two plans (Growth, Scale) and one tenant (Acme) subscribed to it with no plan.</summary>
    private async Task<Setup> SetUpAsync(CancellationToken ct)
    {
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
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
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/plans", new { key = "growth", name = "Growth", values = GrowthValues(contacts: 50_000) }, adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/plans", new { key = "scale", name = "Scale",
            values = new Dictionary<string, long?> { ["contacts.reachable"] = 250_000, ["seats"] = 15 } }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var setup = new Setup(adminToken, admin.UserId, product, Guid.Empty);
        var tenantId = await SubscribeAnotherTenantAsync(setup, "Acme Ltd", "owner@acme.com", ct);
        return setup with { TenantId = tenantId };
    }

    private async Task<Guid> SubscribeAnotherTenantAsync(Setup setup, string name, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(name, email, ct);
        var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionEndpoints.RouteFor(owner.TenantId)) { Content = JsonContent.Create(new { }) };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", setup.Product.ServiceToken);
        (await Client.SendAsync(request, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    private static Dictionary<string, long?> GrowthValues(long contacts) =>
        new() { ["contacts.reachable"] = contacts, ["seats"] = 5, ["storage.assets"] = null };

    private async Task<SubscriptionEntitlementsResponse> ReadAsync(Setup setup, CancellationToken ct)
    {
        var response = await GetAsync(EntitlementsRoute(setup), setup.AdminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SubscriptionEntitlementsResponse>(Json, ct))!;
    }

    private static long? Value(SubscriptionEntitlementsResponse entitlements, string key) => entitlements.Limits.Single(limit => limit.Key == key).Value;

    private static string Source(SubscriptionEntitlementsResponse entitlements, string key) => entitlements.Limits.Single(limit => limit.Key == key).Source;

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        return Client.SendAsync(request, ct);
    }

    private static string SubscriptionRoute(Setup setup) => $"/api/auth/admin/tenants/{setup.TenantId}/subscriptions/{setup.Product.Id}";

    private static string EntitlementsRoute(Setup setup) => SubscriptionRoute(setup) + "/entitlements";

    private static string PlanRoute(Setup setup) => SubscriptionRoute(setup) + "/plan";

    private static string OverrideRoute(Setup setup, string limitKey) => $"{SubscriptionRoute(setup)}/overrides/{limitKey}";

    private static string PlansRoute(Setup setup) => $"/api/auth/admin/applications/{setup.Product.Id}/plans";

    private static string LimitsRoute(Setup setup) => $"/api/auth/admin/applications/{setup.Product.Id}/limits";
}
