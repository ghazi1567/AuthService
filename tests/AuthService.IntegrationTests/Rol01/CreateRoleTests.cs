using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.IntegrationTests.Rol01;

/// <summary>
/// ROL-01 (AUTH-21) Create Role, through the real <c>POST /api/auth/roles</c> with a tenant-admin token. It also checks what a
/// created role is for: the next token exchange of a member holding it (TOK_TC1, TOK_TC15). Until ROL-05 can assign roles through
/// the API, those two give the member the roles directly in the database.
/// Test method names start with their Test Case ID (ROL_TC1..ROL_TC4, TOK_TC1, TOK_TC15) per the workbook convention.
/// </summary>
public sealed class CreateRoleTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task ROL_TC1_ARoleGrantingCreateReadOnASubscribedApplicationsPermissionIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct, "campaign.create", "campaign.send");

        var response = await CreateRoleAsync(ownerToken, "Marketer", ct, Grant(product.Id, "campaign.create", "CreateRead"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var role = (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!;
        (role.Name, role.IsBuiltIn).ShouldBe(("Marketer", false));
        role.Permissions.ShouldBe([new RoleGrantDto(product.Id, "whatsapp-saas", "campaign.create", "CreateRead")]);
        await WithDbAsync(async db =>
        {
            var stored = await db.Roles.AsNoTracking().SingleAsync(candidate => candidate.Id == role.Id, ct);
            stored.TenantId.ShouldBe(owner.TenantId, "the tenant comes from the token");
            (await db.RolePermissions.AsNoTracking().SingleAsync(grant => grant.RoleId == role.Id, ct)).Level.ShouldBe(AccessLevel.CreateRead);

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleCreated, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((owner.UserId, owner.TenantId, role.Id.ToString()));
        });
    }

    [Fact]
    public async Task ROL_TC2_AGrantFromAnApplicationTheTenantIsNotSubscribedToIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var unsubscribed = await CreateActiveProductAsync(adminToken, "other-product", ct);

        var response = await CreateRoleAsync(ownerToken, "Sneaky", ct, Grant(unsubscribed.Id, "thing.use", "Full"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Roles.AsNoTracking().AnyAsync(role => role.Name == "Sneaky", ct)).ShouldBeFalse("nothing was created"));
    }

    [Fact]
    public async Task ROL_TC3_AGrantReferencingAnUnknownPermissionKeyIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _, ownerToken) = await SubscribedTenantAsync(ct);

        var response = await CreateRoleAsync(ownerToken, "Typo", ct, Grant(product.Id, "campaign.craete", "Full"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("Unknown permission 'campaign.craete'");
        await WithDbAsync(async db => (await db.Roles.AsNoTracking().AnyAsync(role => role.Name == "Typo", ct)).ShouldBeFalse());
    }

    [Fact]
    public async Task ROL_TC4_TwoTenantsCanEachHaveARoleWithTheSameName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);

        (await CreateRoleAsync(ownerToken, "Support", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CreateRoleAsync(otherToken, "Support", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        await WithDbAsync(async db => (await db.Roles.CountAsync(role => role.Name == "Support", ct)).ShouldBe(2));
    }

    [Fact]
    public async Task TOK_TC1_TheNextExchangeEmbedsThePermissionsOfAllTheMembersRolesForThatApplicationOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct, "campaign.create", "campaign.send", "report.export");
        var writer = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Writer", ct,
            Grant(product.Id, "campaign.create", "CreateRead"), Grant(product.Id, "report.export", "ReadOnly")), ct);
        var sender = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Sender", ct, Grant(product.Id, "campaign.send", "Full")), ct);
        var member = await AddMemberAsync(owner.TenantId, product.Id, "member@other.com", [writer, sender], ct);

        var token = await ExchangeAsync(member, owner.TenantId, product.Id, ct);

        token.Audiences.ShouldBe(["whatsapp-saas-api"]);
        PermissionsOf(token).ShouldBe(["campaign.create:CreateRead", "campaign.send:Full", "report.export:ReadOnly"],
            "exactly what the member's roles grant, and nothing the catalog has beyond that");
    }

    [Fact]
    public async Task TOK_TC15_AMemberHoldingRolesInTwoProductsGetsOnlyTheRequestedProductsPermissions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (productX, owner, ownerToken) = await SubscribedTenantAsync(ct, "x.read");
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var productY = await CreateActiveProductAsync(adminToken, "product-y", ct);
        (await PostAsync($"/api/auth/admin/applications/{productY.Id}/permissions", new { permissions = new[] { new { key = "y.write", label = "Y" } } }, adminToken, ct))
            .IsSuccessStatusCode.ShouldBeTrue();
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, productY.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var both = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Both", ct,
            Grant(productX.Id, "x.read", "Full"), Grant(productY.Id, "y.write", "Full")), ct);
        var member = await AddMemberAsync(owner.TenantId, productX.Id, "member@other.com", [both], ct);
        await GiveAccessAsync(owner.TenantId, productY.Id, member.MembershipId, ct);

        var forX = await ExchangeAsync(member, owner.TenantId, productX.Id, ct);
        var forY = await ExchangeAsync(member, owner.TenantId, productY.Id, ct);

        PermissionsOf(forX).ShouldBe(["x.read:Full"], "nothing from product Y");
        PermissionsOf(forY).ShouldBe(["y.write:Full"], "nothing from product X");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task ROL01_WhenRolesGrantTheSamePermissionTheHighestLevelWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct, "campaign.create");
        var low = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Low", ct, Grant(product.Id, "campaign.create", "ReadOnly")), ct);
        var high = await RoleIdAsync(await CreateRoleAsync(ownerToken, "High", ct, Grant(product.Id, "campaign.create", "Full")), ct);
        var member = await AddMemberAsync(owner.TenantId, product.Id, "member@other.com", [low, high], ct);

        PermissionsOf(await ExchangeAsync(member, owner.TenantId, product.Id, ct)).ShouldBe(["campaign.create:Full"], "once, at Full");
    }

    [Fact]
    public async Task ROL01_ANoneGrantIsStoredButGivesNoAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct, "campaign.create");
        var none = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Nothing", ct, Grant(product.Id, "campaign.create", "None")), ct);
        var member = await AddMemberAsync(owner.TenantId, product.Id, "member@other.com", [none], ct);

        PermissionsOf(await ExchangeAsync(member, owner.TenantId, product.Id, ct)).ShouldBeEmpty();
        await WithDbAsync(async db => (await db.RolePermissions.AsNoTracking().SingleAsync(grant => grant.RoleId == none, ct)).Level.ShouldBe(AccessLevel.None));
    }

    [Fact]
    public async Task ROL01_ARoleWithNoPermissionsIsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);

        var response = await CreateRoleAsync(ownerToken, "Access only", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Permissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ROL01_PermissionsOfTheAuthServiceItselfCanBeGrantedWithoutASubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, owner, ownerToken) = await SubscribedTenantAsync(ct);

        var response = await CreateRoleAsync(ownerToken, "Billing viewer", ct, Grant(BuiltInApplication.Id, "subscriptions.read", "ReadOnly"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        // And a member holding it can then use the endpoint it opens, which needs a ReadOnly level and nothing more.
        var roleId = (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var member = await AddMemberAsync(owner.TenantId, null, "member@other.com", [roleId], ct);
        var memberTenantToken = await TenantTokenAsync(member.IdentityToken, owner.TenantId, ct);
        (await GetAsync(ListSubscriptionsEndpoints.Route, memberTenantToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ROL01_ANameAlreadyUsedInTheTenantIsRejectedWith409IgnoringCaseAndSpaces()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);
        (await CreateRoleAsync(ownerToken, "Support", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        foreach (var duplicate in new[] { "Support", "support", "  SUPPORT  ", "Owner", "owner" })
        {
            var response = await CreateRoleAsync(ownerToken, duplicate, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict, $"'{duplicate}'");
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("role_name_taken");
        }
    }

    [Fact]
    public async Task ROL01_AGrantFromASubscriptionThatIsNotActiveIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct);
        foreach (var status in new[] { "Suspended", "Cancelled" })
        {
            (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(owner.TenantId, product.Id), $$"""{"status": "{{status}}"}""", product.ServiceToken, ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            (await CreateRoleAsync(ownerToken, "Late", ct, Grant(product.Id, "thing.use", "Full"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest, status);
        }
    }

    [Fact]
    public async Task ROL01_AnotherTenantsSubscriptionCannotBeUsedAndTellsTheCallerNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var theirs = await CreateActiveProductAsync(adminToken, "their-product", ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(other.TenantId), new { }, theirs.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var subscribedByAnother = await CreateRoleAsync(ownerToken, "A", ct, Grant(theirs.Id, "thing.use", "Full"));
        var doesNotExist = await CreateRoleAsync(ownerToken, "B", ct, Grant(Guid.NewGuid(), "thing.use", "Full"));

        subscribedByAnother.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        doesNotExist.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        // Compared without the per-request trace id, which differs by design.
        async Task<string> ErrorsOf(HttpResponseMessage response) =>
            (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("errors").GetRawText();
        (await ErrorsOf(subscribedByAnother)).ShouldBe(await ErrorsOf(doesNotExist), "an unsubscribed id and an invented one look alike");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ROL01_AMissingNameIsRejectedWith400(string? name)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);

        (await CreateRoleAsync(ownerToken, name, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ROL01_ANameOfExactlyTheLimitIsFineAndOneLongerIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, ownerToken) = await SubscribedTenantAsync(ct);

        (await CreateRoleAsync(ownerToken, new string('a', RoleValidator.MaxNameLength), ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await CreateRoleAsync(ownerToken, new string('b', RoleValidator.MaxNameLength + 1), ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("Admin", "")]
    [InlineData("Admin", "everything")]
    [InlineData("Admin", "1")]
    [InlineData("Admin", "-1")]
    [InlineData("Admin", null)]
    public async Task ROL01_AnInvalidLevelIsRejectedWith400(string name, string? level)
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _, ownerToken) = await SubscribedTenantAsync(ct);

        (await CreateRoleAsync(ownerToken, name, ct, Grant(product.Id, "thing.use", level))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ROL01_ADuplicatedMissingOrMalformedGrantRejectsTheWholeRequestAndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _, ownerToken) = await SubscribedTenantAsync(ct);

        (await CreateRoleAsync(ownerToken, "Dup", ct, Grant(product.Id, "thing.use", "Full"), Grant(product.Id, "Thing.Use", "ReadOnly")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateRoleAsync(ownerToken, "NoApp", ct, new RoleGrantInput(null, "thing.use", "Full"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CreateRoleAsync(ownerToken, "NoKey", ct, new RoleGrantInput(product.Id, " ", "Full"))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await WithDbAsync(async db => (await db.Roles.CountAsync(role => !role.IsBuiltIn, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task ROL01_AKeyIsMatchedIgnoringCase()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _, ownerToken) = await SubscribedTenantAsync(ct);

        var response = await CreateRoleAsync(ownerToken, "Shouty", ct, Grant(product.Id, "  THING.USE ", "full"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Permissions.Single().ShouldBe(new RoleGrantDto(product.Id, "whatsapp-saas", "thing.use", "Full"));
    }

    [Fact]
    public async Task ROL01_OnlyATokenHoldingRolesManageFullCanCreateARole()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner, ownerToken) = await SubscribedTenantAsync(ct);
        var readOnly = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Read only", ct, Grant(BuiltInApplication.Id, "roles.manage", "ReadOnly")), ct);
        var plain = await AddMemberAsync(owner.TenantId, null, "plain@other.com", [], ct);
        var limited = await AddMemberAsync(owner.TenantId, null, "limited@other.com", [readOnly], ct);

        (await CreateRoleAsync(await TenantTokenAsync(plain.IdentityToken, owner.TenantId, ct), "X", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CreateRoleAsync(await TenantTokenAsync(limited.IdentityToken, owner.TenantId, ct), "X", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "ReadOnly is not Full");
        (await CreateRoleAsync(limited.IdentityToken, "X", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await CreateRoleAsync(product.ServiceToken, "X", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CreateRoleAsync(null, "X", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ROL01_AMemberWithFullAccessToRolesManageCanCreateARoleInTheirTenantOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, owner, ownerToken) = await SubscribedTenantAsync(ct);
        var admin = await RoleIdAsync(await CreateRoleAsync(ownerToken, "Role admin", ct, Grant(BuiltInApplication.Id, "roles.manage", "Full")), ct);
        var member = await AddMemberAsync(owner.TenantId, null, "member@other.com", [admin], ct);

        var response = await CreateRoleAsync(await TenantTokenAsync(member.IdentityToken, owner.TenantId, ct), "Made by a member", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        await WithDbAsync(async db => (await db.Roles.AsNoTracking().SingleAsync(role => role.Name == "Made by a member", ct)).TenantId.ShouldBe(owner.TenantId));
    }

    // ---- helpers ----

    private static RoleGrantInput Grant(Guid applicationId, string permission, string? level) => new(applicationId, permission, level);

    private Task<HttpResponseMessage> CreateRoleAsync(string? token, string? name, CancellationToken ct, params RoleGrantInput[] grants) =>
        PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), token, ct);

    private static async Task<Guid> RoleIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    private static string[] PermissionsOf(JsonWebToken token) =>
        token.TryGetPayloadValue<string[]>("permissions", out var permissions) ? permissions : [];

    /// <summary>An active product with the given extra permissions, a tenant whose Owner has a tenant-admin token, and the subscription.</summary>
    private async Task<(TestProduct Product, SignupResponse Owner, string OwnerToken)> SubscribedTenantAsync(CancellationToken ct, params string[] extraPermissions)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        if (extraPermissions.Length > 0)
        {
            var permissions = extraPermissions.Select(key => new { key, label = key }).ToArray();
            (await PostAsync($"/api/auth/admin/applications/{product.Id}/permissions", new { permissions }, adminToken, ct)).IsSuccessStatusCode.ShouldBeTrue();
        }

        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var ownerToken = await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct);
        return (product, owner, ownerToken);
    }

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    /// <summary>
    /// A person with an Active membership in the tenant, holding the given roles, and (when a product is given) a seat in it. Roles
    /// are assigned directly because assigning them through the API is ROL-05.
    /// </summary>
    private async Task<Member> AddMemberAsync(Guid tenantId, Guid? applicationId, string email, Guid[] roleIds, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);

        Guid membershipId = default;
        await WithDbAsync(async db =>
        {
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id;
            foreach (var roleId in roleIds)
            {
                db.MembershipRoles.Add(new MembershipRole
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    MembershipId = membershipId,
                    RoleId = roleId,
                    AssignedAt = DateTimeOffset.UtcNow,
                });
            }

            await db.SaveChangesAsync(ct);
        });

        if (applicationId is { } id)
        {
            await GiveAccessAsync(tenantId, id, membershipId, ct);
        }

        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private Task GiveAccessAsync(Guid tenantId, Guid applicationId, Guid membershipId, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            db.ApplicationAccess.Add(new MembershipApplicationAccess
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membershipId,
                ApplicationId = applicationId,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        });

    private async Task<JsonWebToken> ExchangeAsync(Member member, Guid tenantId, Guid applicationId, CancellationToken ct)
    {
        var response = await PostAsync("/api/auth/token/exchange", new { tenantId, applicationId }, member.IdentityToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return new JsonWebTokenHandler().ReadJsonWebToken(body.GetProperty("accessToken").GetString()!);
    }
}
