using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AuthService.Api.Features.Applications;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.App03;

/// <summary>
/// APP-03 (AUTH-17) List Applications, through the real <c>GET /api/auth/applications</c> with the identity token of a signed-in
/// person. Test method names start with their Test Case ID (APP_TC7..APP_TC8) per the workbook convention.
/// </summary>
public sealed class ListApplicationsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string TenantEmail = "owner@acme.com";

    [Fact]
    public async Task APP_TC7_OnlyTheActiveApplicationIsListedWhenOneIsActiveAndOneIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var active = await RegisterAsync(adminToken, "whatsapp-saas", "WhatsApp SaaS", ct, activate: true);
        await RegisterAsync(adminToken, "draft-product", "Draft Product", ct, activate: false);
        var tenantToken = await TenantTokenAsync(ct);

        var response = await GetAsync(ListApplicationsEndpoints.Route, tenantToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var applications = (await response.Content.ReadFromJsonAsync<List<ApplicationSummary>>(Json, ct))!;
        applications.ShouldBe([new ApplicationSummary(active, "whatsapp-saas", "WhatsApp SaaS")]);
    }

    [Fact]
    public async Task APP_TC8_ATenantWithNoSubscriptionsSeesTheActiveApplications()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "whatsapp-saas", "WhatsApp SaaS", ct, activate: true);
        await RegisterAsync(adminToken, "crm", "CRM", ct, activate: true);
        var tenantToken = await TenantTokenAsync(ct);

        var response = await GetAsync(ListApplicationsEndpoints.Route, tenantToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var applications = (await response.Content.ReadFromJsonAsync<List<ApplicationSummary>>(Json, ct))!;
        applications.Select(application => application.Key).ShouldBe(["crm", "whatsapp-saas"], "no subscription is needed to see them");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task APP03_WithNoActiveApplicationsTheListIsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "draft-product", "Draft Product", ct, activate: false);

        var response = await GetAsync(ListApplicationsEndpoints.Route, await TenantTokenAsync(ct), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<ApplicationSummary>>(Json, ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task APP03_AWithdrawnApplicationDisappearsAndReturnsWhenReactivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "whatsapp-saas", "WhatsApp SaaS", ct, activate: true);
        var tenantToken = await TenantTokenAsync(ct);

        (await SetActiveAsync(adminToken, id, false, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await KeysAsync(tenantToken, ct)).ShouldBeEmpty();

        (await SetActiveAsync(adminToken, id, true, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await KeysAsync(tenantToken, ct)).ShouldBe(["whatsapp-saas"]);
    }

    [Fact]
    public async Task APP03_TheListShowsOnlyWhatATenantNeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "whatsapp-saas", "WhatsApp SaaS", ct, activate: true);

        var response = await GetAsync(ListApplicationsEndpoints.Route, await TenantTokenAsync(ct), ct);

        var item = (await response.Content.ReadFromJsonAsync<JsonArray>(ct))!.Single()!.AsObject();
        item.Select(property => property.Key).Order().ShouldBe(["id", "key", "name"]);
    }

    [Fact]
    public async Task APP03_ASignedInPersonWhoIsNotAnAdminCanListButAnonymousAndServiceCallersCannot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var response = await PostAsync(ApplicationAdminEndpoints.Route,
            new { key = "whatsapp-saas", name = "WhatsApp SaaS", jwtAudience = "whatsapp-saas-api" }, adminToken, ct);
        var product = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var serviceToken = (await (await PostAsync(ServiceTokenEndpoints.Route,
                new { grant_type = "client_credentials", client_id = product.ClientId, client_secret = product.ClientSecret }, null, ct))
            .Content.ReadFromJsonAsync<ServiceTokenResponse>(ct))!.AccessToken;
        var productAudienceToken = CreateToken(Guid.NewGuid(), ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas-api");

        (await GetAsync(ListApplicationsEndpoints.Route, await TenantTokenAsync(ct), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(ListApplicationsEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(ListApplicationsEndpoints.Route, serviceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(ListApplicationsEndpoints.Route, productAudienceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<string> TenantTokenAsync(CancellationToken ct)
    {
        await SignUpVerifiedAsync("Acme Ltd", TenantEmail, ct);
        return (await LoginForTokensAsync(TenantEmail, ct)).AccessToken;
    }

    private async Task<List<string>> KeysAsync(string token, CancellationToken ct) =>
        (await (await GetAsync(ListApplicationsEndpoints.Route, token, ct)).Content.ReadFromJsonAsync<List<ApplicationSummary>>(Json, ct))!
            .Select(application => application.Key).ToList();

    private Task<HttpResponseMessage> SetActiveAsync(string token, Guid applicationId, bool isActive, CancellationToken ct) =>
        PutAsync($"{ApplicationAdminEndpoints.Route}/{applicationId}/active", new SetApplicationActiveRequest(isActive), token, ct);

    /// <summary>Registers a product and, when asked, gives it a catalog and activates it, as a platform admin would.</summary>
    private async Task<Guid> RegisterAsync(string adminToken, string key, string name, CancellationToken ct, bool activate)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;

        if (activate)
        {
            (await PostAsync($"{ApplicationAdminEndpoints.Route}/{id}/permissions",
                new RegisterPermissionsRequest([new PermissionInput("thing.use", "Use the thing", null)]), adminToken, ct))
                .StatusCode.ShouldBe(HttpStatusCode.Created);
            (await SetActiveAsync(adminToken, id, true, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        return id;
    }
}
