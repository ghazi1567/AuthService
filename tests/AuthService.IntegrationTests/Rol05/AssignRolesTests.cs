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

namespace AuthService.IntegrationTests.Rol05;

/// <summary>
/// ROL-05 (AUTH-25) Assign Roles to User, through the real <c>PUT /api/auth/tenants/me/members/{membershipId}/roles</c> with a
/// tenant-admin token. Test method names start with their Test Case ID (ROL_TC10..ROL_TC14) per the workbook convention.
/// </summary>
public sealed class AssignRolesTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task ROL_TC10_ReplacingAMembersRolesWithTwoSameTenantRolesLeavesThemHoldingExactlyThose()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var writer = await CreateRoleAsync(t.OwnerToken, "Writer", ct);
        var sender = await CreateRoleAsync(t.OwnerToken, "Sender", ct);
        var viewer = await CreateRoleAsync(t.OwnerToken, "Viewer", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, viewer)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await AssignAsync(t.OwnerToken, member.MembershipId, ct, writer, sender);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<AssignRolesResponse>(Json, ct))!;
        body.Roles.Select(role => role.Name).ShouldBe(["Sender", "Writer"]);
        (await HeldRolesAsync(member.MembershipId, ct)).ShouldBe([writer, sender], ignoreOrder: true, "the set was replaced, not added to");
    }

    [Fact]
    public async Task ROL_TC11_AssigningARoleFromAnotherTenantReturns404AndLeavesTheRolesUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var mine = await CreateRoleAsync(t.OwnerToken, "Mine", ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        var theirs = await CreateRoleAsync(otherToken, "Theirs", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await AssignAsync(t.OwnerToken, member.MembershipId, ct, mine);

        var response = await AssignAsync(t.OwnerToken, member.MembershipId, ct, mine, theirs);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "404, not 403: the other tenant's role is not confirmed to exist");
        (await HeldRolesAsync(member.MembershipId, ct)).ShouldBe([mine]);
    }

    [Fact]
    public async Task ROL_TC12_AnEmptySetIsRejectedWith409AndTheMemberKeepsTheirRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var mine = await CreateRoleAsync(t.OwnerToken, "Mine", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await AssignAsync(t.OwnerToken, member.MembershipId, ct, mine);

        var response = await AssignAsync(t.OwnerToken, member.MembershipId, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("roles_required");
        (await HeldRolesAsync(member.MembershipId, ct)).ShouldBe([mine]);
    }

    [Fact]
    public async Task ROL_TC13_AssigningElevenRolesIsRejectedWith400AndTheMemberKeepsTheirRoles()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var roles = new List<Guid>();
        for (var index = 0; index < 11; index++)
        {
            roles.Add(await CreateRoleAsync(t.OwnerToken, $"Role {index}", ct));
        }

        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await AssignAsync(t.OwnerToken, member.MembershipId, ct, roles[0]);

        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, [.. roles])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await HeldRolesAsync(member.MembershipId, ct)).ShouldBe([roles[0]]);

        // Ten is the most, and is accepted.
        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, [.. roles.Take(10)])).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ROL_TC14_TwoRolesGrantingTheSamePermissionAtReadOnlyAndFullGiveItOnceAtFullInTheNextExchange()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, "campaign.create");
        var low = await CreateRoleAsync(t.OwnerToken, "Low", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "ReadOnly"));
        var high = await CreateRoleAsync(t.OwnerToken, "High", ct, new RoleGrantInput(t.Product.Id, "campaign.create", "Full"));
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await GiveSeatAsync(t.Owner.TenantId, t.Product.Id, member.MembershipId, ct);

        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, low, high)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var exchange = await PostAsync("/api/auth/token/exchange", new { tenantId = t.Owner.TenantId, applicationId = t.Product.Id }, member.IdentityToken, ct);
        exchange.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = new JsonWebTokenHandler().ReadJsonWebToken((await exchange.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString()!);
        token.TryGetPayloadValue<string[]>("permissions", out var permissions).ShouldBeTrue();
        permissions.ShouldBe(["campaign.create:Full"], "once, at Full");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task ROL05_ARoleChangeNeverChangesSeatUsage()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await GiveSeatAsync(t.Owner.TenantId, t.Product.Id, member.MembershipId, ct);
        var before = await SeatsUsedAsync(t.OwnerToken, ct);

        await AssignAsync(t.OwnerToken, member.MembershipId, ct, role);

        (await SeatsUsedAsync(t.OwnerToken, ct)).ShouldBe(before);
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(2, "no access row was added or removed"));
    }

    [Fact]
    public async Task ROL05_AMembershipOfAnotherTenantOrThatDoesNotExistReturns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);

        (await AssignAsync(t.OwnerToken, other.MembershipId, ct, role)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await AssignAsync(t.OwnerToken, Guid.NewGuid(), ct, role)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WithDbAsync(async db => (await db.MembershipRoles.CountAsync(assignment => assignment.MembershipId == other.MembershipId, ct)).ShouldBe(1, "their Owner role is untouched"));
    }

    [Fact]
    public async Task ROL05_TheSameRoleNamedTwiceCountsOnceAndIsStoredOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, role, role, role)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await HeldRolesAsync(member.MembershipId, ct)).ShouldBe([role]);
    }

    [Fact]
    public async Task ROL05_RepeatingTheSameRequestChangesNothingFurther()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await AssignAsync(t.OwnerToken, member.MembershipId, ct, role);
        var before = await WithDbReadAsync(db => db.MembershipRoles.AsNoTracking().Where(assignment => assignment.MembershipId == member.MembershipId).ToListAsync(ct));

        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, role)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var after = await WithDbReadAsync(db => db.MembershipRoles.AsNoTracking().Where(assignment => assignment.MembershipId == member.MembershipId).ToListAsync(ct));
        after.Select(assignment => assignment.Id).ShouldBe(before.Select(assignment => assignment.Id), "the existing assignment is kept, not recreated");
    }

    [Fact]
    public async Task ROL05_TheChangeIsAuditedWithTheAdminAndTheTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        await AssignAsync(t.OwnerToken, member.MembershipId, ct, role);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.MemberRolesChanged, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, member.MembershipId.ToString()));
        });
    }

    [Fact]
    public async Task ROL05_InvalidRequestsAreRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        (await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new { }, t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AssignAsync(t.OwnerToken, member.MembershipId, ct, Guid.Empty)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ROL05_SomeoneWhoIsNotAnOwnerCannotGiveOrTakeTheOwnerRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await OwnerRoleIdAsync(t.Owner.TenantId, ct);
        var assigner = await CreateRoleAsync(t.OwnerToken, "Assigner", ct, new RoleGrantInput(BuiltInApplication.Id, "roles.assign", "Full"));
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var admin = await AddMemberAsync(t.Owner.TenantId, "admin@other.com", ct);
        var target = await AddMemberAsync(t.Owner.TenantId, "target@other.com", ct);
        await AssignAsync(t.OwnerToken, admin.MembershipId, ct, assigner);
        await AssignAsync(t.OwnerToken, target.MembershipId, ct, support);
        var adminToken = await TenantTokenAsync(admin.IdentityToken, t.Owner.TenantId, ct);

        // An assigner may not make anyone, themselves included, an Owner...
        var promote = await AssignAsync(adminToken, target.MembershipId, ct, support, ownerRole);
        var selfPromote = await AssignAsync(adminToken, admin.MembershipId, ct, assigner, ownerRole);
        // ...nor take the role from the real Owner.
        var demote = await AssignAsync(adminToken, t.Owner.MembershipId, ct, support);

        foreach (var response in new[] { promote, selfPromote, demote })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("owner_role_restricted");
        }

        // They can still hand out ordinary roles.
        (await AssignAsync(adminToken, target.MembershipId, ct, support, assigner)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await HeldRolesAsync(t.Owner.MembershipId, ct)).ShouldBe([ownerRole]);
    }

    [Fact]
    public async Task ROL05_TheTenantCannotBeLeftWithoutAnActiveOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await OwnerRoleIdAsync(t.Owner.TenantId, ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);

        // The only Owner cannot step down...
        var alone = await AssignAsync(t.OwnerToken, t.Owner.MembershipId, ct, support);
        alone.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(alone, ct)).Code.ShouldBe("last_owner");

        // ...until someone else is an Owner.
        (await AssignAsync(t.OwnerToken, second.MembershipId, ct, ownerRole)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await AssignAsync(t.OwnerToken, t.Owner.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // And now the second one is the last.
        var secondToken = await TenantTokenAsync(second.IdentityToken, t.Owner.TenantId, ct);
        (await AssignAsync(secondToken, second.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await HeldRolesAsync(second.MembershipId, ct)).ShouldBe([ownerRole]);
    }

    [Fact]
    public async Task ROL05_ADisabledOwnerDoesNotCountAsAnOwnerWhenTheLastActiveOneStepsDown()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await OwnerRoleIdAsync(t.Owner.TenantId, ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);
        await AssignAsync(t.OwnerToken, second.MembershipId, ct, ownerRole);
        await WithDbAsync(db => db.TenantMemberships.Where(membership => membership.Id == second.MembershipId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(membership => membership.Status, MembershipStatus.Disabled), ct));

        (await AssignAsync(t.OwnerToken, t.Owner.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ROL05_OnlyATokenHoldingRolesAssignFullCanAssign()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var readOnly = await CreateRoleAsync(t.OwnerToken, "Read only", ct, new RoleGrantInput(BuiltInApplication.Id, "roles.assign", "ReadOnly"));
        var plain = await AddMemberAsync(t.Owner.TenantId, "plain@other.com", ct);
        var limited = await AddMemberAsync(t.Owner.TenantId, "limited@other.com", ct);
        await AssignAsync(t.OwnerToken, limited.MembershipId, ct, readOnly);
        var target = await AddMemberAsync(t.Owner.TenantId, "target@other.com", ct);

        (await AssignAsync(await TenantTokenAsync(plain.IdentityToken, t.Owner.TenantId, ct), target.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AssignAsync(await TenantTokenAsync(limited.IdentityToken, t.Owner.TenantId, ct), target.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AssignAsync(limited.IdentityToken, target.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AssignAsync(t.Product.ServiceToken, target.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await AssignAsync(null, target.MembershipId, ct, support)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await HeldRolesAsync(target.MembershipId, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ROL05_SimultaneousReplacementsLeaveExactlyOneOfTheRequestedSets()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var a = await CreateRoleAsync(t.OwnerToken, "A", ct);
        var b = await CreateRoleAsync(t.OwnerToken, "B", ct);
        var c = await CreateRoleAsync(t.OwnerToken, "C", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        var responses = await Task.WhenAll(
            AssignAsync(t.OwnerToken, member.MembershipId, ct, a, b),
            AssignAsync(t.OwnerToken, member.MembershipId, ct, c),
            AssignAsync(t.OwnerToken, member.MembershipId, ct, b, c));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        var held = (await HeldRolesAsync(member.MembershipId, ct)).Order().ToList();
        new[] { new[] { a, b }, new[] { c }, new[] { b, c } }.Select(set => set.Order().ToList())
            .ShouldContain(candidate => candidate.SequenceEqual(held), "the result is one whole request, never a mixture");
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    /// <summary>An active product, a tenant subscribed to it, and the Owner's tenant-admin token.</summary>
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

    private Task<HttpResponseMessage> AssignAsync(string? token, Guid membershipId, CancellationToken ct, params Guid[] roleIds) =>
        PutAsync(RoleEndpoints.MemberRolesRouteFor(membershipId), new AssignRolesRequest(roleIds), token, ct);

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    /// <summary>A person with an Active membership in the tenant and no roles yet.</summary>
    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private async Task<List<Guid>> HeldRolesAsync(Guid membershipId, CancellationToken ct) =>
        await WithDbReadAsync(db => db.MembershipRoles.AsNoTracking().Where(assignment => assignment.MembershipId == membershipId)
            .Select(assignment => assignment.RoleId).ToListAsync(ct));

    private Task<Guid> OwnerRoleIdAsync(Guid tenantId, CancellationToken ct) =>
        WithDbReadAsync(db => db.Roles.AsNoTracking().Where(role => role.TenantId == tenantId && role.IsBuiltIn).Select(role => role.Id).SingleAsync(ct));

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

    private async Task<int> SeatsUsedAsync(string ownerToken, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, ownerToken, ct);
        return (await response.Content.ReadFromJsonAsync<List<TenantSubscriptionItem>>(Json, ct))!.Single().SeatsUsed;
    }

    private async Task<T> WithDbReadAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}
