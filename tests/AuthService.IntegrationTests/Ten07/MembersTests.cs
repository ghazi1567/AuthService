using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten07;

/// <summary>
/// TEN-07 (AUTH-14) List / Update Tenant Members, through the real <c>GET /api/auth/tenants/me/members</c> and
/// <c>PATCH /api/auth/tenants/me/members/{membershipId}</c> with a tenant-admin token.
/// Test method names start with their Test Case ID (AUTH_TC20, AUTH_TC25) per the workbook convention.
/// </summary>
public sealed class MembersTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task AUTH_TC20_ReEnablingAMemberWhoseProductHasNoFreeSeatIsRejectedWith409AndTheyStayDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 2);
        var first = await AddMemberAsync(t.Owner.TenantId, "first@other.com", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);
        await GrantAsync(t, first, ct);
        (await SetStatusAsync(t.OwnerToken, first.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await GrantAsync(t, second, ct);
        (await SeatsUsedAsync(t.OwnerToken, ct)).ShouldBe(2, "the freed seat was taken by someone else");

        var response = await SetStatusAsync(t.OwnerToken, first.MembershipId, "Active", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(response, ct);
        problem.Code.ShouldBe("no_seat_available");
        problem.Detail!.ShouldContain("whatsapp-saas", Case.Sensitive, "it says which product is full");
        (await StatusOfAsync(first.MembershipId, ct)).ShouldBe(MembershipStatus.Disabled);
    }

    [Fact]
    public async Task AUTH_TC25_DisablingAMembershipInOneTenantFreesItsSeatsAndLeavesTheSamePersonsOtherTenantUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var a = await SignUpVerifiedAsync("Tenant A", "boss@a.com", ct);
        var b = await SignUpVerifiedAsync("Tenant B", "boss@b.com", ct);
        await SubscribeAsync(product, a.TenantId, ct);
        await SubscribeAsync(product, b.TenantId, ct);
        var aToken = await TenantTokenAsync((await LoginForTokensAsync("boss@a.com", ct)).AccessToken, a.TenantId, ct);
        var bToken = await TenantTokenAsync((await LoginForTokensAsync("boss@b.com", ct)).AccessToken, b.TenantId, ct);
        var person = await SignUpVerifiedAsync("Home", "person@both.com", ct);
        var inA = await AddMembershipWithSeatAsync(person.UserId, a.TenantId, product.Id, aToken, ct);
        var inB = await AddMembershipWithSeatAsync(person.UserId, b.TenantId, product.Id, bToken, ct);
        var identity = (await LoginForTokensAsync("person@both.com", ct)).AccessToken;
        (await SeatsUsedAsync(aToken, ct)).ShouldBe(2);

        (await SetStatusAsync(aToken, inA, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SeatsUsedAsync(aToken, ct)).ShouldBe(1, "the seat in A is free again");
        (await StatusOfAsync(inA, ct)).ShouldBe(MembershipStatus.Disabled);
        (await StatusOfAsync(inB, ct)).ShouldBe(MembershipStatus.Active, "B is untouched");
        (await ExchangeAsync(identity, a.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ExchangeAsync(identity, b.TenantId, product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "and can still exchange tokens there");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN07_TheListShowsTheTenantsMembersWithTheirRolesAndProducts()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([role]), t.OwnerToken, ct);
        await GrantAsync(t, member, ct);
        var invited = await AddMemberAsync(t.Owner.TenantId, "invited@other.com", ct, MembershipStatus.Invited);

        var response = await GetAsync(MemberEndpoints.Route, t.OwnerToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var members = (await response.Content.ReadPageItemsAsync<MemberItem>(Json, ct))!;
        members.Select(item => (item.Email, item.Status)).ShouldBe([("invited@other.com", "Invited"), ("member@other.com", "Active"), (OwnerEmail, "Active")]);
        var listed = members.Single(item => item.Email == "member@other.com");
        listed.Roles.Select(item => item.Name).ShouldBe(["Support"]);
        listed.Applications.Select(item => item.ApplicationKey).ShouldBe(["whatsapp-saas"]);
        members.Single(item => item.Email == OwnerEmail).Roles.Select(item => item.Name).ShouldBe(["Owner"]);
        members.Single(item => item.MembershipId == invited.MembershipId).Roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task TEN07_TheListShowsOnlyTheCallersTenantEvenWhenAnotherIdIsPassed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);

        foreach (var tampered in new[] { "", $"?tenantId={other.TenantId}", $"?tid={other.TenantId}" })
        {
            var members = (await (await GetAsync(MemberEndpoints.Route + tampered, t.OwnerToken, ct)).Content.ReadPageItemsAsync<MemberItem>(Json, ct))!;

            members.Select(item => item.Email).ShouldBe([OwnerEmail], $"for '{tampered}'");
        }
    }

    [Fact]
    public async Task TEN07_AMembershipOfAnotherTenantOrThatDoesNotExistReturns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);

        (await SetStatusAsync(t.OwnerToken, other.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await SetStatusAsync(t.OwnerToken, Guid.NewGuid(), "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await StatusOfAsync(other.MembershipId, ct)).ShouldBe(MembershipStatus.Active, "the other tenant's member is untouched");
    }

    [Fact]
    public async Task TEN07_DisablingKeepsRolesAndAccessSoEnablingRestoresTheMemberAsTheyWere()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([role]), t.OwnerToken, ct);
        await GrantAsync(t, member, ct);
        (await ExchangeAsync(member.IdentityToken, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Disabled", ct);

        (await ExchangeAsync(member.IdentityToken, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PostAsync("/api/auth/token/exchange", new { tenantId = t.Owner.TenantId, applicationId = BuiltInApplication.Id }, member.IdentityToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden, "not even a tenant-admin token");
        var kept = (await (await GetAsync(MemberEndpoints.Route, t.OwnerToken, ct)).Content.ReadPageItemsAsync<MemberItem>(Json, ct))!
            .Single(item => item.MembershipId == member.MembershipId);
        (kept.Status, kept.Roles.Count, kept.Applications.Count).ShouldBe(("Disabled", 1, 1));

        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Active", ct);

        (await ExchangeAsync(member.IdentityToken, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN07_SettingTheStatusAMembershipAlreadyHasIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        (await SetStatusAsync(t.OwnerToken, member.MembershipId, "Active", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Disabled", ct);
        (await SetStatusAsync(t.OwnerToken, member.MembershipId, "disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db =>
        {
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.MemberDisabled, ct)).ShouldBe(1);
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.MemberEnabled, ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task TEN07_AnInvitedPersonCannotBeEnabledButCanBeDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var invited = await AddMemberAsync(t.Owner.TenantId, "invited@other.com", ct, MembershipStatus.Invited);

        var enable = await SetStatusAsync(t.OwnerToken, invited.MembershipId, "Active", ct);

        enable.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(enable, ct)).Code.ShouldBe("membership_invited");
        (await SetStatusAsync(t.OwnerToken, invited.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("Invited")]
    [InlineData("Deleted")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("Active, Disabled")]
    public async Task TEN07_AnythingButActiveOrDisabledIsRejectedWith400(string status)
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        (await SetStatusAsync(t.OwnerToken, member.MembershipId, status, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PatchJsonAsync(MemberEndpoints.MemberRouteFor(member.MembershipId), "{}", t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOfAsync(member.MembershipId, ct)).ShouldBe(MembershipStatus.Active);
    }

    [Fact]
    public async Task TEN07_TheLastActiveOwnerCannotBeDisabledAndOnlyAnOwnerCanDisableAnOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await ReadAsync(db => db.Roles.AsNoTracking().Where(role => role.TenantId == t.Owner.TenantId && role.IsBuiltIn).Select(role => role.Id).SingleAsync(ct));
        var manager = await CreateRoleAsync(t.OwnerToken, "Member manager", ct, new RoleGrantInput(BuiltInApplication.Id, "members.manage", "Full"));
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);
        var delegateMember = await AddMemberAsync(t.Owner.TenantId, "delegate@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(delegateMember.MembershipId), new AssignRolesRequest([manager]), t.OwnerToken, ct);
        var delegateToken = await TenantTokenAsync(delegateMember.IdentityToken, t.Owner.TenantId, ct);

        // The only Owner cannot be disabled, not even by themselves...
        var alone = await SetStatusAsync(t.OwnerToken, t.Owner.MembershipId, "Disabled", ct);
        alone.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(alone, ct)).Code.ShouldBe("last_owner");

        // ...and a delegate who may manage members cannot disable an Owner, even with another one around.
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(second.MembershipId), new AssignRolesRequest([ownerRole]), t.OwnerToken, ct);
        var byDelegate = await SetStatusAsync(delegateToken, t.Owner.MembershipId, "Disabled", ct);
        byDelegate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(byDelegate, ct)).Code.ShouldBe("owner_role_restricted");

        // An Owner can step another Owner down, until one is left.
        (await SetStatusAsync(t.OwnerToken, second.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SetStatusAsync(t.OwnerToken, t.Owner.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task TEN07_UnlimitedSubscriptionsNeverBlockReEnabling()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: null);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await GrantAsync(t, member, ct);
        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Disabled", ct);

        (await SetStatusAsync(t.OwnerToken, member.MembershipId, "Active", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN07_StatusChangesAreAuditedWithTheAdminTheTenantAndTheMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Disabled", ct);
        await SetStatusAsync(t.OwnerToken, member.MembershipId, "Active", ct);

        await WithDbAsync(async db =>
        {
            foreach (var action in new[] { AuditActions.MemberDisabled, AuditActions.MemberEnabled })
            {
                var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == action, ct);
                (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, member.MembershipId.ToString()));
            }
        });
    }

    [Fact]
    public async Task TEN07_ListingNeedsMembersReadAndChangingNeedsMembersManageFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var reader = await CreateRoleAsync(t.OwnerToken, "Reader", ct, new RoleGrantInput(BuiltInApplication.Id, "members.read", "ReadOnly"));
        var readerMember = await AddMemberAsync(t.Owner.TenantId, "reader@other.com", ct);
        var plain = await AddMemberAsync(t.Owner.TenantId, "plain@other.com", ct);
        var target = await AddMemberAsync(t.Owner.TenantId, "target@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(readerMember.MembershipId), new AssignRolesRequest([reader]), t.OwnerToken, ct);
        var readerToken = await TenantTokenAsync(readerMember.IdentityToken, t.Owner.TenantId, ct);
        var plainToken = await TenantTokenAsync(plain.IdentityToken, t.Owner.TenantId, ct);

        (await GetAsync(MemberEndpoints.Route, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetAsync(MemberEndpoints.Route, plainToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetStatusAsync(readerToken, target.MembershipId, "Disabled", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "reading is not managing");
        (await GetAsync(MemberEndpoints.Route, readerMember.IdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(MemberEndpoints.Route, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(MemberEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await StatusOfAsync(target.MembershipId, ct)).ShouldBe(MembershipStatus.Active);
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct, int? maxSeats = null)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new SubscribeRequest(maxSeats), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct));
    }

    private async Task SubscribeAsync(TestProduct product, Guid tenantId, CancellationToken ct) =>
        (await PostAsync(SubscriptionEndpoints.RouteFor(tenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

    private async Task<Guid> CreateRoleAsync(string token, string name, CancellationToken ct, params RoleGrantInput[] grants)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    private Task<HttpResponseMessage> SetStatusAsync(string? token, Guid membershipId, string status, CancellationToken ct) =>
        PatchJsonAsync(MemberEndpoints.MemberRouteFor(membershipId), $$"""{"status": "{{status}}"}""", token, ct);

    private Task<HttpResponseMessage> ExchangeAsync(string identityToken, Guid tenantId, Guid applicationId, CancellationToken ct) =>
        PostAsync("/api/auth/token/exchange", new { tenantId, applicationId }, identityToken, ct);

    private async Task GrantAsync(Tenant t, Member member, CancellationToken ct) =>
        (await PutAsync(MemberAccessEndpoints.RouteFor(member.MembershipId, t.Product.Id), new { }, t.OwnerToken, ct)).IsSuccessStatusCode.ShouldBeTrue();

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct, MembershipStatus status = MembershipStatus.Active)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, status, ct);
        var membershipId = await ReadAsync(db => db.TenantMemberships.AsNoTracking()
            .Where(membership => membership.UserId == person.UserId && membership.TenantId == tenantId).Select(membership => membership.Id).SingleAsync(ct));
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    /// <summary>Makes the person an Active member of the tenant and gives them a seat in the product, through the real endpoint.</summary>
    private async Task<Guid> AddMembershipWithSeatAsync(Guid userId, Guid tenantId, Guid applicationId, string tenantToken, CancellationToken ct)
    {
        await AddMembershipAsync(userId, tenantId, MembershipStatus.Active, ct);
        var membershipId = await ReadAsync(db => db.TenantMemberships.AsNoTracking()
            .Where(membership => membership.UserId == userId && membership.TenantId == tenantId).Select(membership => membership.Id).SingleAsync(ct));
        (await PutAsync(MemberAccessEndpoints.RouteFor(membershipId, applicationId), new { }, tenantToken, ct)).IsSuccessStatusCode.ShouldBeTrue();
        return membershipId;
    }

    private Task<MembershipStatus> StatusOfAsync(Guid membershipId, CancellationToken ct) =>
        ReadAsync(db => db.TenantMemberships.AsNoTracking().Where(membership => membership.Id == membershipId).Select(membership => membership.Status).SingleAsync(ct));

    private async Task<int> SeatsUsedAsync(string ownerToken, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, ownerToken, ct);
        return (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!.Single().SeatsUsed;
    }

    private async Task<T> ReadAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}
