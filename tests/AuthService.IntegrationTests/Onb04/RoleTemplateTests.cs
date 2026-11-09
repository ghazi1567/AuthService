using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Onb04;

/// <summary>
/// ONB-04 (AUTH-275) The roles a product ships with: a platform admin keeps them, they become ordinary roles in an organisation the first
/// time it subscribes, and a copy is never overwritten. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class RoleTemplateTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";
    private const string SecondEmail = "owner@globex.com";

    private static readonly object[] Catalogue =
    [
        new { key = "campaign.view", label = "View campaigns" },
        new { key = "campaign.send", label = "Send campaigns" },
        new { key = "contact.view", label = "View contacts" },
    ];

    private static readonly object[] Marketer =
    [
        new { name = "Marketer", description = "Builds and sends campaigns", permissions = new object[] { new { key = "campaign.view", level = "ReadOnly" }, new { key = "campaign.send", level = "CreateRead" } } },
        new { name = "Analyst", description = (string?)null, permissions = new object[] { new { key = "contact.view", level = "ReadOnly" }, new { key = "campaign.view", level = "ReadOnly" } } },
    ];

    [Fact]
    public async Task ONB04_AnAdminSetsAProductsRoleTemplatesAndReadsThemBackInOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);

        var set = await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);

        set.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await set.Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;
        stored.Roles.Select(role => role.Name).ShouldBe(["Analyst", "Marketer"]);
        stored.Roles[0].Permissions.ShouldBe([new RoleTemplateGrantDto("campaign.view", "ReadOnly"), new RoleTemplateGrantDto("contact.view", "ReadOnly")]);
        stored.Roles[1].Description.ShouldBe("Builds and sends campaigns");
        stored.Roles[1].Permissions.ShouldBe([new RoleTemplateGrantDto("campaign.send", "CreateRead"), new RoleTemplateGrantDto("campaign.view", "ReadOnly")]);

        var read = (await (await GetAsync(Route(world.Product.Id), world.AdminToken, ct)).Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;
        read.Roles.Select(role => role.Name).ShouldBe(["Analyst", "Marketer"]);
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleTemplatesSet, ct);
            entry.ActorId.ShouldBe(world.AdminId);
            entry.Detail.ShouldBe("marketing");
        });
    }

    [Fact]
    public async Task ONB04_SettingTheSetAgainChangesTemplatesInPlaceAndRemovesTheOnesLeftOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var first = (await (await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct)).Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;
        var marketerId = first.Roles.Single(role => role.Name == "Marketer").Id;

        var again = await PutAsync(
            Route(world.Product.Id),
            new
            {
                roles = new object[]
                {
                    new { name = "marketer", description = "Changed", permissions = new object[] { new { key = "contact.view", level = "Full" } } },
                    new { name = "Agent", permissions = Array.Empty<object>() },
                },
            },
            world.AdminToken,
            ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = (await again.Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;
        stored.Roles.Select(role => role.Name).ToList().ShouldBe(["Agent", "marketer"], customMessage: "Analyst was left out, so it is gone; the name match ignores case and the spelling just sent is kept");
        var marketer = stored.Roles.Single(role => role.Name == "marketer");
        marketer.Id.ShouldBe(marketerId, "changed in place");
        marketer.Description.ShouldBe("Changed");
        marketer.Permissions.ShouldBe([new RoleTemplateGrantDto("contact.view", "Full")]);
        stored.Roles.Single(role => role.Name == "Agent").Permissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ONB04_AnEmptyListRemovesEveryTemplateAndAMissingListIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);

        var missing = await PutAsync(Route(world.Product.Id), new { }, world.AdminToken, ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync(ct)).ShouldContain("Roles is required");
        (await ReadTemplatesAsync(world, ct)).Roles.Count.ShouldBe(2, "a refused request changes nothing");

        var cleared = await PutAsync(Route(world.Product.Id), new { roles = Array.Empty<object>() }, world.AdminToken, ct);

        cleared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadTemplatesAsync(world, ct)).Roles.ShouldBeEmpty();
        await WithDbAsync(async db => (await db.RoleTemplatePermissions.CountAsync(ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("", "Roles[0].Name")]
    [InlineData("   ", "Roles[0].Name")]
    [InlineData("Owner", "Roles[0].Name")]
    [InlineData("OWNER", "Roles[0].Name")]
    public async Task ONB04_ABlankNameOrTheBuiltInOwnerNameIsRefused(string name, string field)
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);

        var response = await PutAsync(Route(world.Product.Id), new { roles = new object[] { new { name, permissions = Array.Empty<object>() } } }, world.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain(field);
        (await ReadTemplatesAsync(world, ct)).Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task ONB04_ADuplicateNameATooLongNameAnUnknownKeyABadLevelAndARepeatedPermissionAreEachNamedByPosition()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var body = new
        {
            roles = new object[]
            {
                new { name = "Marketer", permissions = Array.Empty<object>() },
                new { name = "marketer", permissions = Array.Empty<object>() },
                new { name = new string('a', 101), permissions = Array.Empty<object>() },
                new { name = "Typo", permissions = new object[] { new { key = "campaign.sned", level = "Full" } } },
                new { name = "Level", permissions = new object[] { new { key = "campaign.view", level = "Admin" }, new { key = "campaign.view", level = "7" } } },
                new { name = "Repeat", permissions = new object[] { new { key = "campaign.view", level = "ReadOnly" }, new { key = "Campaign.View", level = "Full" } } },
            },
        };

        var response = await PutAsync(Route(world.Product.Id), body, world.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var text = await response.Content.ReadAsStringAsync(ct);
        text.ShouldContain("Roles[1].Name");
        text.ShouldContain("Roles[2].Name");
        text.ShouldContain("Roles[4].Permissions[0].Level");
        text.ShouldContain("Roles[4].Permissions[1].Level");
        text.ShouldContain("Roles[5].Permissions[1].Key");
        (await ReadTemplatesAsync(world, ct)).Roles.ShouldBeEmpty("nothing is stored when anything is wrong");

        // The shape is fine here, so the catalogue is what is checked, and the typo is named.
        var typo = await PutAsync(Route(world.Product.Id), new { roles = new object[] { body.roles[3] } }, world.AdminToken, ct);
        typo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await typo.Content.ReadAsStringAsync(ct)).ShouldContain("Unknown permission 'campaign.sned'");
    }

    [Fact]
    public async Task ONB04_TooManyRolesAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var roles = Enumerable.Range(0, RoleTemplateValidator.MaxTemplates + 1).Select(i => (object)new { name = $"Role {i}", permissions = Array.Empty<object>() }).ToArray();

        var response = await PutAsync(Route(world.Product.Id), new { roles }, world.AdminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("At most");
    }

    [Fact]
    public async Task ONB04_AnOrganisationThatSubscribesGetsTheProductsRolesAsOrdinaryRolesOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        (await SubscribeAsync(world, owner.TenantId, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var roles = await RolesOfAsync(owner.TenantId, ct);
        roles.Keys.Order().ShouldBe(["Analyst", "Marketer", Role.OwnerName]);
        roles["Marketer"].ShouldBe([("campaign.send", AccessLevel.CreateRead), ("campaign.view", AccessLevel.ReadOnly)]);
        roles["Analyst"].ShouldBe([("campaign.view", AccessLevel.ReadOnly), ("contact.view", AccessLevel.ReadOnly)]);
        await WithDbAsync(async db =>
        {
            (await db.Roles.AsNoTracking().Where(role => role.TenantId == owner.TenantId && role.Name != Role.OwnerName).AllAsync(role => !role.IsBuiltIn, ct)).ShouldBeTrue();
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleTemplatesApplied, ct);
            entry.ActorType.ShouldBe(AuditActorType.Product);
            entry.TenantId.ShouldBe(owner.TenantId);
            entry.Detail.ShouldBe("marketing");
        });
    }

    [Fact]
    public async Task ONB04_ACopyIsNeverOverwrittenSubscribingAgainAddsNothingAndADeletedRoleStaysDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(world, owner.TenantId, ct);

        // The organisation's admins change one copy and delete the other.
        await WithDbAsync(async db =>
        {
            var marketer = await db.Roles.SingleAsync(role => role.TenantId == owner.TenantId && role.Name == "Marketer", ct);
            await db.RolePermissions.Where(grant => grant.RoleId == marketer.Id).ExecuteDeleteAsync(ct);
            var analyst = await db.Roles.SingleAsync(role => role.TenantId == owner.TenantId && role.Name == "Analyst", ct);
            await db.RolePermissions.Where(grant => grant.RoleId == analyst.Id).ExecuteDeleteAsync(ct);
            await db.Roles.Where(role => role.Id == analyst.Id).ExecuteDeleteAsync(ct);
        });
        var again = await SubscribeAsync(world, owner.TenantId, ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(owner.TenantId, world.Product.Id), """{"status":"Suspended"}""", world.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var back = await SubscribeAsync(world, owner.TenantId, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        back.StatusCode.ShouldBe(HttpStatusCode.OK, "an existing subscription is active again, not a new one");
        var roles = await RolesOfAsync(owner.TenantId, ct);
        roles.Keys.Order().ToList().ShouldBe(["Marketer", Role.OwnerName], customMessage: "the deleted role is not made again, nothing is added twice");
        roles["Marketer"].ShouldBeEmpty("the organisation's own change to the copy stays");
    }

    [Fact]
    public async Task ONB04_ARoleTheOrganisationAlreadyHasUnderThatNameIsLeftExactlyAsItIsWhateverTheCase()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await WithDbAsync(async db =>
        {
            db.Roles.Add(new Role { Id = Guid.CreateVersion7(), TenantId = owner.TenantId, Name = "marketer", IsBuiltIn = false, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        });

        await SubscribeAsync(world, owner.TenantId, ct);

        var roles = await RolesOfAsync(owner.TenantId, ct);
        roles.Keys.Order().ShouldBe(["Analyst", "marketer", Role.OwnerName]);
        roles["marketer"].ShouldBeEmpty();
    }

    [Fact]
    public async Task ONB04_AnAdminSubscribingAnOrganisationAndATestOrganisationGetTheRolesToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SignUpVerifiedAsync("Globex", SecondEmail, ct);

        (await PostAsync($"{ApplicationAdminEndpoints.Route}/{world.Product.Id}/subscriptions", new { tenantId = owner.TenantId }, world.AdminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var test = await PostAsync($"{ApplicationAdminEndpoints.Route}/{world.Product.Id}/test-organisations", new { name = "Sandbox", ownerEmail = SecondEmail }, world.AdminToken, ct);

        test.StatusCode.ShouldBe(HttpStatusCode.Created);
        var sandbox = (await test.Content.ReadFromJsonAsync<TestOrganisationResponse>(Json, ct))!;
        (await RolesOfAsync(owner.TenantId, ct)).Keys.Order().ShouldBe(["Analyst", "Marketer", Role.OwnerName]);
        (await RolesOfAsync(sandbox.TenantId, ct)).Keys.Order().ShouldBe(["Analyst", "Marketer", Role.OwnerName]);
        await WithDbAsync(async db =>
        {
            var entries = await db.AuditEntries.AsNoTracking().Where(entry => entry.Action == AuditActions.RoleTemplatesApplied).ToListAsync(ct);
            entries.Count.ShouldBe(2);
            entries.ShouldAllBe(entry => entry.ActorType == AuditActorType.User && entry.ActorId == world.AdminId);
        });
    }

    [Fact]
    public async Task ONB04_ApplyingCopiesTheRolesIntoOrganisationsThatSubscribedEarlierAndNeverOverwrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var early = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var gone = await SignUpVerifiedAsync("Globex", SecondEmail, ct);
        await SubscribeAsync(world, early.TenantId, ct);
        await SubscribeAsync(world, gone.TenantId, ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(gone.TenantId, world.Product.Id), """{"status":"Cancelled"}""", world.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RolesOfAsync(early.TenantId, ct)).Keys.ToList().ShouldBe([Role.OwnerName], customMessage: "there were no templates when it subscribed");

        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        var applied = await PostAsync($"{Route(world.Product.Id)}/apply", new { }, world.AdminToken, ct);

        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await applied.Content.ReadFromJsonAsync<ApplyRoleTemplatesResponse>(Json, ct)).ShouldBe(new ApplyRoleTemplatesResponse(1, 2, 0));
        (await RolesOfAsync(early.TenantId, ct)).Keys.Order().ShouldBe(["Analyst", "Marketer", Role.OwnerName]);
        (await RolesOfAsync(gone.TenantId, ct)).Keys.ToList().ShouldBe([Role.OwnerName], customMessage: "a cancelled subscription is left alone");

        var again = await PostAsync($"{Route(world.Product.Id)}/apply", new { }, world.AdminToken, ct);
        (await again.Content.ReadFromJsonAsync<ApplyRoleTemplatesResponse>(Json, ct)).ShouldBe(new ApplyRoleTemplatesResponse(1, 0, 2));
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleTemplatesApplied, ct);
            entry.ActorId.ShouldBe(world.AdminId);
            entry.TenantId.ShouldBe(early.TenantId);
        });
    }

    [Fact]
    public async Task ONB04_ApplyingWithNoTemplatesDoesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(world, owner.TenantId, ct);

        var applied = await PostAsync($"{Route(world.Product.Id)}/apply", new { }, world.AdminToken, ct);

        applied.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await applied.Content.ReadFromJsonAsync<ApplyRoleTemplatesResponse>(Json, ct)).ShouldBe(new ApplyRoleTemplatesResponse(1, 0, 0));
    }

    [Fact]
    public async Task ONB04_AProductIsRegisteredWithItsRolesInOneCallAndARoleNeedsAPermissionOfTheSameRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var ok = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", permissions = Catalogue, roles = Marketer, activate = true },
            adminToken,
            ct);

        ok.StatusCode.ShouldBe(HttpStatusCode.Created);
        var registered = (await ok.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        registered.RoleTemplateCount.ShouldBe(2);
        var stored = (await (await GetAsync(Route(registered.Id), adminToken, ct)).Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;
        stored.Roles.Select(role => role.Name).ShouldBe(["Analyst", "Marketer"]);
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleTemplatesSet, ct);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("marketing");
        });

        var noCatalogue = await PostAsync(
            ApplicationAdminEndpoints.Route, new { key = "other", name = "Other", jwtAudience = "other-api", roles = Marketer }, adminToken, ct);
        var typo = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "third", name = "Third", jwtAudience = "third-api", permissions = Catalogue, roles = new object[] { new { name = "X", permissions = new object[] { new { key = "campaign.sned", level = "Full" } } } } },
            adminToken,
            ct);

        noCatalogue.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await noCatalogue.Content.ReadAsStringAsync(ct)).ShouldContain("Roles[0].Permissions[0].Key");
        typo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await typo.Content.ReadAsStringAsync(ct)).ShouldContain("Unknown permission 'campaign.sned'");
        await WithDbAsync(async db => (await db.Applications.CountAsync(application => !application.IsBuiltIn, ct)).ShouldBe(1, "a refused registration creates nothing"));
    }

    [Fact]
    public async Task ONB04_AnUnknownProductAndTheBuiltInProductAreNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);

        foreach (var applicationId in new[] { Guid.NewGuid(), BuiltInApplication.Id })
        {
            (await GetAsync(Route(applicationId), adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PutAsync(Route(applicationId), new { roles = Array.Empty<object>() }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await PostAsync($"{Route(applicationId)}/apply", new { }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }
    }

    [Fact]
    public async Task ONB04_OnlyAPlatformAdminCanReadSetOrApplyTemplates()
    {
        var ct = TestContext.Current.CancellationToken;
        var world = await SetUpAsync(ct);
        await PutAsync(Route(world.Product.Id), new { roles = Marketer }, world.AdminToken, ct);
        await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var ownerToken = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var clear = new { roles = Array.Empty<object>() };

        foreach (var (token, expected) in new (string?, HttpStatusCode)[] { (ownerToken, HttpStatusCode.Forbidden), (null, HttpStatusCode.Unauthorized), (world.ServiceToken, HttpStatusCode.Forbidden) })
        {
            (await GetAsync(Route(world.Product.Id), token, ct)).StatusCode.ShouldBe(expected);
            (await PutAsync(Route(world.Product.Id), clear, token, ct)).StatusCode.ShouldBe(expected);
            (await PostAsync($"{Route(world.Product.Id)}/apply", new { }, token, ct)).StatusCode.ShouldBe(expected);
        }

        (await ReadTemplatesAsync(world, ct)).Roles.Count.ShouldBe(2, "nothing changed");
    }

    private sealed record World(string AdminToken, Guid AdminId, TestProduct Product, string ServiceToken);

    /// <summary>A registered, available product with a catalogue of three permissions and no role templates yet, and its service token.</summary>
    private async Task<World> SetUpAsync(CancellationToken ct)
    {
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var registered = await PostAsync(
            ApplicationAdminEndpoints.Route,
            new { key = "marketing", name = "Marketing", jwtAudience = "marketing-api", permissions = Catalogue, activate = true },
            adminToken,
            ct);
        registered.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = (await registered.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
        var token = await PostAsync("/api/auth/token", new { grant_type = "client_credentials", client_secret = body.Credential }, null, ct);
        var serviceToken = (await token.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("access_token").GetString()!;

        return new World(adminToken, admin.UserId, new TestProduct(body.Id, body.Key, body.ClientId, body.ClientSecret, serviceToken), serviceToken);
    }

    private static string Route(Guid applicationId) => $"{ApplicationAdminEndpoints.Route}/{applicationId}/role-templates";

    private Task<HttpResponseMessage> SubscribeAsync(World world, Guid tenantId, CancellationToken ct) =>
        PostAsync(SubscriptionEndpoints.RouteFor(tenantId), new { }, world.ServiceToken, ct);

    private async Task<RoleTemplatesResponse> ReadTemplatesAsync(World world, CancellationToken ct) =>
        (await (await GetAsync(Route(world.Product.Id), world.AdminToken, ct)).Content.ReadFromJsonAsync<RoleTemplatesResponse>(Json, ct))!;

    /// <summary>The roles of an organisation, each with what it grants (permission key and level), in key order.</summary>
    private async Task<Dictionary<string, List<(string Key, AccessLevel Level)>>> RolesOfAsync(Guid tenantId, CancellationToken ct)
    {
        var result = new Dictionary<string, List<(string, AccessLevel)>>();
        await WithDbAsync(async db =>
        {
            var roles = await db.Roles.AsNoTracking().Where(role => role.TenantId == tenantId).ToListAsync(ct);
            foreach (var role in roles)
            {
                var grants = await (
                    from grant in db.RolePermissions.AsNoTracking()
                    join permission in db.Permissions on grant.PermissionId equals permission.Id
                    where grant.RoleId == role.Id && permission.ApplicationId != BuiltInApplication.Id
                    select new { permission.Key, grant.Level })
                    .ToListAsync(ct);
                result[role.Name] = [.. grants.OrderBy(grant => grant.Key, StringComparer.Ordinal).Select(grant => (grant.Key, grant.Level))];
            }
        });
        return result;
    }
}
