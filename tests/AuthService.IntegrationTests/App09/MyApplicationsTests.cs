using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.App09;

/// <summary>
/// APP-09 (AUTH-259) A signed-in person lists the products they can open in one organisation, through the real
/// <c>GET /api/auth/users/me/applications?tenantId=…</c> with their identity token. The tests have no workbook Test Case ID: the story's
/// acceptance criteria cover them.
/// </summary>
public sealed class MyApplicationsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";
    private const string LaunchUrl = "https://helpdesk.example.com/signed-in";

    [Fact]
    public async Task APP09_TheOwnerCanOpenASubscribedProductAndIsGivenWhereToGo()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);

        var items = await ListAsync(s.OwnerIdentityToken, s.Owner.TenantId, ct);

        var helpdesk = items.Single(item => item.Key == "helpdesk");
        helpdesk.State.ShouldBe(MyApplicationState.Available);
        helpdesk.LaunchUrl.ShouldBe(LaunchUrl);
        helpdesk.ApplicationId.ShouldBe(s.Helpdesk.Id);
    }

    [Fact]
    public async Task APP09_AMemberWithoutAccessSeesTheProductAsNoAccessAndNoAddress()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        var member = await AddMemberAsync(s.Owner.TenantId, "member@other.com", ct);

        var helpdesk = (await ListAsync(member.IdentityToken, s.Owner.TenantId, ct)).Single(item => item.Key == "helpdesk");

        helpdesk.State.ShouldBe(MyApplicationState.NoAccess);
        helpdesk.LaunchUrl.ShouldBeNull("where a product lives is not shown to someone who can't open it");
    }

    [Fact]
    public async Task APP09_GivingAMemberAccessMakesTheProductAvailableToThemAndNotToSomeoneElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        var member = await AddMemberAsync(s.Owner.TenantId, "member@other.com", ct);
        var other = await AddMemberAsync(s.Owner.TenantId, "other@other.com", ct);

        (await PutAsync(MemberAccessEndpoints.RouteFor(member.MembershipId, s.Helpdesk.Id), new { }, s.OwnerTenantToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        (await ListAsync(member.IdentityToken, s.Owner.TenantId, ct)).Single(item => item.Key == "helpdesk").State.ShouldBe(MyApplicationState.Available);
        (await ListAsync(other.IdentityToken, s.Owner.TenantId, ct)).Single(item => item.Key == "helpdesk").State.ShouldBe(MyApplicationState.NoAccess);
    }

    [Fact]
    public async Task APP09_AProductTheOrganisationDoesNotHaveIsNotSubscribed()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);

        var billing = (await ListAsync(s.OwnerIdentityToken, s.Owner.TenantId, ct)).Single(item => item.Key == "billing");

        billing.State.ShouldBe(MyApplicationState.NotSubscribed);
        billing.LaunchUrl.ShouldBeNull();
    }

    [Theory]
    [InlineData(SubscriptionStatus.Suspended, MyApplicationState.SubscriptionSuspended)]
    [InlineData(SubscriptionStatus.Cancelled, MyApplicationState.SubscriptionCancelled)]
    public async Task APP09_ASuspendedOrCancelledSubscriptionSaysSoEvenForSomeoneWithASeat(SubscriptionStatus status, string expected)
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await WithDbAsync(async db =>
        {
            var subscription = await db.Subscriptions.SingleAsync(candidate => candidate.ApplicationId == s.Helpdesk.Id, ct);
            subscription.Status = status;
            await db.SaveChangesAsync(ct);
        });

        var helpdesk = (await ListAsync(s.OwnerIdentityToken, s.Owner.TenantId, ct)).Single(item => item.Key == "helpdesk");

        helpdesk.State.ShouldBe(expected);
        helpdesk.LaunchUrl.ShouldBeNull();
    }

    [Fact]
    public async Task APP09_OnlyActiveProductsAreListedByNameAndTheBuiltInOneIsLeftOut()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        await PutAsync($"{ApplicationAdminEndpoints.Route}/{s.Billing.Id}/active", new { isActive = false }, s.AdminToken, ct);

        var items = await ListAsync(s.OwnerIdentityToken, s.Owner.TenantId, ct);

        items.Select(item => item.Key).ShouldBe(["helpdesk"]);
    }

    [Fact]
    public async Task APP09_AnOrganisationThePersonIsNotAnActiveMemberOfIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        var stranger = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var strangerToken = (await LoginForTokensAsync("boss@other.com", ct)).AccessToken;

        var response = await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", strangerToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("tenant_access_denied");
        (await response.Content.ReadAsStringAsync(ct)).ShouldNotContain("helpdesk");
        stranger.TenantId.ShouldNotBe(s.Owner.TenantId);
    }

    [Fact]
    public async Task APP09_ADisabledMemberAndASuspendedOrganisationAreRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);
        var disabled = await AddMemberAsync(s.Owner.TenantId, "gone@other.com", ct, MembershipStatus.Disabled);

        (await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", disabled.IdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await WithDbAsync(async db =>
        {
            var tenant = await db.Tenants.SingleAsync(candidate => candidate.Id == s.Owner.TenantId, ct);
            tenant.Status = TenantStatus.Suspended;
            await db.SaveChangesAsync(ct);
        });

        (await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", s.OwnerIdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APP09_ATenantIdIsRequiredAndNoTokenOrAProductTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);

        (await GetAsync(MyApplicationsEndpoints.Route, s.OwnerIdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", s.Helpdesk.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task APP09_TheAnswerIsPersonalSoNoSharedCacheMayKeepIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var s = await SetUpAsync(ct);

        var response = await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={s.Owner.TenantId}", s.OwnerIdentityToken, ct);

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    private async Task<List<MyApplicationItem>> ListAsync(string identityToken, Guid tenantId, CancellationToken ct)
    {
        var response = await GetAsync($"{MyApplicationsEndpoints.Route}?tenantId={tenantId}", identityToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadPageItemsAsync<MyApplicationItem>(Json, ct))!;
    }

    /// <summary>An organisation subscribed to Helpdesk (which has a launch address), with Billing registered but not subscribed to.</summary>
    private async Task<Setup> SetUpAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var helpdesk = await CreateActiveProductAsync(adminToken, "helpdesk", ct);
        var billing = await CreateActiveProductAsync(adminToken, "billing", ct);
        (await PutAsync($"{ApplicationAdminEndpoints.Route}/{helpdesk.Id}/launch-url", new { url = LaunchUrl }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync($"{ApplicationAdminEndpoints.Route}/{billing.Id}/launch-url", new { url = "https://billing.example.com/in" }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new SubscribeRequest(null), helpdesk.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        return new Setup(adminToken, helpdesk, billing, owner, identity, await TenantTokenAsync(identity, owner.TenantId, ct));
    }

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct, MembershipStatus status = MembershipStatus.Active)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, status, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private sealed record Setup(string AdminToken, TestProduct Helpdesk, TestProduct Billing, SignupResponse Owner, string OwnerIdentityToken, string OwnerTenantToken);

    private sealed record Member(Guid MembershipId, string IdentityToken);
}
