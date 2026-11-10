using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Entitlements;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ent05;

/// <summary>
/// ENT-05 (AUTH-282) Reading entitlements instead of waiting for an event: a product reads one tenant, or all its tenants in the order their
/// entitlements changed (first sync and "what changed since"); a tenant admin reads their own plan and limits. Proposal items 17, 18 and 22.
/// </summary>
public sealed class EntitlementReadTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task ENT05_AProductReadsOneTenantsEntitlementsWithTheSameFieldsAsTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(OverrideRoute(setup, setup.TenantId, "contacts.reachable"), new { value = 75_000, reason = "Goodwill" }, setup.AdminToken, ct);

        var response = await GetAsync(EntitlementReadEndpoints.ProductTenantRouteFor(setup.TenantId), setup.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("tenantId").GetGuid().ShouldBe(setup.TenantId);
        body.GetProperty("applicationId").GetGuid().ShouldBe(setup.Product.Id);
        body.GetProperty("status").GetString().ShouldBe("Active");
        body.GetProperty("planKey").GetString().ShouldBe("growth");
        body.GetProperty("planName").GetString().ShouldBe("Growth");
        body.GetProperty("version").GetInt64().ShouldBe(3, "subscribed (1), plan assigned (2), override set (3)");
        body.GetProperty("changeNumber").GetInt64().ShouldBeGreaterThan(0);
        var set = body.GetProperty("entitlements");
        set.GetProperty("contacts.reachable").GetInt64().ShouldBe(75_000);
        set.GetProperty("seats").GetInt64().ShouldBe(5);
        set.GetProperty("journeys.live").GetInt64().ShouldBe(0, "a limit the plan leaves out is its default");
        set.GetProperty("storage.assets").ValueKind.ShouldBe(JsonValueKind.Null, "null is unlimited");
        body.GetProperty("overridden").EnumerateArray().Select(key => key.GetString()).ShouldBe(["contacts.reachable"]);
    }

    [Fact]
    public async Task ENT05_ATenantThatNeverSubscribedToTheCallingProductIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var other = await CreateActiveProductAsync(setup.AdminToken, "crm", ct);
        var owner = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, other.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach (var tenantId in new[] { owner.TenantId, Guid.NewGuid() })
        {
            var response = await GetAsync(EntitlementReadEndpoints.ProductTenantRouteFor(tenantId), setup.Product.ServiceToken, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "subscribed to another product only, or no such tenant");
            (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("subscription_not_found");
        }

        (await GetAsync(EntitlementReadEndpoints.ProductTenantRouteFor(owner.TenantId), other.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ENT05_ASuspendedSubscriptionIsStillReadWithItsStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(setup.TenantId, setup.Product.Id), """{"status": "Suspended"}""", setup.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await (await GetAsync(EntitlementReadEndpoints.ProductTenantRouteFor(setup.TenantId), setup.Product.ServiceToken, ct))
            .Content.ReadFromJsonAsync<JsonElement>(ct);

        body.GetProperty("status").GetString().ShouldBe("Suspended");
    }

    [Fact]
    public async Task ENT05_TheFeedListsEveryTenantOnceInChangeOrderAndPagesThroughThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var beta = await SubscribeTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);
        var gamma = await SubscribeTenantAsync(setup, "Gamma Ltd", "owner@gamma.com", ct);

        var first = await FeedAsync(setup, "?limit=2", ct);
        first.Items.Select(item => item.TenantId).ShouldBe([setup.TenantId, beta], "in the order they subscribed");
        first.Next.ShouldNotBeNull();
        first.Next!.After.ShouldBe(first.Items[^1].ChangeNumber);

        var second = await FeedAsync(setup, $"?limit=2&after={first.Next.After}", ct);
        second.Items.Select(item => item.TenantId).ShouldBe([gamma]);
        second.Next.ShouldBeNull("the last page");

        var all = first.Items.Concat(second.Items).ToList();
        all.Select(item => item.ChangeNumber).ShouldBeInOrder(SortDirection.Ascending);
        all.ShouldAllBe(item => item.ApplicationId == setup.Product.Id && item.Version == 1 && item.Entitlements.Count == 4);
    }

    [Fact]
    public async Task ENT05_AfterTheLastChangeNumberOnlyTenantsThatChangedSinceAreListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var beta = await SubscribeTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);
        var synced = await FeedAsync(setup, "", ct);
        var last = synced.Items[^1].ChangeNumber;

        (await FeedAsync(setup, $"?after={last}", ct)).Items.ShouldBeEmpty("nothing changed yet");

        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = "growth" }, setup.AdminToken, ct);
        var changed = await FeedAsync(setup, $"?after={last}", ct);

        changed.Items.Select(item => item.TenantId).ShouldBe([setup.TenantId], $"only the tenant that changed, not {beta}");
        changed.Items[0].ChangeNumber.ShouldBeGreaterThan(last);
        changed.Items[0].Version.ShouldBe(2);
        changed.Items[0].PlanKey.ShouldBe("growth");
        changed.Items[0].Entitlements["contacts.reachable"].ShouldBe(50_000);
        changed.Next.ShouldBeNull();
    }

    [Fact]
    public async Task ENT05_TheFeedListsOnlyTheCallingProductsSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var other = await CreateActiveProductAsync(setup.AdminToken, "crm", ct);
        var owner = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, other.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await FeedAsync(setup, "", ct)).Items.Select(item => item.TenantId).ShouldBe([setup.TenantId]);
        var theirs = await GetAsync(EntitlementReadEndpoints.ProductFeedRoute, other.ServiceToken, ct);
        (await theirs.Content.ReadPageItemsAsync<TenantEntitlements>(Json, ct))!.Select(item => item.TenantId).ShouldBe([owner.TenantId]);
    }

    [Theory]
    [InlineData("?limit=0", "limit")]
    [InlineData("?limit=501", "limit")]
    [InlineData("?after=-1", "after")]
    public async Task ENT05_PagingOutsideTheLimitsIsRefused(string query, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var response = await GetAsync(EntitlementReadEndpoints.ProductFeedRoute + query, setup.Product.ServiceToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors").TryGetProperty(field, out _).ShouldBeTrue();
        (await GetAsync(EntitlementReadEndpoints.ProductFeedRoute + "?limit=500", setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ENT05_OnlyServiceTokensReadTheProductEndpoints()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var ownerToken = await OwnerTenantTokenAsync(setup.TenantId, ct);

        (await GetAsync(EntitlementReadEndpoints.ProductFeedRoute, ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(EntitlementReadEndpoints.ProductTenantRouteFor(setup.TenantId), ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(EntitlementReadEndpoints.ProductFeedRoute, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ENT05_ATenantAdminReadsTheirOwnPlanAndLimitsWithLabelsButNoReasons()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        await PutAsync(PlanRoute(setup, setup.TenantId), new { planKey = "growth" }, setup.AdminToken, ct);
        await PutAsync(OverrideRoute(setup, setup.TenantId, "contacts.reachable"), new { value = 75_000, reason = "Goodwill for the outage" }, setup.AdminToken, ct);

        var response = await GetAsync(EntitlementReadEndpoints.TenantRouteFor(setup.Product.Id), await OwnerTenantTokenAsync(setup.TenantId, ct), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotContain("Goodwill", Case.Insensitive, "override reasons are for the platform team only");
        var body = JsonSerializer.Deserialize<TenantOwnEntitlements>(raw, Json)!;
        body.ApplicationKey.ShouldBe("marketing");
        body.Status.ShouldBe("Active");
        body.PlanKey.ShouldBe("growth");
        body.PlanName.ShouldBe("Growth");
        body.Version.ShouldBe(3);
        body.Limits.Select(limit => (limit.Key, limit.Label, limit.Unit, limit.Value, limit.Overridden)).ShouldBe(
        [
            ("contacts.reachable", "Reachable contacts", "contacts", (long?)75_000, true),
            ("journeys.live", "Live journeys", "journeys", 0, false),
            ("seats", "Users", "users", 5, false),
            ("storage.assets", "Asset storage", "MB", null, false),
        ]);
    }

    [Fact]
    public async Task ENT05_ATenantAdminCannotReadAProductTheyAreNotSubscribedToOrTheBuiltInOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var other = await CreateActiveProductAsync(setup.AdminToken, "crm", ct);
        var token = await OwnerTenantTokenAsync(setup.TenantId, ct);

        foreach (var applicationId in new[] { other.Id, BuiltInApplication.Id, Guid.NewGuid() })
        {
            var response = await GetAsync(EntitlementReadEndpoints.TenantRouteFor(applicationId), token, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString().ShouldBe("subscription_not_found");
        }
    }

    [Fact]
    public async Task ENT05_AMemberWithoutSubscriptionsReadAndAServiceTokenAreRefusedTheTenantRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var member = await SignUpVerifiedAsync("Member Co", "member@other.com", ct);
        await AddMembershipAsync(member.UserId, setup.TenantId, MembershipStatus.Active, ct);
        var memberToken = await TenantTokenAsync((await LoginForTokensAsync("member@other.com", ct)).AccessToken, setup.TenantId, ct);

        (await GetAsync(EntitlementReadEndpoints.TenantRouteFor(setup.Product.Id), memberToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(EntitlementReadEndpoints.TenantRouteFor(setup.Product.Id), setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A product reading "what changed since N" must never skip a change. A change number is given out while the product's lock is held, and
    /// that lock is kept until the change commits, so a second change to the same product waits and gets a higher number than the first.
    /// </summary>
    [Fact]
    public async Task ENT05_ASecondChangeWaitsForTheFirstToCommitSoChangeNumbersFollowCommitOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var beta = await SubscribeTenantAsync(setup, "Beta Ltd", "owner@beta.com", ct);

        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var state = scope.ServiceProvider.GetRequiredService<EntitlementState>();
        var alpha = await db.Subscriptions.Where(subscription => subscription.TenantId == setup.TenantId).Select(subscription => subscription.Id).SingleAsync(ct);

        long alphaNumber;
        Task<HttpResponseMessage> betaChange;
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            // Raising alpha's version with no "before" counts it as changed; its number is given out but not yet committed.
            alphaNumber = (await state.CommitAsync(setup.Product.Id, [alpha], new Dictionary<Guid, EntitlementSnapshot>(), ct, sendEvents: false))[0].ChangeSeq;
            betaChange = PutAsync(OverrideRoute(setup, beta, "contacts.reachable"), new { value = 10, reason = "Trial" }, setup.AdminToken, ct);

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            betaChange.IsCompleted.ShouldBeFalse("beta's change waits while alpha's is uncommitted");
            await transaction.CommitAsync(ct);
        }

        (await betaChange).StatusCode.ShouldBe(HttpStatusCode.OK);
        var feed = await FeedAsync(setup, "", ct);
        var numbers = feed.Items.ToDictionary(item => item.TenantId, item => item.ChangeNumber);
        numbers[setup.TenantId].ShouldBe(alphaNumber);
        numbers[beta].ShouldBeGreaterThan(alphaNumber);
    }

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
        var values = new Dictionary<string, long?> { ["contacts.reachable"] = 50_000, ["seats"] = 5, ["storage.assets"] = null };
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/plans", new { key = "growth", name = "Growth", values }, adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var setup = new Setup(adminToken, product, Guid.Empty);
        return setup with { TenantId = await SubscribeTenantAsync(setup, "Acme Ltd", OwnerEmail, ct) };
    }

    private async Task<Guid> SubscribeTenantAsync(Setup setup, string name, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(name, email, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    private async Task<Feed> FeedAsync(Setup setup, string query, CancellationToken ct)
    {
        var response = await GetAsync(EntitlementReadEndpoints.ProductFeedRoute + query, setup.Product.ServiceToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<Feed>(Json, ct))!;
    }

    private async Task<string> OwnerTenantTokenAsync(Guid tenantId, CancellationToken ct) =>
        await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, tenantId, ct);

    private static string SubscriptionRoute(Setup setup, Guid tenantId) => $"/api/auth/admin/tenants/{tenantId}/subscriptions/{setup.Product.Id}";

    private static string PlanRoute(Setup setup, Guid tenantId) => SubscriptionRoute(setup, tenantId) + "/plan";

    private static string OverrideRoute(Setup setup, Guid tenantId, string limitKey) => $"{SubscriptionRoute(setup, tenantId)}/overrides/{limitKey}";

    private sealed record Setup(string AdminToken, TestProduct Product, Guid TenantId);

    private sealed record Feed(List<TenantEntitlements> Items, ChangeCursor? Next);
}
