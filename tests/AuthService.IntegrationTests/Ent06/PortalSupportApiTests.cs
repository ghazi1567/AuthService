using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Entitlements;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ent06;

/// <summary>
/// ENT-06 (AUTH-283) The two reads the portal needs beside ENT-01 to ENT-05: what a plan edit would change before it is saved, and the list of
/// one organisation's subscriptions for a platform admin.
/// </summary>
public sealed class PortalSupportApiTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task ENT06_APlanPreviewCountsTheTenantsOnThePlanAndTheOnesWhoseNumbersWouldChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var tenants = new List<Guid>();
        foreach (var (name, email) in new[] { ("Acme Ltd", "owner@acme.com"), ("Beta Ltd", "owner@beta.com"), ("Gamma Ltd", "owner@gamma.com") })
        {
            var tenantId = await SubscribeTenantAsync(setup, name, email, ct);
            await PutAsync($"{SubscriptionRoute(setup, tenantId)}/plan", new { planKey = "growth" }, setup.AdminToken, ct);
            tenants.Add(tenantId);
        }

        await PutAsync($"{SubscriptionRoute(setup, tenants[2])}/overrides/contacts.reachable", new { value = 75_000, reason = "Goodwill" }, setup.AdminToken, ct);
        await SubscribeTenantAsync(setup, "Delta Ltd", "owner@delta.com", ct);
        var versionsBefore = await VersionsAsync(setup, ct);
        var eventsBefore = await EventCountAsync(ct);

        var response = await PostAsync(PreviewRoute(setup), new { name = "Growth", values = new Dictionary<string, long?> { ["contacts.reachable"] = 60_000, ["seats"] = 5 } }, setup.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<PlanPreviewDto>(Json, ct))!;
        preview.Subscriptions.ShouldBe(3, "Delta has no plan");
        preview.Changed.ShouldBe(2, "Gamma's override hides the change");
        (await VersionsAsync(setup, ct)).ShouldBe(versionsBefore, "a preview writes nothing");
        (await EventCountAsync(ct)).ShouldBe(eventsBefore);
    }

    [Fact]
    public async Task ENT06_APreviewOfTheSameValuesChangesNobody()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var tenantId = await SubscribeTenantAsync(setup, "Acme Ltd", "owner@acme.com", ct);
        await PutAsync($"{SubscriptionRoute(setup, tenantId)}/plan", new { planKey = "growth" }, setup.AdminToken, ct);

        var preview = (await (await PostAsync(PreviewRoute(setup), new { name = "Renamed", values = Growth }, setup.AdminToken, ct))
            .Content.ReadFromJsonAsync<PlanPreviewDto>(Json, ct))!;

        preview.ShouldBe(new PlanPreviewDto(1, 0), "only the name differs");
    }

    [Fact]
    public async Task ENT06_APreviewIsRefusedLikeTheSaveItStandsFor()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);

        var unknownPlan = await PostAsync($"/api/auth/admin/applications/{setup.Product.Id}/plans/scale/preview", new { name = "Scale", values = Growth }, setup.AdminToken, ct);
        var unknownLimit = await PostAsync(PreviewRoute(setup), new { name = "Growth", values = new Dictionary<string, long?> { ["sms.monthly"] = 5 } }, setup.AdminToken, ct);
        var noName = await PostAsync(PreviewRoute(setup), new { values = Growth }, setup.AdminToken, ct);

        unknownPlan.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Code(unknownPlan, ct)).ShouldBe("plan_not_found");
        unknownLimit.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Code(unknownLimit, ct)).ShouldBe("limit_unknown");
        noName.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ENT06_APlatformAdminListsAnOrganisationsSubscriptionsByProductNameWithTheirPlans()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var other = await CreateActiveProductAsync(setup.AdminToken, "analytics", ct);
        var tenantId = await SubscribeTenantAsync(setup, "Acme Ltd", "owner@acme.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(tenantId), new { }, other.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await PutAsync($"{SubscriptionRoute(setup, tenantId)}/plan", new { planKey = "growth" }, setup.AdminToken, ct);

        var response = await GetAsync($"/api/auth/admin/tenants/{tenantId}/subscriptions", setup.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await response.Content.ReadPageItemsAsync<AdminTenantSubscriptionItem>(Json, ct))!;
        items.Select(item => (item.ApplicationKey, item.Status, item.PlanKey, item.PlanName, item.EntitlementsVersion)).ShouldBe(
        [
            ("analytics", "Active", (string?)null, (string?)null, 1L),
            ("marketing", "Active", "growth", "Growth", 2L),
        ]);
    }

    [Fact]
    public async Task ENT06_TheOrganisationListIsForPlatformAdminsAndAnUnknownOrganisationIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetUpAsync(ct);
        var tenantId = await SubscribeTenantAsync(setup, "Acme Ltd", "owner@acme.com", ct);
        var ownerToken = await TenantTokenAsync((await LoginForTokensAsync("owner@acme.com", ct)).AccessToken, tenantId, ct);

        var unknown = await GetAsync($"/api/auth/admin/tenants/{Guid.NewGuid()}/subscriptions", setup.AdminToken, ct);

        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await Code(unknown, ct)).ShouldBe("tenant_not_found");
        (await GetAsync($"/api/auth/admin/tenants/{tenantId}/subscriptions", ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(PreviewRoute(setup), new { name = "Growth", values = Growth }, ownerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private static readonly Dictionary<string, long?> Growth = new() { ["contacts.reachable"] = 50_000, ["seats"] = 5 };

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
            },
        }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PostAsync($"/api/auth/admin/applications/{product.Id}/plans", new { key = "growth", name = "Growth", values = Growth }, adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Setup(adminToken, product);
    }

    private async Task<Guid> SubscribeTenantAsync(Setup setup, string name, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(name, email, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, setup.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    private async Task<Dictionary<Guid, long>> VersionsAsync(Setup setup, CancellationToken ct)
    {
        var versions = new Dictionary<Guid, long>();
        await WithDbAsync(async db => versions = await db.Subscriptions.AsNoTracking()
            .Where(subscription => subscription.ApplicationId == setup.Product.Id)
            .ToDictionaryAsync(subscription => subscription.TenantId, subscription => subscription.EntitlementsVersion, ct));
        return versions;
    }

    private async Task<int> EventCountAsync(CancellationToken ct)
    {
        var count = 0;
        await WithDbAsync(async db => count = await db.WebhookOutbox.CountAsync(message => message.EventType == WebhookEvents.EntitlementsChanged, ct));
        return count;
    }

    private static async Task<string?> Code(HttpResponseMessage response, CancellationToken ct) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("code").GetString();

    private static string SubscriptionRoute(Setup setup, Guid tenantId) => $"/api/auth/admin/tenants/{tenantId}/subscriptions/{setup.Product.Id}";

    private static string PreviewRoute(Setup setup) => $"/api/auth/admin/applications/{setup.Product.Id}/plans/growth/preview";

    private sealed record Setup(string AdminToken, TestProduct Product);
}
