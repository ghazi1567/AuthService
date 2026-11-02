using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AuthService.Api.Features.Applications;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.App06;

/// <summary>
/// APP-06 (AUTH-256) A platform admin lists every product, active or not, through the real <c>GET /api/auth/admin/applications</c>.
/// The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class AdminListApplicationsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string TenantEmail = "owner@acme.com";

    [Fact]
    public async Task APP06_WithNoProductsTheListIsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await GetAsync(ApplicationAdminEndpoints.Route, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<AdminApplicationItem>>(Json, ct))!.ShouldBeEmpty("the built-in product is not one to manage");
    }

    [Fact]
    public async Task APP06_ActiveInactiveAndJustRegisteredProductsAreAllListedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var billing = await RegisterAsync(adminToken, "billing", "Billing", ct, activate: true);
        var draft = await RegisterAsync(adminToken, "draft", "Draft", ct, activate: false);
        var helpdesk = await RegisterAsync(adminToken, "helpdesk", "Helpdesk", ct, activate: true);
        (await SetActiveAsync(adminToken, helpdesk, false, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var items = await ListAsync(adminToken, ct);

        items.Select(item => item.Key).ShouldBe(["billing", "draft", "helpdesk"]);
        items.Single(item => item.Id == billing).IsActive.ShouldBeTrue();
        items.Single(item => item.Id == draft).IsActive.ShouldBeFalse("registered, no catalog yet");
        items.Single(item => item.Id == helpdesk).IsActive.ShouldBeFalse("withdrawn");
    }

    [Fact]
    public async Task APP06_EachProductShowsHowManyPermissionsItHas()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var withCatalog = await RegisterAsync(adminToken, "billing", "Billing", ct, activate: true);
        var without = await RegisterAsync(adminToken, "draft", "Draft", ct, activate: false);
        (await PostAsync($"{ApplicationAdminEndpoints.Route}/{withCatalog}/permissions",
            new RegisterPermissionsRequest([new PermissionInput("invoice.read", "See invoices", null)]), adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        var items = await ListAsync(adminToken, ct);

        items.Single(item => item.Id == withCatalog).PermissionCount.ShouldBe(2);
        items.Single(item => item.Id == without).PermissionCount.ShouldBe(0);
    }

    [Fact]
    public async Task APP06_TheListShowsNoSecretMaterial()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "billing", "Billing", ct, activate: true);

        var body = await (await GetAsync(ApplicationAdminEndpoints.Route, adminToken, ct)).Content.ReadAsStringAsync(ct);

        var item = JsonNode.Parse(body)!.AsArray().Single()!.AsObject();
        item.Select(property => property.Key).Order().ShouldBe(["createdAt", "id", "isActive", "key", "name", "permissionCount"]);
        body.ShouldNotContain("billing-api", customMessage: "the audience stays with the product");
    }

    [Fact]
    public async Task APP06_APersonWhoIsNotAnAdminIsRefusedAndNoTokenIsUnauthorised()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "billing", "Billing", ct, activate: true);
        await SignUpVerifiedAsync("Acme Ltd", TenantEmail, ct);
        var tenantToken = (await LoginForTokensAsync(TenantEmail, ct)).AccessToken;

        var refused = await GetAsync(ApplicationAdminEndpoints.Route, tenantToken, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(ct)).ShouldNotContain("billing");
        (await GetAsync(ApplicationAdminEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<List<AdminApplicationItem>> ListAsync(string token, CancellationToken ct) =>
        (await (await GetAsync(ApplicationAdminEndpoints.Route, token, ct)).Content.ReadFromJsonAsync<List<AdminApplicationItem>>(Json, ct))!;

    private Task<HttpResponseMessage> SetActiveAsync(string token, Guid applicationId, bool isActive, CancellationToken ct) =>
        PutAsync($"{ApplicationAdminEndpoints.Route}/{applicationId}/active", new SetApplicationActiveRequest(isActive), token, ct);

    /// <summary>Registers a product and, when asked, gives it a one-permission catalog and activates it, as a platform admin would.</summary>
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
