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

namespace AuthService.IntegrationTests.Rol02;

/// <summary>
/// ROL-02, ROL-03 and ROL-04 (AUTH-22, AUTH-23, AUTH-24): list, update and delete roles, through the real
/// <c>GET /api/auth/roles</c> and <c>PATCH|DELETE /api/auth/roles/{id}</c> with a tenant-admin token.
/// Test method names start with their Test Case ID (ROL_TC5..ROL_TC9, ROL_TC15, ROL_TC16, TOK_TC5) per the workbook convention.
/// </summary>
public sealed class ManageRolesTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    // ---- ROL-02 list ----

    [Fact]
    public async Task ROL_TC15_ListingARolesTenantShowsTheOwnerRoleAsBuiltInAndBothCustomRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "CreateRead"));
        await CreateRoleAsync(t.OwnerToken, "Support", ct);

        var response = await GetAsync(RoleEndpoints.Route, t.OwnerToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var roles = (await response.Content.ReadPageItemsAsync<RoleListItem>(Json, ct))!;
        roles.Select(role => (role.Name, role.IsBuiltIn)).ShouldBe([("Owner", true), ("Support", false), ("Writer", false)], "Owner first, then by name");
        roles.Single(role => role.Name == "Owner").MemberCount.ShouldBe(1);
        roles.Single(role => role.Name == "Writer").Permissions.ShouldBe([new RoleGrantDto(t.Product.Id, "whatsapp-saas", "campaign.create", "CreateRead")]);
        roles.Single(role => role.Name == "Support").Permissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task ROL_TC16_ListingAsOneTenantWhilePassingAnotherTenantsIdShowsOnlyTheCallersRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        await CreateRoleAsync(t.OwnerToken, "Mine", ct);
        await CreateRoleAsync(otherToken, "Theirs", ct);

        foreach (var tampered in new[] { "", $"?tenantId={other.TenantId}", $"?tid={other.TenantId}" })
        {
            var roles = (await (await GetAsync(RoleEndpoints.Route + tampered, t.OwnerToken, ct)).Content.ReadPageItemsAsync<RoleListItem>(Json, ct))!;

            roles.Select(role => role.Name).ShouldBe(["Owner", "Mine"], $"for '{tampered}'");
        }
    }

    // ---- ROL-03 update ----

    [Fact]
    public async Task ROL_TC5_DowngradingARolesPermissionFromFullToReadOnlyShowsInTheNextExchangeOfAHolder()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([roleId]), t.OwnerToken, ct);
        await GiveSeatAsync(t.Owner.TenantId, t.Product.Id, member.MembershipId, ct);
        var before = await ExchangedPermissionsAsync(member, t, ct);

        var response = await PatchAsync(t.OwnerToken, roleId, new { permissions = new[] { new { applicationId = t.Product.Id, permission = "campaign.create", level = "ReadOnly" } } }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Permissions.Single().Level.ShouldBe("ReadOnly");
        before.ShouldBe(["campaign.create:Full"]);
        (await ExchangedPermissionsAsync(member, t, ct)).ShouldBe(["campaign.create:ReadOnly"], "the new token reflects the change; no re-login was needed");
    }

    [Fact]
    public async Task TOK_TC5_ExchangeRightAfterARolesPermissionsChangedReflectsItWhileThePreviousTokenIsUnaffected()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([roleId]), t.OwnerToken, ct);
        await GiveSeatAsync(t.Owner.TenantId, t.Product.Id, member.MembershipId, ct);
        var oldToken = await ExchangedTokenAsync(member, t, ct);

        await PatchAsync(t.OwnerToken, roleId, new { permissions = Array.Empty<object>() }, ct);

        var newToken = await ExchangedTokenAsync(member, t, ct);
        PermissionsOf(newToken).ShouldBeEmpty();
        PermissionsOf(oldToken).ShouldBe(["campaign.create:Full"], "a token already issued is not touched; it simply expires");
        (await ValidatesAsync(oldToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task ROL_TC6_AnyEditOfTheBuiltInOwnerRoleIsRejectedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await OwnerRoleIdAsync(t.Owner.TenantId, ct);

        foreach (var body in new object[] { new { name = "Boss" }, new { permissions = Array.Empty<object>() }, new { name = "Boss", permissions = Array.Empty<object>() } })
        {
            var response = await PatchAsync(t.OwnerToken, ownerRole, body, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("owner_role_immutable");
        }

        await WithDbAsync(async db => (await db.Roles.AsNoTracking().SingleAsync(role => role.Id == ownerRole, ct)).Name.ShouldBe("Owner"));
    }

    [Fact]
    public async Task ROL_TC7_UpdatingARoleOfAnotherTenantReturns404Not403()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        var theirs = await CreateRoleAsync(otherToken, "Theirs", ct);

        var foreign = await PatchAsync(t.OwnerToken, theirs, new { name = "Hijacked" }, ct);
        var unknown = await PatchAsync(t.OwnerToken, Guid.NewGuid(), new { name = "Nothing" }, ct);

        foreign.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WithDbAsync(async db => (await db.Roles.AsNoTracking().SingleAsync(role => role.Id == theirs, ct)).Name.ShouldBe("Theirs"));
    }

    // ---- ROL-04 delete ----

    [Fact]
    public async Task ROL_TC8_DeletingARoleThatAMemberHoldsIsRejectedWith409()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var roleId = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([roleId]), t.OwnerToken, ct);

        var response = await DeleteAsync(t.OwnerToken, roleId, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("role_in_use");
        await WithDbAsync(async db => (await db.Roles.AnyAsync(role => role.Id == roleId, ct)).ShouldBeTrue());
    }

    [Fact]
    public async Task ROL_TC9_DeletingTheBuiltInOwnerRoleIsRejectedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await OwnerRoleIdAsync(t.Owner.TenantId, ct);

        var response = await DeleteAsync(t.OwnerToken, ownerRole, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("owner_role_immutable");
        await WithDbAsync(async db => (await db.Roles.AnyAsync(role => role.Id == ownerRole, ct)).ShouldBeTrue());
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task ROL03_NameAndPermissionsCanBeChangedTogetherOrSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create", "campaign.send");
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));

        var renamed = await PatchAsync(t.OwnerToken, roleId, new { name = "  Author  " }, ct);
        var regranted = await PatchAsync(t.OwnerToken, roleId, new
        {
            permissions = new[]
            {
                new { applicationId = t.Product.Id, permission = "campaign.send", level = "CreateRead" },
                new { applicationId = t.Product.Id, permission = "campaign.create", level = "Full" },
            },
        }, ct);

        var first = (await renamed.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!;
        first.Name.ShouldBe("Author");
        first.Permissions.Select(grant => grant.Permission).ShouldBe(["campaign.create"], "a rename alone leaves the grants");
        var second = (await regranted.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!;
        second.Name.ShouldBe("Author", "new permissions alone leave the name");
        second.Permissions.Select(grant => (grant.Permission, grant.Level)).ShouldBe([("campaign.create", "Full"), ("campaign.send", "CreateRead")]);
    }

    [Fact]
    public async Task ROL03_UnchangedGrantsKeepTheirRowsAndRemovedOnesAreDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create", "campaign.send");
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct,
            new RoleGrantInput(t.Product.Id, "campaign.create", "Full"), new RoleGrantInput(t.Product.Id, "campaign.send", "Full"));
        var before = await GrantRowsAsync(roleId, ct);

        await PatchAsync(t.OwnerToken, roleId, new { permissions = new[] { new { applicationId = t.Product.Id, permission = "campaign.create", level = "Full" } } }, ct);

        var after = await GrantRowsAsync(roleId, ct);
        after.Count.ShouldBe(1);
        before.ShouldContain(after.Single(), "the grant that did not change is the same row");
    }

    [Fact]
    public async Task ROL03_ARejectedChangeLeavesTheRoleExactlyAsItWas()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        await CreateRoleAsync(t.OwnerToken, "Taken", ct);
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));

        var badGrant = await PatchAsync(t.OwnerToken, roleId, new { name = "Renamed", permissions = new[] { new { applicationId = t.Product.Id, permission = "campaign.craete", level = "Full" } } }, ct);
        var clash = await PatchAsync(t.OwnerToken, roleId, new { name = "taken", permissions = Array.Empty<object>() }, ct);

        badGrant.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        clash.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(clash, ct)).Code.ShouldBe("role_name_taken");
        var role = (await (await GetAsync(RoleEndpoints.Route, t.OwnerToken, ct)).Content.ReadPageItemsAsync<RoleListItem>(Json, ct))!.Single(item => item.Id == roleId);
        (role.Name, role.Permissions.Count).ShouldBe(("Writer", 1), "neither the rename nor the emptied grants were kept");
    }

    [Fact]
    public async Task ROL03_ARequestThatChangesNothingOrIsInvalidIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct);

        (await PatchAsync(t.OwnerToken, roleId, new { }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(t.OwnerToken, roleId, new { name = "   " }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(t.OwnerToken, roleId, new { name = new string('a', RoleValidator.MaxNameLength + 1) }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchAsync(t.OwnerToken, roleId, new { permissions = new[] { new { applicationId = Guid.NewGuid(), permission = "x.y", level = "Full" } } }, ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ROL03_ARoleCanBeRenamedToADifferentCaseOfItsOwnName()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var roleId = await CreateRoleAsync(t.OwnerToken, "support", ct);

        var response = await PatchAsync(t.OwnerToken, roleId, new { name = "Support" }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Name.ShouldBe("Support");
    }

    [Fact]
    public async Task ROL04_ADeletedRoleIsGoneWithItsGrantsAndItsNameCanBeUsedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));

        (await DeleteAsync(t.OwnerToken, roleId, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WithDbAsync(async db =>
        {
            (await db.Roles.AnyAsync(role => role.Id == roleId, ct)).ShouldBeFalse();
            (await db.RolePermissions.AnyAsync(grant => grant.RoleId == roleId, ct)).ShouldBeFalse();
        });
        (await DeleteAsync(t.OwnerToken, roleId, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "deleting it again finds nothing");
        await CreateRoleAsync(t.OwnerToken, "Writer", ct);
    }

    [Fact]
    public async Task ROL04_ARoleAfterItsLastHolderIsMovedOffItCanBeDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var old = await CreateRoleAsync(t.OwnerToken, "Old", ct);
        var current = await CreateRoleAsync(t.OwnerToken, "Current", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([old]), t.OwnerToken, ct);
        (await DeleteAsync(t.OwnerToken, old, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([current]), t.OwnerToken, ct);

        (await DeleteAsync(t.OwnerToken, old, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task ROL04_ARoleOfAnotherTenantReturns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        var theirs = await CreateRoleAsync(otherToken, "Theirs", ct);

        (await DeleteAsync(t.OwnerToken, theirs, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WithDbAsync(async db => (await db.Roles.AnyAsync(role => role.Id == theirs, ct)).ShouldBeTrue());
    }

    [Fact]
    public async Task ROL02_03_04_ChangesAreAuditedWithTheAdminTheTenantAndTheRoleId()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var roleId = await CreateRoleAsync(t.OwnerToken, "Writer", ct);

        await PatchAsync(t.OwnerToken, roleId, new { name = "Author" }, ct);
        await DeleteAsync(t.OwnerToken, roleId, ct);

        await WithDbAsync(async db =>
        {
            foreach (var action in new[] { AuditActions.RoleUpdated, AuditActions.RoleDeleted })
            {
                var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == action, ct);
                (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, roleId.ToString()));
            }
        });
    }

    [Fact]
    public async Task ROL02_03_04_EachCallNeedsItsOwnPermissionAtTheRightLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var target = await CreateRoleAsync(t.OwnerToken, "Target", ct);
        var reader = await CreateRoleAsync(t.OwnerToken, "Reader", ct, new RoleGrantInput(BuiltInApplication.Id, "roles.read", "ReadOnly"));
        var manager = await CreateRoleAsync(t.OwnerToken, "Manager", ct, new RoleGrantInput(BuiltInApplication.Id, "roles.manage", "Full"));
        var readerMember = await AddMemberAsync(t.Owner.TenantId, "reader@other.com", ct);
        var managerMember = await AddMemberAsync(t.Owner.TenantId, "manager@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(readerMember.MembershipId), new AssignRolesRequest([reader]), t.OwnerToken, ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(managerMember.MembershipId), new AssignRolesRequest([manager]), t.OwnerToken, ct);
        var readerToken = await TenantTokenAsync(readerMember.IdentityToken, t.Owner.TenantId, ct);
        var managerToken = await TenantTokenAsync(managerMember.IdentityToken, t.Owner.TenantId, ct);

        // Reading roles is its own permission: managing them does not imply it, and reading does not allow changes.
        (await GetAsync(RoleEndpoints.Route, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(RoleEndpoints.Route, managerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PatchAsync(readerToken, target, new { name = "X" }, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DeleteAsync(readerToken, target, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PatchAsync(managerToken, target, new { name = "X" }, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And none of it works with an identity, service or missing token.
        (await GetAsync(RoleEndpoints.Route, readerMember.IdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DeleteAsync(t.Product.ServiceToken, target, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(RoleEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DeleteAsync(managerToken, target, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct, params string[] extraPermissions)
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
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct));
    }

    private async Task<Guid> CreateRoleAsync(string token, string name, CancellationToken ct, params RoleGrantInput[] grants)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    private Task<HttpResponseMessage> PatchAsync(string? token, Guid roleId, object body, CancellationToken ct) =>
        PatchJsonAsync(RoleEndpoints.RoleRouteFor(roleId), JsonSerializer.Serialize(body), token, ct);

    private Task<HttpResponseMessage> DeleteAsync(string? token, Guid roleId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, RoleEndpoints.RoleRouteFor(roleId));
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request, ct);
    }

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private Task GiveSeatAsync(Guid tenantId, Guid applicationId, Guid membershipId, CancellationToken ct) =>
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

    private async Task<JsonWebToken> ExchangedTokenAsync(Member member, Tenant t, CancellationToken ct)
    {
        var response = await PostAsync("/api/auth/token/exchange", new { tenantId = t.Owner.TenantId, applicationId = t.Product.Id }, member.IdentityToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new JsonWebTokenHandler().ReadJsonWebToken((await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!);
    }

    private static string[] PermissionsOf(JsonWebToken token) =>
        token.TryGetPayloadValue<string[]>("permissions", out var permissions) ? permissions : [];

    private async Task<string[]> ExchangedPermissionsAsync(Member member, Tenant t, CancellationToken ct) =>
        PermissionsOf(await ExchangedTokenAsync(member, t, ct));

    private async Task<bool> ValidatesAsync(JsonWebToken token) =>
        (await new JsonWebTokenHandler().ValidateTokenAsync(token.EncodedToken, new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = ServiceSigningKey(),
            ValidAlgorithms = [Microsoft.IdentityModel.Tokens.SecurityAlgorithms.EcdsaSha256],
            ValidateIssuer = false,
            ValidateAudience = false,
            ValidateLifetime = true,
        })).IsValid;

    private Task<Guid> OwnerRoleIdAsync(Guid tenantId, CancellationToken ct) =>
        ReadDbAsync(db => db.Roles.AsNoTracking().Where(role => role.TenantId == tenantId && role.IsBuiltIn).Select(role => role.Id).SingleAsync(ct));

    private Task<List<Guid>> GrantRowsAsync(Guid roleId, CancellationToken ct) =>
        ReadDbAsync(db => db.RolePermissions.AsNoTracking().Where(grant => grant.RoleId == roleId).Select(grant => grant.Id).ToListAsync(ct));

    private async Task<T> ReadDbAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}
