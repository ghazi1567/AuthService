using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.BuiltIn;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.App07;

/// <summary>
/// APP-07 (AUTH-257) A platform admin reads a product's permission catalog, through the real
/// <c>GET /api/auth/admin/applications/{id}/permissions</c>. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class AdminPermissionCatalogReadTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string TenantEmail = "owner@acme.com";

    [Fact]
    public async Task APP07_AJustRegisteredProductHasAnEmptyCatalog()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "draft", ct);

        var response = await GetAsync(CatalogRoute(id), adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<List<AdminPermissionItem>>(Json, ct))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task APP07_TheCatalogIsReadByKeyEvenWhileTheProductIsNotActive()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "draft", ct);
        (await PostAsync(CatalogRoute(id), new RegisterPermissionsRequest(
        [
            new PermissionInput("ticket.reply", "Answer tickets", "Reply to a customer"),
            new PermissionInput("campaign.create", "Create campaigns", null),
        ]), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var items = (await (await GetAsync(CatalogRoute(id), adminToken, ct)).Content.ReadFromJsonAsync<List<AdminPermissionItem>>(Json, ct))!;

        items.Select(item => item.Key).ShouldBe(["campaign.create", "ticket.reply"]);
        items[1].Label.ShouldBe("Answer tickets");
        items[1].Description.ShouldBe("Reply to a customer");
        items[0].Description.ShouldBeNull();
    }

    [Fact]
    public async Task APP07_ARelabelledPermissionShowsItsNewLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "draft", ct);
        await PostAsync(CatalogRoute(id), new RegisterPermissionsRequest([new PermissionInput("ticket.reply", "Answer tickets", null)]), adminToken, ct);
        await PostAsync(CatalogRoute(id), new RegisterPermissionsRequest([new PermissionInput("ticket.reply", "Reply to tickets", null)]), adminToken, ct);

        var items = (await (await GetAsync(CatalogRoute(id), adminToken, ct)).Content.ReadFromJsonAsync<List<AdminPermissionItem>>(Json, ct))!;

        items.ShouldHaveSingleItem().Label.ShouldBe("Reply to tickets");
    }

    [Fact]
    public async Task APP07_AnUnknownProductAndTheBuiltInProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await GetAsync(CatalogRoute(Guid.NewGuid()), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var builtIn = await GetAsync(CatalogRoute(BuiltInApplication.Id), adminToken, ct);

        builtIn.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await builtIn.Content.ReadAsStringAsync(ct)).ShouldContain("application_not_found");
    }

    [Fact]
    public async Task APP07_APersonWhoIsNotAnAdminIsRefusedAndNoTokenIsUnauthorised()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "draft", ct);
        await PostAsync(CatalogRoute(id), new RegisterPermissionsRequest([new PermissionInput("ticket.reply", "Answer tickets", null)]), adminToken, ct);
        await SignUpVerifiedAsync("Acme Ltd", TenantEmail, ct);
        var tenantToken = (await LoginForTokensAsync(TenantEmail, ct)).AccessToken;

        var refused = await GetAsync(CatalogRoute(id), tenantToken, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(ct)).ShouldNotContain("ticket.reply");
        (await GetAsync(CatalogRoute(id), null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string CatalogRoute(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/permissions";

    private async Task<Guid> RegisterAsync(string adminToken, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;
    }
}
