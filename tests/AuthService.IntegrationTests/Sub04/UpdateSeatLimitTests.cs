using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Sub04;

/// <summary>
/// SUB-04 (AUTH-101) Update Seat Limit, through the real <c>PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}/seats</c>
/// called with a product's service token. Test method names start with their Test Case ID (SUB_TC8, SUB_TC9, and APP_TC13 from
/// APP-05, which names the seat limit) per the workbook convention.
/// </summary>
public sealed class UpdateSeatLimitTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task APP_TC13_ProductAsServiceTokenCannotUpdateProductBsSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var productA = await CreateActiveProductAsync(adminToken, "product-a", ct);
        var productB = await CreateActiveProductAsync(adminToken, "product-b", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await SubscribeAsync(productB, owner.TenantId, """{"maxSeats": 5}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await SetSeatsAsync(productA.ServiceToken, owner.TenantId, productB.Id, """{"maxSeats": 99}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("subscription_not_found");
        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).MaxSeats.ShouldBe(5, "B is unchanged"));

        // Even naming its own product, A cannot touch a tenant that is subscribed only to B.
        (await SetSeatsAsync(productA.ServiceToken, owner.TenantId, productA.Id, """{"maxSeats": 99}""", ct)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SUB_TC8_ATenantUserTokenCannotUpdateTheSeatLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 3}""", ct);
        var ownerToken = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var productAudienceToken = CreateToken(owner.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas-api");

        // Not even the tenant's own Owner, and not an admin: it is the product's call.
        (await SetSeatsAsync(ownerToken, owner.TenantId, product.Id, """{"maxSeats": 100}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetSeatsAsync(adminToken, owner.TenantId, product.Id, """{"maxSeats": 100}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetSeatsAsync(productAudienceToken, owner.TenantId, product.Id, """{"maxSeats": 100}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetSeatsAsync(null, owner.TenantId, product.Id, """{"maxSeats": 100}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).MaxSeats.ShouldBe(3, "MaxSeats unchanged"));
    }

    [Fact]
    public async Task SUB_TC9_LoweringTheLimitBelowCurrentUsageIsAcceptedAndKeepsExistingAccess()
    {
        // The next grant returning 409 is SUB-05 (grant access), which does not exist yet. What the limit change must do
        // is tested here: succeed, and take nobody's access away.
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 5}""", ct);
        await GiveSeatsToMoreMembersAsync(owner.TenantId, product.Id, 2, ct);

        var response = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": 1}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the call never fails because of current usage");
        var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        (subscription.MaxSeats, subscription.SeatsUsed).ShouldBe((1, 3), "three people keep access under a limit of one");
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(3));
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB04_TheLimitCanBeRaisedAndRemoved()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 3}""", ct);

        var raised = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": 25}""", ct);
        var removed = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": null}""", ct);

        (await raised.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.MaxSeats.ShouldBe(25);
        removed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await removed.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.MaxSeats.ShouldBeNull("null means unlimited");
    }

    [Fact]
    public async Task SUB04_TheChangeIsAuditedWithTheProductAsActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, "{}", ct);

        await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": 7}""", ct);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.SeatLimitChanged, ct);
            entry.ActorType.ShouldBe(AuditActorType.Product);
            entry.ActorId.ShouldBe(product.Id);
            entry.TenantId.ShouldBe(owner.TenantId);
            entry.Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact]
    public async Task SUB04_AnUnknownSubscriptionReturns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        // The tenant exists but never subscribed, and the other tenant id does not exist at all: the same answer.
        var neverSubscribed = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": 2}""", ct);
        var noSuchTenant = await SetSeatsAsync(product.ServiceToken, Guid.NewGuid(), product.Id, """{"maxSeats": 2}""", ct);

        neverSubscribed.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        noSuchTenant.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(neverSubscribed, ct)).Code.ShouldBe("subscription_not_found");
        await WithDbAsync(async db => (await db.Subscriptions.CountAsync(ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"seats": 5}""")]
    [InlineData("""{"maxSeats": 0}""")]
    [InlineData("""{"maxSeats": -3}""")]
    [InlineData("""{"maxSeats": 2.5}""")]
    [InlineData("""{"maxSeats": "5"}""")]
    [InlineData("""{"maxSeats": true}""")]
    [InlineData("""{"maxSeats": 99999999999}""")]
    [InlineData("""[3]""")]
    [InlineData("""5""")]
    public async Task SUB04_AnInvalidOrMissingLimitIsRejectedWith400AndChangesNothing(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, """{"maxSeats": 3}""", ct);

        var response = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).MaxSeats.ShouldBe(3));
    }

    [Fact]
    public async Task SUB04_ASuspendedSubscriptionCanStillHaveItsLimitChanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product, owner.TenantId, "{}", ct);
        await WithDbAsync(db => db.Subscriptions.ExecuteUpdateAsync(
            setters => setters.SetProperty(row => row.Status, SubscriptionStatus.Suspended), ct));

        var response = await SetSeatsAsync(product.ServiceToken, owner.TenantId, product.Id, """{"maxSeats": 4}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.Status.ShouldBe("Suspended", "only the limit changed");
    }

    private Task<HttpResponseMessage> SetSeatsAsync(string? token, Guid tenantId, Guid applicationId, string rawJson, CancellationToken ct) =>
        PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(tenantId, applicationId), rawJson, token, ct);

    private async Task<HttpResponseMessage> SubscribeAsync(TestProduct product, Guid tenantId, string rawJson, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionEndpoints.RouteFor(tenantId))
        {
            Content = new StringContent(rawJson, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", product.ServiceToken);
        return await Client.SendAsync(request, ct);
    }

    /// <summary>Gives more members of the tenant access to the product directly (granting through the API is SUB-05).</summary>
    private Task GiveSeatsToMoreMembersAsync(Guid tenantId, Guid applicationId, int count, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            for (var index = 0; index < count; index++)
            {
                var user = new User
                {
                    Id = Guid.CreateVersion7(),
                    Email = $"member{index}@acme.com",
                    PasswordHash = "x",
                    CreatedAt = DateTimeOffset.UtcNow,
                };
                var membership = new TenantMembership
                {
                    Id = Guid.CreateVersion7(),
                    TenantId = tenantId,
                    UserId = user.Id,
                    Status = MembershipStatus.Active,
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
            }

            await db.SaveChangesAsync(ct);
        });
}
