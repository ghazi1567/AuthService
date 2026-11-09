using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Onb01;

/// <summary>
/// ONB-01 (AUTH-272) A platform admin registers a product together with its catalogue, launch address and activation in one call, and
/// renames a product afterwards. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class AddProductAndRenameTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string TenantEmail = "owner@acme.com";

    private static readonly object[] Catalogue =
    [
        new { key = "Campaign.View", label = "View campaigns", description = "See campaigns and their results" },
        new { key = "campaign.send", label = "Send campaigns" },
        new { key = "contact.view", label = "View contacts" },
    ];

    [Fact]
    public async Task ONB01_TheOldThreeFieldRegistrationStillWorksAndLeavesTheProductInactiveWithNothingSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api" }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        body.IsActive.ShouldBeFalse();
        body.LaunchUrl.ShouldBeNull();
        body.PermissionCount.ShouldBe(0);
        (await ListAsync(adminToken, ct)).Single().PermissionCount.ShouldBe(0);
    }

    [Fact]
    public async Task ONB01_OneCallRegistersTheProductItsCatalogueItsLaunchAddressAndMakesItAvailable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new
            {
                key = "marketing",
                name = "Marketing",
                jwtAudience = "marketing-api",
                launchUrl = "http://localhost:5080/api/session/launch",
                permissions = Catalogue,
                activate = true,
            },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue("the response carries the client secret");
        var body = (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        body.IsActive.ShouldBeTrue();
        body.LaunchUrl.ShouldBe("http://localhost:5080/api/session/launch");
        body.PermissionCount.ShouldBe(3);
        body.ClientSecret.ShouldNotBeNullOrWhiteSpace();

        var listed = (await ListAsync(adminToken, ct)).Single();
        listed.IsActive.ShouldBeTrue();
        listed.PermissionCount.ShouldBe(3);
        listed.LaunchUrl.ShouldBe("http://localhost:5080/api/session/launch");

        var catalogue = (await (await GetAsync($"{ApplicationAdminEndpoints.Route}/{body.Id}/permissions", adminToken, ct)).Content
            .ReadPageItemsAsync<AdminPermissionItem>(Json, ct))!;
        catalogue.Select(item => item.Key).ShouldBe(["campaign.send", "campaign.view", "contact.view"], "keys are lowercased on the way in");
        catalogue.Single(item => item.Key == "campaign.view").Description.ShouldBe("See campaigns and their results");
    }

    [Fact]
    public async Task ONB01_ACatalogueWithoutActivateLeavesTheProductInactive()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", permissions = Catalogue },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var listed = (await ListAsync(adminToken, ct)).Single();
        listed.IsActive.ShouldBeFalse();
        listed.PermissionCount.ShouldBe(3);
        listed.LaunchUrl.ShouldBeNull();
    }

    [Fact]
    public async Task ONB01_AnEmptyCatalogueIsTheSameAsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", permissions = Array.Empty<object>() },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ListAsync(adminToken, ct)).Single().PermissionCount.ShouldBe(0);
    }

    [Fact]
    public async Task ONB01_ActivatingWithoutAnyPermissionIsRefusedAndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", activate = true },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("at least one permission");
        (await ListAsync(adminToken, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ONB01_ABadPermissionKeyRefusesTheWholeCallSoNoHalfSetUpProductIsLeftBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new
            {
                key = "marketing",
                name = "Marketing",
                jwtAudience = "marketing-api",
                permissions = new object[] { new { key = "campaign.view", label = "View" }, new { key = "Not A Key!", label = "Broken" } },
                activate = true,
            },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("Permissions[1].Key");
        (await ListAsync(adminToken, ct)).ShouldBeEmpty();
        await WithDbAsync(async db => (await db.Permissions.CountAsync(permission => permission.ApplicationId != BuiltInApplication.Id, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ONB01_ABadLaunchAddressRefusesTheWholeCallAndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", launchUrl = "http://marketing.example.com/in", permissions = Catalogue },
            adminToken,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("LaunchUrl");
        (await ListAsync(adminToken, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ONB01_ATakenKeyIsStillAConflictAndTheExistingProductKeepsItsCatalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        await PostAsync(ApplicationAdminEndpoints.Route, new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", permissions = Catalogue }, adminToken, ct);

        var again = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Other", jwtAudience = "other-api", permissions = new object[] { new { key = "x.y", label = "X" } } },
            adminToken,
            ct);

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var only = (await ListAsync(adminToken, ct)).Single();
        only.Name.ShouldBe("Marketing");
        only.PermissionCount.ShouldBe(3);
    }

    [Fact]
    public async Task ONB01_TheOneCallLeavesTheSameAuditEntriesAsTheSeparateCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", launchUrl = "https://app.example.com/launch", permissions = Catalogue, activate = true },
            adminToken,
            ct);

        await WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.AsNoTracking().Where(entry => entry.Detail == "marketing").ToListAsync(ct);
            entries.Select(entry => entry.Action).ShouldBe(
                [AuditActions.ApplicationRegistered, AuditActions.PermissionsRegistered, AuditActions.ApplicationLaunchUrlSet, AuditActions.ApplicationActivated],
                ignoreOrder: true);
            entries.ShouldAllBe(entry => entry.ActorId == admin.UserId);
        });
    }

    [Fact]
    public async Task ONB01_RenamingChangesTheNameOnlyAndKeepsTheKeyAndAudience()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "marketing", ct);

        var response = await PutAsync(RenameRoute(id), new { name = "  Staff Link Marketing  " }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<RenameApplicationResponse>(Json, ct))!;
        body.Name.ShouldBe("Staff Link Marketing", "the name is trimmed");
        body.Key.ShouldBe("marketing");
        (await ListAsync(adminToken, ct)).Single().Name.ShouldBe("Staff Link Marketing");
        await WithDbAsync(async db => (await db.Applications.AsNoTracking().SingleAsync(application => application.Id == id, ct)).JwtAudience.ShouldBe("marketing-api"));
    }

    [Fact]
    public async Task ONB01_RenamingIsAuditedUnderTheAdminWithTheKeyAndSayingTheSameNameAgainIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "marketing", ct);

        await PutAsync(RenameRoute(id), new { name = "Staff Link Marketing" }, adminToken, ct);
        await PutAsync(RenameRoute(id), new { name = "Staff Link Marketing" }, adminToken, ct);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.ApplicationRenamed, ct);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("marketing");
        });
    }

    [Theory]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    public async Task ONB01_ABlankNameIsRefusedAndTheNameIsUnchanged(string name, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "marketing", ct);

        var response = await PutAsync(RenameRoute(id), new { name }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(reason);
        (await ListAsync(adminToken, ct)).Single().Name.ShouldBe("marketing");
    }

    [Fact]
    public async Task ONB01_ANameThatIsTooLongIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "marketing", ct);

        var response = await PutAsync(RenameRoute(id), new { name = new string('a', ApplicationValidator.MaxNameLength + 1) }, adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ONB01_AnUnknownProductAndTheBuiltInProductCannotBeRenamed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await PutAsync(RenameRoute(Guid.NewGuid()), new { name = "X" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await PutAsync(RenameRoute(BuiltInApplication.Id), new { name = "X" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ONB01_OnlyAPlatformAdminCanRenameOrRegisterWithACatalogue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var id = await RegisterAsync(adminToken, "marketing", ct);
        await SignUpVerifiedAsync("Acme Ltd", TenantEmail, ct);
        var tenantToken = (await LoginForTokensAsync(TenantEmail, ct)).AccessToken;
        var body = new { key = "sneaky", name = "Sneaky", jwtAudience = "sneaky-api", permissions = Catalogue, activate = true };

        (await PutAsync(RenameRoute(id), new { name = "Hijacked" }, tenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(RenameRoute(id), new { name = "Hijacked" }, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PostAsync(ApplicationAdminEndpoints.Route, body, tenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync(ApplicationAdminEndpoints.Route, body, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var only = (await ListAsync(adminToken, ct)).Single();
        only.Name.ShouldBe("marketing");
    }

    private static string RenameRoute(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/name";

    private async Task<List<AdminApplicationItem>> ListAsync(string token, CancellationToken ct) =>
        (await (await GetAsync(ApplicationAdminEndpoints.Route, token, ct)).Content.ReadPageItemsAsync<AdminApplicationItem>(Json, ct))!;

    private async Task<Guid> RegisterAsync(string adminToken, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!.Id;
    }
}
