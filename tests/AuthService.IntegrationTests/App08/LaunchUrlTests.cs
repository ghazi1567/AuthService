using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.App08;

/// <summary>
/// APP-08 (AUTH-258) A platform admin sets where a product is opened from the console, through the real
/// <c>PUT /api/auth/admin/applications/{id}/launch-url</c>. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class LaunchUrlTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string TenantEmail = "owner@acme.com";

    [Fact]
    public async Task APP08_AProductHasNoLaunchUrlUntilOneIsSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await RegisterAsync(adminToken, "helpdesk", ct);

        (await ListAsync(adminToken, ct)).ShouldHaveSingleItem().LaunchUrl.ShouldBeNull();
    }

    [Fact]
    public async Task APP08_AnAddressIsSetShownInTheListAndCanBeClearedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);

        var set = await PutAsync(Route(id), new { url = "https://helpdesk.example.com/signed-in?from=auth" }, adminToken, ct);

        set.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await set.Content.ReadFromJsonAsync<LaunchUrlResponse>(Json, ct))!.LaunchUrl.ShouldBe("https://helpdesk.example.com/signed-in?from=auth");
        (await ListAsync(adminToken, ct)).Single().LaunchUrl.ShouldBe("https://helpdesk.example.com/signed-in?from=auth");

        var cleared = await PutAsync(Route(id), new { url = (string?)null }, adminToken, ct);

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ListAsync(adminToken, ct)).Single().LaunchUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData("http://helpdesk.example.com/in", "https")]
    [InlineData("helpdesk.example.com/in", "https://")]
    [InlineData("javascript:alert(1)", "https://")]
    [InlineData("ftp://helpdesk.example.com/in", "https://")]
    [InlineData("https://user:pass@helpdesk.example.com/in", "user name or password")]
    [InlineData("https://helpdesk.example.com/in#token", "fragment")]
    public async Task APP08_AddressesThatAreNotPlainHttpsPagesAreRefusedAndNothingChanges(string url, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);
        await PutAsync(Route(id), new { url = "https://helpdesk.example.com/ok" }, adminToken, ct);

        var response = await PutAsync(Route(id), new { url }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(reason);
        (await ListAsync(adminToken, ct)).Single().LaunchUrl.ShouldBe("https://helpdesk.example.com/ok", "a refused address changes nothing");
    }

    [Fact]
    public async Task APP08_AnAddressThatIsTooLongIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);

        var response = await PutAsync(Route(id), new { url = "https://helpdesk.example.com/" + new string('a', LaunchUrlValidator.MaxLength) }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("http://localhost:5100/signed-in")]
    [InlineData("http://127.0.0.1:5100/signed-in")]
    public async Task APP08_PlainHttpIsAcceptedOnlyForThisMachine(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);

        (await PutAsync(Route(id), new { url }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task APP08_SettingTheAddressIsRecordedInTheAuditLogUnderTheAdminWithTheProductKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);

        await PutAsync(Route(id), new { url = "https://helpdesk.example.com/signed-in" }, adminToken, ct);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApplicationLaunchUrlSet, ct);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("helpdesk");
        });
    }

    [Fact]
    public async Task APP08_AnUnknownProductAndTheBuiltInProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await PutAsync(Route(Guid.NewGuid()), new { url = "https://x.example.com/in" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PutAsync(Route(BuiltInApplication.Id), new { url = "https://x.example.com/in" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task APP08_APersonWhoIsNotAnAdminIsRefusedAndNoTokenIsUnauthorised()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "helpdesk", ct);
        await SignUpVerifiedAsync("Acme Ltd", TenantEmail, ct);
        var tenantToken = (await LoginForTokensAsync(TenantEmail, ct)).AccessToken;

        (await PutAsync(Route(id), new { url = "https://evil.example.com/in" }, tenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(Route(id), new { url = "https://evil.example.com/in" }, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ListAsync(adminToken, ct)).Single().LaunchUrl.ShouldBeNull("a refused request changes nothing");
    }

    private static string Route(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/launch-url";

    private async Task<List<AdminApplicationItem>> ListAsync(string token, CancellationToken ct) =>
        (await (await GetAsync(ApplicationAdminEndpoints.Route, token, ct)).Content.ReadPageItemsAsync<AdminApplicationItem>(Json, ct))!;

    private async Task<Guid> RegisterAsync(string adminToken, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;
    }
}
