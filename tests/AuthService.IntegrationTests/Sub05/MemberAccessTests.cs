using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Sub05;

/// <summary>
/// SUB-05 (AUTH-102) Grant / Revoke Application Access, through the real
/// <c>PUT|DELETE /api/auth/tenants/me/members/{membershipId}/applications/{applicationId}</c> with a tenant-admin token.
/// Test method names start with their Test Case ID (SUB_TC10..SUB_TC13) per the workbook convention.
/// </summary>
public sealed class MemberAccessTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task SUB_TC10_GrantingAccessWhenAllSeatsAreUsedIsRejectedWith409AndCreatesNoAccessRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 1);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        var response = await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("no_seat_available");
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(access => access.MembershipId == member.MembershipId, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB_TC11_TwoConcurrentGrantsForTheLastFreeSeatLetExactlyOneSucceed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 2);
        var first = await AddMemberAsync(t.Owner.TenantId, "first@other.com", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);

        var responses = await Task.WhenAll(
            GrantAsync(t.OwnerToken, first.MembershipId, t.Product.Id, ct),
            GrantAsync(t.OwnerToken, second.MembershipId, t.Product.Id, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(2, "the Owner and exactly one of them"));
    }

    [Fact]
    public async Task SUB_TC12_GrantingAccessToAMemberOfAnotherTenantReturns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);

        var response = await GrantAsync(t.OwnerToken, other.MembershipId, t.Product.Id, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "404, not 403: the other tenant's member is not confirmed to exist");
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(access => access.MembershipId == other.MembershipId, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB_TC13_GivingARoleAnApplicationsPermissionsNeverChangesSeatUsageOrLetsAnotherExchange()
    {
        // ROL-03 (editing a role) does not exist yet, so the role is created with the permissions in the first place; the rule
        // is the same: roles decide what a member can do, access decides whether they can use the product at all.
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var role = await PostAsync(RoleEndpoints.Route,
            new CreateRoleRequest("Product user", [new RoleGrantInput(t.Product.Id, "thing.use", "Full")]), t.OwnerToken, ct);
        var roleId = (await role.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        (await PutAsync(RoleEndpoints.MemberRolesRouteFor(member.MembershipId), new AssignRolesRequest([roleId]), t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await SeatsUsedAsync(t.OwnerToken, ct)).ShouldBe(1, "only the Owner has a seat");
        (await ExchangeAsync(member, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "roles alone are not access");

        (await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ExchangeAsync(member, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB05_GrantingTakesASeatAndGrantingAgainIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 3);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        var first = await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);
        var again = await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await first.Content.ReadFromJsonAsync<MemberAccessResponse>(Json, ct))!.ShouldBe(new MemberAccessResponse(member.MembershipId, t.Product.Id, true, 2, 3));
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.Content.ReadFromJsonAsync<MemberAccessResponse>(Json, ct))!.SeatsUsed.ShouldBe(2, "no second seat");
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(access => access.MembershipId == member.MembershipId, ct)).ShouldBe(1));
    }

    [Fact]
    public async Task SUB05_RevokingFreesTheSeatForAnotherMemberAndStopsTheNextExchange()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 2);
        var first = await AddMemberAsync(t.Owner.TenantId, "first@other.com", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);
        (await GrantAsync(t.OwnerToken, first.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await GrantAsync(t.OwnerToken, second.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ExchangeAsync(first, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RevokeAsync(t.OwnerToken, first.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ExchangeAsync(first, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "revoking applies at the next exchange");
        (await GrantAsync(t.OwnerToken, second.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SUB05_RevokingAccessThatWasNeverGrantedIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        (await RevokeAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RevokeAsync(t.OwnerToken, member.MembershipId, Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RevokeAsync(t.OwnerToken, Guid.NewGuid(), t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await WithDbAsync(async db => (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.MemberAccessRevoked, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB05_RevokingStillWorksOnASuspendedSubscriptionSoSeatsCanAlwaysBeFreed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(t.Owner.TenantId, t.Product.Id), """{"status": "Suspended"}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RevokeAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(access => access.MembershipId == member.MembershipId, ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB05_AnApplicationTheTenantIsNotActivelySubscribedToIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var unsubscribed = await CreateActiveProductAsync(adminToken, "other-product", ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(t.Owner.TenantId, t.Product.Id), """{"status": "Cancelled"}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        foreach (var applicationId in new[] { unsubscribed.Id, t.Product.Id, Guid.NewGuid(), BuiltInApplication.Id })
        {
            var response = await GrantAsync(t.OwnerToken, member.MembershipId, applicationId, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, $"for {applicationId}");
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_subscribed");
        }
    }

    [Fact]
    public async Task SUB05_InvitedMembersTakeASeatAndDisabledOnesCannotBeGivenAccess()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 2);
        var invited = await AddMemberAsync(t.Owner.TenantId, "invited@other.com", ct, MembershipStatus.Invited);
        var disabled = await AddMemberAsync(t.Owner.TenantId, "disabled@other.com", ct, MembershipStatus.Disabled);
        var active = await AddMemberAsync(t.Owner.TenantId, "active@other.com", ct);

        (await GrantAsync(t.OwnerToken, invited.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var disabledResponse = await GrantAsync(t.OwnerToken, disabled.MembershipId, t.Product.Id, ct);
        var full = await GrantAsync(t.OwnerToken, active.MembershipId, t.Product.Id, ct);

        disabledResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(disabledResponse, ct)).Code.ShouldBe("membership_disabled");
        full.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the Owner and the invited member fill both seats");
        (await ReadProblemAsync(full, ct)).Code.ShouldBe("no_seat_available");
    }

    [Fact]
    public async Task SUB05_UnlimitedSubscriptionsHaveNoSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: null);

        for (var index = 0; index < 4; index++)
        {
            var member = await AddMemberAsync(t.Owner.TenantId, $"member{index}@other.com", ct);
            (await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        (await SeatsUsedAsync(t.OwnerToken, ct)).ShouldBe(5);
    }

    [Fact]
    public async Task SUB05_LoweringTheLimitBelowUsageBlocksNewGrantsUntilUsageFallsAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 3);
        var first = await AddMemberAsync(t.Owner.TenantId, "first@other.com", ct);
        var second = await AddMemberAsync(t.Owner.TenantId, "second@other.com", ct);
        var third = await AddMemberAsync(t.Owner.TenantId, "third@other.com", ct);
        await GrantAsync(t.OwnerToken, first.MembershipId, t.Product.Id, ct);
        await GrantAsync(t.OwnerToken, second.MembershipId, t.Product.Id, ct);
        (await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(t.Owner.TenantId, t.Product.Id), """{"maxSeats": 1}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        (await GrantAsync(t.OwnerToken, third.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ExchangeAsync(first, t.Owner.TenantId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "existing access is kept");

        await RevokeAsync(t.OwnerToken, first.MembershipId, t.Product.Id, ct);
        await RevokeAsync(t.OwnerToken, second.MembershipId, t.Product.Id, ct);
        await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(t.Owner.TenantId, t.Product.Id), """{"maxSeats": 2}""", t.Product.ServiceToken, ct);
        (await GrantAsync(t.OwnerToken, third.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task SUB05_GrantsAndRevokesAreAuditedWithTheMemberAndTheProduct()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var member = await AddMemberAsync(t.Owner.TenantId, "member@other.com", ct);

        await GrantAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);
        await RevokeAsync(t.OwnerToken, member.MembershipId, t.Product.Id, ct);

        await WithDbAsync(async db =>
        {
            foreach (var action in new[] { AuditActions.MemberAccessGranted, AuditActions.MemberAccessRevoked })
            {
                var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == action, ct);
                (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, $"{member.MembershipId}.whatsapp-saas"));
            }
        });
    }

    [Fact]
    public async Task SUB05_OnlyATokenHoldingAccessManageFullCanGrantOrRevoke()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var readOnly = await PostAsync(RoleEndpoints.Route,
            new CreateRoleRequest("Read only", [new RoleGrantInput(BuiltInApplication.Id, "access.manage", "ReadOnly")]), t.OwnerToken, ct);
        var readOnlyId = (await readOnly.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var limited = await AddMemberAsync(t.Owner.TenantId, "limited@other.com", ct);
        var plain = await AddMemberAsync(t.Owner.TenantId, "plain@other.com", ct);
        var target = await AddMemberAsync(t.Owner.TenantId, "target@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(limited.MembershipId), new AssignRolesRequest([readOnlyId]), t.OwnerToken, ct);

        foreach (var token in new[] { await TenantTokenAsync(limited.IdentityToken, t.Owner.TenantId, ct), await TenantTokenAsync(plain.IdentityToken, t.Owner.TenantId, ct) })
        {
            (await GrantAsync(token, target.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await RevokeAsync(token, t.Owner.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await GrantAsync(limited.IdentityToken, target.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await GrantAsync(t.Product.ServiceToken, target.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GrantAsync(null, target.MembershipId, t.Product.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(1, "only the Owner's seat exists"));
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct, int? maxSeats = null)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var subscribe = await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new SubscribeRequest(maxSeats), product.ServiceToken, ct);
        subscribe.StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct));
    }

    private Task<HttpResponseMessage> GrantAsync(string? token, Guid membershipId, Guid applicationId, CancellationToken ct) =>
        PutAsync(MemberAccessEndpoints.RouteFor(membershipId, applicationId), new { }, token, ct);

    private Task<HttpResponseMessage> RevokeAsync(string? token, Guid membershipId, Guid applicationId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, MemberAccessEndpoints.RouteFor(membershipId, applicationId));
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request, ct);
    }

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct, MembershipStatus status = MembershipStatus.Active)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, status, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private Task<HttpResponseMessage> ExchangeAsync(Member member, Guid tenantId, Guid applicationId, CancellationToken ct) =>
        PostAsync("/api/auth/token/exchange", new { tenantId, applicationId }, member.IdentityToken, ct);

    private async Task<int> SeatsUsedAsync(string ownerToken, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, ownerToken, ct);
        return (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!.Single().SeatsUsed;
    }
}
