using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.IntegrationTests.Sub03;

/// <summary>
/// SUB-03 (AUTH-20) List Tenant's Subscribed Applications, through the real <c>GET /api/auth/tenants/me/subscriptions</c> with a
/// tenant-admin token (a token for the Auth Service's own product).
/// Test method names start with their Test Case ID (SUB_TC14..SUB_TC16) per the workbook convention.
/// </summary>
public sealed class ListSubscriptionsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task SUB_TC14_ATenantWithOneActiveAndOneSuspendedSubscriptionSeesBothWithStatusAndSeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var whatsapp = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var crm = await CreateActiveProductAsync(adminToken, "crm", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(whatsapp, owner.TenantId, """{"maxSeats": 5}""", ct);
        await SubscribeAsync(crm, owner.TenantId, "{}", ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(owner.TenantId, crm.Id), """{"status": "Suspended"}""", crm.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await GetAsync(ListSubscriptionsEndpoints.Route, await OwnerTenantTokenAsync(owner, ct), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!;
        items.Select(item => (item.ApplicationKey, item.ApplicationName, item.Status, item.MaxSeats, item.SeatsUsed))
            .ShouldBe([("crm", "crm", "Suspended", (int?)null, 1), ("whatsapp-saas", "whatsapp-saas", "Active", (int?)5, 1)],
                "ordered by product name, each with status, MaxSeats and SeatsUsed");
        items.Select(item => item.ApplicationId).ShouldBe([crm.Id, whatsapp.Id]);
    }

    [Fact]
    public async Task SUB_TC15_PassingAnotherTenantsIdReturnsOnlyTheCallersOwnSubscriptions()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var mine = await CreateActiveProductAsync(adminToken, "mine-product", ct);
        var theirs = await CreateActiveProductAsync(adminToken, "theirs-product", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        await SubscribeAsync(mine, owner.TenantId, "{}", ct);
        await SubscribeAsync(theirs, other.TenantId, "{}", ct);
        var token = await OwnerTenantTokenAsync(owner, ct);

        foreach (var tampered in new[] { "", $"?tenantId={other.TenantId}", $"?tid={other.TenantId}&tenant={other.TenantId}" })
        {
            var response = await GetAsync(ListSubscriptionsEndpoints.Route + tampered, token, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            var items = (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!;
            items.Select(item => item.ApplicationKey).ShouldBe(["mine-product"], $"for '{tampered}'");
        }
    }

    [Fact]
    public async Task SUB_TC16_SeatsUsedCountsInvitedAndActiveMembersButNotDisabledOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 10}""", ct);
        await GiveAccessAsync(owner.TenantId, product.Id, MembershipStatus.Invited, "invited@acme.com", ct);
        await GiveAccessAsync(owner.TenantId, product.Id, MembershipStatus.Disabled, "disabled@acme.com", ct);

        var items = await ListAsync(await OwnerTenantTokenAsync(owner, ct), ct);

        // One Active (the Owner), one Invited and one Disabled member all have access; the Disabled one uses no seat.
        items.Single().SeatsUsed.ShouldBe(2);
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(3, "the Disabled member keeps the access row"));
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB03_ATenantWithNoSubscriptionsGetsAnEmptyList()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        (await ListAsync(await OwnerTenantTokenAsync(owner, ct), ct)).ShouldBeEmpty("the built-in product is not a subscription");
    }

    [Fact]
    public async Task SUB03_ACancelledSubscriptionIsStillListedAndHasItsSeatsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, "{}", ct);
        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(owner.TenantId, product.Id), """{"status": "Cancelled"}""", product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var item = (await ListAsync(await OwnerTenantTokenAsync(owner, ct), ct)).Single();

        (item.Status, item.SeatsUsed).ShouldBe(("Cancelled", 1));
    }

    [Fact]
    public async Task SUB03_AMemberWithoutTheSubscriptionsPermissionIsRefusedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var member = await SignUpVerifiedAsync("Member Co", "member@other.com", ct);
        await AddMembershipAsync(member.UserId, owner.TenantId, MembershipStatus.Active, ct);
        var memberToken = await TenantTokenAsync((await LoginForTokensAsync("member@other.com", ct)).AccessToken, owner.TenantId, ct);

        // The member belongs to the tenant and holds no role granting this, so the token carries no such permission.
        new JsonWebTokenHandler().ReadJsonWebToken(memberToken).TryGetPayloadValue<string[]>("permissions", out var held).ShouldBeTrue();
        held.ShouldNotContain(claim => claim.StartsWith("subscriptions.read", StringComparison.Ordinal));
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, memberToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SUB03_TheOwnersTokenCarriesThePermissionAndNoOtherKindOfTokenIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var identity = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var token = await TenantTokenAsync(identity, owner.TenantId, ct);

        new JsonWebTokenHandler().ReadJsonWebToken(token).TryGetPayloadValue<string[]>("permissions", out var permissions).ShouldBeTrue();
        permissions.ShouldContain("subscriptions.read:Full");
        (await GetAsync(ListSubscriptionsEndpoints.Route, identity, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await GetAsync(ListSubscriptionsEndpoints.Route, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(ListSubscriptionsEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SUB03_TheSeatCountInEveryResponseUsesTheSameRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 4}""", ct);
        await GiveAccessAsync(owner.TenantId, product.Id, MembershipStatus.Disabled, "disabled@acme.com", ct);

        var seatLimit = await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(owner.TenantId, product.Id), """{"maxSeats": 6}""", product.ServiceToken, ct);

        (await seatLimit.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.SeatsUsed.ShouldBe(1, "a Disabled member's access uses no seat here either");
    }

    private async Task<string> OwnerTenantTokenAsync(SignupResponse owner, CancellationToken ct) =>
        await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct);

    private async Task<List<TenantSubscriptionItem>> ListAsync(string token, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!;
    }

    private async Task SubscribeAsync(TestProduct product, Guid tenantId, string rawJson, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionEndpoints.RouteFor(tenantId))
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", product.ServiceToken);
        (await Client.SendAsync(request, ct)).IsSuccessStatusCode.ShouldBeTrue();
    }

    /// <summary>Adds a member in the given state with access to the product, directly (granting through the API is SUB-05).</summary>
    private Task GiveAccessAsync(Guid tenantId, Guid applicationId, MembershipStatus status, string email, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            var user = new User { Id = Guid.CreateVersion7(), Email = email, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
            var membership = new TenantMembership
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                UserId = user.Id,
                Status = status,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Add(user);
            db.Add(membership);
            db.ApplicationAccess.Add(new MembershipApplicationAccess
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membership.Id,
                ApplicationId = applicationId,
                GrantedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        });
}
