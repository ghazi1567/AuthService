using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.App02;

/// <summary>
/// APP-02 (AUTH-16) Register Permission Catalog for Application, through the real
/// <c>POST /api/auth/admin/applications/{id}/permissions</c> and <c>PUT /api/auth/admin/applications/{id}/active</c> with the
/// access token of a platform admin.
/// Test method names start with their Test Case ID (APP_TC4..APP_TC6) per the workbook convention.
/// </summary>
public sealed class PermissionCatalogTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task APP_TC4_ACatalogRegisteredForANewApplicationLetsItBeActivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var response = await RegisterPermissionsAsync(token, application.Id, ct,
            Permission("campaign.create", "Create campaigns", "Start a new campaign."),
            Permission("campaign.send", "Send campaigns", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
        body.ApplicationId.ShouldBe(application.Id);
        (body.Created, body.Updated, body.Unchanged).ShouldBe((2, 0, 0));
        body.Permissions.Select(permission => permission.Key).ShouldBe(["campaign.create", "campaign.send"]);
        body.Permissions[0].Label.ShouldBe("Create campaigns");
        body.Permissions[0].Description.ShouldBe("Start a new campaign.");

        // The product starts inactive, and now that it has a catalog it can be switched on.
        var activate = await SetActiveAsync(token, application.Id, true, ct);
        activate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await activate.Content.ReadFromJsonAsync<ApplicationStatusResponse>(Json, ct))!.IsActive.ShouldBeTrue();

        await WithDbAsync(async db =>
        {
            (await db.Applications.AsNoTracking().Where(application => !application.IsBuiltIn).SingleAsync(ct)).IsActive.ShouldBeTrue();
            (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(2);

            var registered = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.PermissionsRegistered, ct);
            registered.ActorId.ShouldBe(admin.UserId);
            registered.Detail.ShouldBe("whatsapp-saas");
            (await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.ApplicationActivated, ct))
                .Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact]
    public async Task APP_TC5_RegisteringAPermissionCatalogForAnUnknownApplicationIsRejectedWith404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await RegisterPermissionsAsync(token, Guid.NewGuid(), ct, Permission("campaign.create", "Create campaigns", null));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_found");
        await WithDbAsync(async db => (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task APP_TC6_ReRegisteringAnExistingKeyUpdatesItInPlaceAndKeepsItsIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        var first = await RegisterPermissionsForAsync(token, application.Id, ct, Permission("campaign.create", "Create campaigns", "Old text."));
        var originalId = first.Permissions.Single().Id;

        var response = await RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.create", "Create a campaign", "New text."));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "nothing new was added, so this is not a 201");
        var body = (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
        (body.Created, body.Updated, body.Unchanged).ShouldBe((0, 1, 0));

        // A role grants a permission by its Id (ROL-01). The same row, with a new label, means no grant is lost.
        body.Permissions.Single().Id.ShouldBe(originalId);
        await WithDbAsync(async db =>
        {
            var permission = await db.Permissions.AsNoTracking().Where(permission => permission.ApplicationId != BuiltInApplication.Id).SingleAsync(ct);
            permission.Id.ShouldBe(originalId);
            permission.Label.ShouldBe("Create a campaign");
            permission.Description.ShouldBe("New text.");
            permission.UpdatedAt.ShouldBeGreaterThan(permission.CreatedAt);
        });
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task APP02_RepeatingTheSameCatalogChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        var permission = Permission("campaign.create", "Create campaigns", "Start a new campaign.");
        await RegisterPermissionsForAsync(token, application.Id, ct, permission);
        var before = await QueryDbAsync(db => db.Permissions.AsNoTracking().Where(permission => permission.ApplicationId != BuiltInApplication.Id).SingleAsync(ct));

        var response = await RegisterPermissionsAsync(token, application.Id, ct, permission);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
        (body.Created, body.Updated, body.Unchanged).ShouldBe((0, 0, 1));
        var after = await QueryDbAsync(db => db.Permissions.AsNoTracking().Where(permission => permission.ApplicationId != BuiltInApplication.Id).SingleAsync(ct));
        after.UpdatedAt.ShouldBe(before.UpdatedAt, "an unchanged permission is not touched");
    }

    [Fact]
    public async Task APP02_AKeyOnlyNeedsToBeUniqueWithinItsOwnApplication()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var first = await RegisterApplicationAsync(token, "product-one", ct);
        var second = await RegisterApplicationAsync(token, "product-two", ct);

        (await RegisterPermissionsAsync(token, first.Id, ct, Permission("report.export", "Export reports", null))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        (await RegisterPermissionsAsync(token, second.Id, ct, Permission("report.export", "Export reports", null))).StatusCode
            .ShouldBe(HttpStatusCode.Created, "the same key in another product is a different permission");

        await WithDbAsync(async db =>
        {
            var permissions = await db.Permissions.AsNoTracking().Where(permission => permission.ApplicationId != BuiltInApplication.Id).ToListAsync(ct);
            permissions.Select(permission => permission.ApplicationId).ShouldBe([first.Id, second.Id], ignoreOrder: true);
            permissions.Select(permission => permission.Id).Distinct().Count().ShouldBe(2);
        });
    }

    [Fact]
    public async Task APP02_RegisteringMoreKeysLaterAddsToTheCatalogAndNeverRemovesOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        await RegisterPermissionsForAsync(token, application.Id, ct, Permission("campaign.create", "Create campaigns", null));

        var response = await RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.send", "Send campaigns", null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
        (body.Created, body.Updated).ShouldBe((1, 0));
        body.Permissions.Select(permission => permission.Key).ShouldBe(["campaign.create", "campaign.send"],
            "the catalog is the whole list, not just what this request carried");
    }

    [Fact]
    public async Task APP02_TheKeyIsTrimmedAndLowercased()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        await RegisterPermissionsForAsync(token, application.Id, ct, Permission("campaign.create", "Create campaigns", null));

        var response = await RegisterPermissionsAsync(token, application.Id, ct, Permission("  Campaign.Create ", "  Create a campaign  ", "   "));

        var body = (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
        (body.Created, body.Updated).ShouldBe((0, 1), "the differently cased key is the same permission");
        body.Permissions.Single().Label.ShouldBe("Create a campaign");
        body.Permissions.Single().Description.ShouldBeNull("a blank description is stored as none");
    }

    [Fact]
    public async Task APP02_SimultaneousRegistrationsOfTheSameNewKeyCreateExactlyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var responses = await Task.WhenAll(
            RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.create", "One", null)),
            RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.create", "Two", null)),
            RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.create", "Three", null)));

        responses.ShouldAllBe(response => response.IsSuccessStatusCode);
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        await WithDbAsync(async db => (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task APP02_AnApplicationWithNoPermissionsCannotBeActivated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var response = await SetActiveAsync(token, application.Id, true, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("no_permissions_registered");
        await WithDbAsync(async db => (await db.Applications.AsNoTracking().Where(application => !application.IsBuiltIn).SingleAsync(ct)).IsActive.ShouldBeFalse());
    }

    [Fact]
    public async Task APP02_ActivatingAnUnknownApplicationIsRejectedWith404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var response = await SetActiveAsync(token, Guid.NewGuid(), true, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_found");
    }

    [Fact]
    public async Task APP02_AnApplicationCanBeDeactivatedAgainAndActivationIsRepeatable()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        await RegisterPermissionsForAsync(token, application.Id, ct, Permission("campaign.create", "Create campaigns", null));

        (await SetActiveAsync(token, application.Id, true, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SetActiveAsync(token, application.Id, true, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "repeating it is harmless");
        var deactivate = await SetActiveAsync(token, application.Id, false, ct);

        deactivate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await deactivate.Content.ReadFromJsonAsync<ApplicationStatusResponse>(Json, ct))!.IsActive.ShouldBeFalse();
        await WithDbAsync(async db =>
        {
            (await db.Applications.AsNoTracking().Where(application => !application.IsBuiltIn).SingleAsync(ct)).IsActive.ShouldBeFalse();

            // Only the actual changes are audited: one activation, one deactivation.
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.ApplicationActivated, ct)).ShouldBe(1);
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.ApplicationDeactivated, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task APP02_ActivationRequiresAnExplicitFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var response = await PutAsync($"{ApplicationAdminEndpoints.Route}/{application.Id}/active", new { }, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task APP02_ANonAdminCannotRegisterPermissionsOrActivate()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(adminToken, "whatsapp-saas", ct);
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var tenantToken = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;

        foreach (var token in new string?[] { tenantToken, null })
        {
            var expected = token is null ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden;
            (await RegisterPermissionsAsync(token, application.Id, ct, Permission("campaign.create", "Create campaigns", null)))
                .StatusCode.ShouldBe(expected);
            (await SetActiveAsync(token, application.Id, true, ct)).StatusCode.ShouldBe(expected);
        }

        await WithDbAsync(async db =>
        {
            (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(0);
            (await db.Applications.AsNoTracking().Where(application => !application.IsBuiltIn).SingleAsync(ct)).IsActive.ShouldBeFalse();
        });
    }

    [Fact]
    public async Task APP02_Validation_AnEmptyOrMissingListIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);
        var route = $"{ApplicationAdminEndpoints.Route}/{application.Id}/permissions";

        (await PostAsync(route, new { permissions = Array.Empty<object>() }, token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PostAsync(route, new { }, token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null, "Label")]
    [InlineData("   ", "Label")]
    [InlineData("has space", "Label")]
    [InlineData(".leading-dot", "Label")]
    [InlineData("trailing-dot.", "Label")]
    [InlineData("bad|char", "Label")]
    [InlineData("campaign.create", null)]
    [InlineData("campaign.create", "   ")]
    public async Task APP02_Validation_AnInvalidPermissionRejectsTheWholeRequestWith400(string? key, string? label)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var response = await RegisterPermissionsAsync(token, application.Id, ct,
            Permission("valid.one", "A valid permission", null), Permission(key, label, null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(0, "a rejected request registers nothing, not even its valid entries"));
    }

    [Fact]
    public async Task APP02_Validation_ADuplicateKeyInOneRequestIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var response = await RegisterPermissionsAsync(token, application.Id, ct,
            Permission("campaign.create", "One", null), Permission("Campaign.Create", "Two", null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Permissions.Where(permission => permission.ApplicationId != BuiltInApplication.Id).CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task APP02_Validation_OverlongValuesAreRejectedWith400AndTheLimitsAreAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        (await RegisterPermissionsAsync(token, application.Id, ct,
            Permission(new string('a', PermissionCatalogValidator.MaxKeyLength + 1), "Label", null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RegisterPermissionsAsync(token, application.Id, ct,
            Permission("key", new string('l', PermissionCatalogValidator.MaxLabelLength + 1), null))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await RegisterPermissionsAsync(token, application.Id, ct,
            Permission("key", "Label", new string('d', PermissionCatalogValidator.MaxDescriptionLength + 1)))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await RegisterPermissionsAsync(token, application.Id, ct,
            Permission(new string('a', PermissionCatalogValidator.MaxKeyLength), new string('l', PermissionCatalogValidator.MaxLabelLength),
                new string('d', PermissionCatalogValidator.MaxDescriptionLength)))).StatusCode
            .ShouldBe(HttpStatusCode.Created, "values exactly at the limits are fine");
    }

    [Fact]
    public async Task APP02_Validation_TooManyPermissionsInOneRequestIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var application = await RegisterApplicationAsync(token, "whatsapp-saas", ct);

        var tooMany = Enumerable.Range(0, PermissionCatalogValidator.MaxPermissionsPerRequest + 1)
            .Select(index => Permission($"permission.{index}", "Label", null)).ToArray();

        (await RegisterPermissionsAsync(token, application.Id, ct, tooMany)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private static PermissionInput Permission(string? key, string? label, string? description) => new(key, label, description);

    private Task<HttpResponseMessage> RegisterPermissionsAsync(string? token, Guid applicationId, CancellationToken ct, params PermissionInput[] permissions) =>
        PostAsync($"{ApplicationAdminEndpoints.Route}/{applicationId}/permissions", new RegisterPermissionsRequest(permissions), token, ct);

    private async Task<RegisterPermissionsResponse> RegisterPermissionsForAsync(
        string token, Guid applicationId, CancellationToken ct, params PermissionInput[] permissions)
    {
        var response = await RegisterPermissionsAsync(token, applicationId, ct, permissions);
        response.IsSuccessStatusCode.ShouldBeTrue();
        return (await response.Content.ReadFromJsonAsync<RegisterPermissionsResponse>(Json, ct))!;
    }

    private Task<HttpResponseMessage> SetActiveAsync(string? token, Guid applicationId, bool isActive, CancellationToken ct) =>
        PutAsync($"{ApplicationAdminEndpoints.Route}/{applicationId}/active", new SetApplicationActiveRequest(isActive), token, ct);

    private async Task<RegisterApplicationResponse> RegisterApplicationAsync(string token, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
    }

    private async Task<T> QueryDbAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> action)
    {
        T result = default!;
        await WithDbAsync(async db => result = await action(db));
        return result;
    }
}
