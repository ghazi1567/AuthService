using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Sub01;

/// <summary>
/// SUB-01 (AUTH-18) Subscribe Tenant to Application, through the real <c>POST /api/auth/tenants/{tenantId}/subscriptions</c>
/// called with a product's service token (APP-05). Delivery of the webhook is SUB-06; here the event is checked in the outbox.
/// Test method names start with their Test Case ID (SUB_TC1..SUB_TC3, SUB_TC7, SUB_TC20) per the workbook convention.
/// </summary>
public sealed class SubscribeTenantTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task SUB_TC1_ATenantSubscribesToAnActiveApplicationForTheFirstTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var response = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        subscription.TenantId.ShouldBe(owner.TenantId);
        subscription.ApplicationId.ShouldBe(product.Id);
        subscription.Status.ShouldBe("Active");
        subscription.MaxSeats.ShouldBeNull("no limit was given, so seats are unlimited");

        // The ApplicationSubscribed event is waiting in the outbox for the product.
        await WithDbAsync(async db =>
        {
            var message = await db.WebhookOutbox.AsNoTracking().SingleAsync(ct);
            message.ApplicationId.ShouldBe(product.Id);
            message.EventType.ShouldBe("ApplicationSubscribed");
            message.Status.ShouldBe(WebhookStatus.Pending);

            using var payload = JsonDocument.Parse(message.PayloadJson);
            payload.RootElement.GetProperty("eventId").GetGuid().ShouldBe(message.Id, "the product dedupes on this id");
            payload.RootElement.GetProperty("eventType").GetString().ShouldBe("ApplicationSubscribed");
            payload.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId);
            payload.RootElement.GetProperty("applicationId").GetGuid().ShouldBe(product.Id);
            payload.RootElement.GetProperty("maxSeats").ValueKind.ShouldBe(JsonValueKind.Null);
            payload.RootElement.TryGetProperty("occurredAt", out _).ShouldBeTrue();

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.TenantSubscribed, ct);
            entry.ActorType.ShouldBe(AuditActorType.Product);
            entry.ActorId.ShouldBe(product.Id);
            entry.TenantId.ShouldBe(owner.TenantId);
            entry.Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact]
    public async Task SUB_TC2_SubscribingATenantThatIsAlreadyActiveIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 5 }, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var again = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 50 }, ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        var subscription = (await again.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        subscription.MaxSeats.ShouldBe(5, "changing the limit is the product's separate seat-limit call, not a re-subscribe");
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(1, "no duplicate row");
            (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(1, "the product is not told twice");
            (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task SUB_TC3_SubscribingToAnInactiveApplicationIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var inactive = await RegisterProductAsync(adminToken, "draft-product", ct);
        var token = await ServiceTokenAsync(inactive, ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var response = await SubscribeAsync(token, owner.TenantId, new { }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("application_not_active");
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(0);
            (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task SUB_TC7_SubscribingWithMaxSeatsOfThreeGivesTheOwnerAccessAndUsesOneSeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var response = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 3 }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        (subscription.MaxSeats, subscription.SeatsUsed).ShouldBe((3, 1));
        await WithDbAsync(async db =>
        {
            var access = await db.ApplicationAccess.AsNoTracking().SingleAsync(ct);
            access.MembershipId.ShouldBe(owner.MembershipId);
            access.ApplicationId.ShouldBe(product.Id);
            access.TenantId.ShouldBe(owner.TenantId);
        });
    }

    [Fact]
    public async Task SUB_TC20_ASubscriptionThatFailsToSaveLeavesNoWebhookBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        // Make the last write of the change fail, after the subscription and the event were written.
        await WithDbAsync(db => db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_subscription_audit() RETURNS trigger AS $$
            BEGIN RAISE EXCEPTION 'simulated failure'; END;
            $$ LANGUAGE plpgsql;
            CREATE TRIGGER fail_subscription_audit BEFORE INSERT ON audit_entries
            FOR EACH ROW WHEN (NEW.action = 'auth.subscription.subscribed') EXECUTE FUNCTION fail_subscription_audit();
            """, ct));

        var response = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 3 }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(0, "the subscription rolled back");
            (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(0, "so no webhook was queued");
            (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(0, "and the owner got no seat");
        });
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB01_OnlyTheOwnerGetsAccessWhenTheTenantSubscribes()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var colleague = await SignUpVerifiedAsync("Other Co", "colleague@other.com", ct);
        await AddMembershipAsync(colleague.UserId, owner.TenantId, MembershipStatus.Active, ct);

        await SubscribeAsync(product.ServiceToken, owner.TenantId, new { }, ct);

        await WithDbAsync(async db =>
        {
            var access = await db.ApplicationAccess.AsNoTracking().ToListAsync(ct);
            access.Select(row => row.MembershipId).ShouldBe([owner.MembershipId], "a member without the Owner role waits for SUB-05");
        });
    }

    [Fact]
    public async Task SUB01_ASuspendedOrCancelledSubscriptionBecomesActiveAgainOnTheNewLimitAndTellsTheProductAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 2 }, ct);

        foreach (var status in new[] { SubscriptionStatus.Suspended, SubscriptionStatus.Cancelled })
        {
            await WithDbAsync(db => db.Subscriptions.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, status), ct));

            var response = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats = 10 }, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK, "an existing row is reused, not created");
            var subscription = (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
            (subscription.Status, subscription.MaxSeats, subscription.SeatsUsed).ShouldBe(("Active", 10, 1));
        }

        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(1);
            (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(3, "once for the first subscription and once for each return");
            (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(1, "the owner's seat is not granted twice");
        });
    }

    [Fact]
    public async Task SUB01_TwoProductsSubscribeTheSameTenantIndependently()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await ActiveProductAsync("product-one", ct);
        var second = await ActiveProductAsync("product-two", ct, createAdmin: false);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        (await SubscribeAsync(first.ServiceToken, owner.TenantId, new { maxSeats = 1 }, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SubscribeAsync(second.ServiceToken, owner.TenantId, new { }, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(2);
            (await db.WebhookOutbox.Select(message => message.ApplicationId).ToListAsync(ct)).ShouldBe([first.Id, second.Id], ignoreOrder: true);
        });
    }

    [Fact]
    public async Task SUB01_SimultaneousRequestsCreateExactlyOneSubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => SubscribeAsync(product.ServiceToken, owner.TenantId, new { }, ct)));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(3);
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.CountAsync(ct)).ShouldBe(1);
            (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(1);
            (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task SUB01_AnUnknownTenantIsRejectedWith404()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);

        var response = await SubscribeAsync(product.ServiceToken, Guid.NewGuid(), new { }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("tenant_not_found");
        await WithDbAsync(async db => (await db.WebhookOutbox.CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB01_ASuspendedTenantCannotSubscribe()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        await WithDbAsync(db => db.Tenants.ExecuteUpdateAsync(setters => setters.SetProperty(tenant => tenant.Status, TenantStatus.Suspended), ct));

        var response = await SubscribeAsync(product.ServiceToken, owner.TenantId, new { }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("tenant_suspended");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task SUB01_MaxSeatsBelowOneIsRejectedWith400(int maxSeats)
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        (await SubscribeAsync(product.ServiceToken, owner.TenantId, new { maxSeats }, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        await WithDbAsync(async db => (await db.Subscriptions.CountAsync(ct)).ShouldBe(0));
    }

    [Fact]
    public async Task SUB01_AnEmptyBodyMeansUnlimitedSeats()
    {
        var ct = TestContext.Current.CancellationToken;
        var product = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var request = new HttpRequestMessage(HttpMethod.Post, SubscriptionEndpoints.RouteFor(owner.TenantId));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", product.ServiceToken);

        var response = await Client.SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.MaxSeats.ShouldBeNull();
    }

    [Fact]
    public async Task SUB01_OnlyAProductsServiceTokenCanSubscribeATenant()
    {
        var ct = TestContext.Current.CancellationToken;
        _ = await ActiveProductAsync("whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        var ownerToken = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var productAudienceToken = CreateToken(owner.UserId, ServiceSigningKey(), TimeSpan.FromMinutes(5), audience: "whatsapp-saas-api");

        // The tenant's own people cannot subscribe themselves: it is the product's call.
        (await SubscribeAsync(ownerToken, owner.TenantId, new { }, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SubscribeAsync(productAudienceToken, owner.TenantId, new { }, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SubscribeAsync(null, owner.TenantId, new { }, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await WithDbAsync(async db => (await db.Subscriptions.CountAsync(ct)).ShouldBe(0));
    }

    private Task<HttpResponseMessage> SubscribeAsync(string? token, Guid tenantId, object body, CancellationToken ct) =>
        PostAsync(SubscriptionEndpoints.RouteFor(tenantId), body, token, ct);

    private sealed record Product(Guid Id, string ServiceToken);

    /// <summary>A registered, activated product and its service token, the way a real product would arrive.</summary>
    private async Task<Product> ActiveProductAsync(string key, CancellationToken ct, bool createAdmin = true)
    {
        var adminToken = createAdmin
            ? (await CreatePlatformAdminAsync(AdminEmail, ct)).AccessToken
            : (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var registered = await RegisterProductAsync(adminToken, key, ct);

        (await PostAsync($"{ApplicationAdminEndpoints.Route}/{registered.Id}/permissions",
            new RegisterPermissionsRequest([new PermissionInput("thing.use", "Use the thing", null)]), adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await PutAsync($"{ApplicationAdminEndpoints.Route}/{registered.Id}/active", new SetApplicationActiveRequest(true), adminToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        return new Product(registered.Id, await ServiceTokenAsync(registered, ct));
    }

    private async Task<RegisterApplicationResponse> RegisterProductAsync(string adminToken, string key, CancellationToken ct)
    {
        var response = await PostAsync(ApplicationAdminEndpoints.Route, new { key, name = key, jwtAudience = key + "-api" }, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RegisterApplicationResponse>(Json, ct))!;
    }

    private async Task<string> ServiceTokenAsync(RegisterApplicationResponse product, CancellationToken ct)
    {
        var response = await PostAsync(ServiceTokenEndpoints.Route,
            new { grant_type = "client_credentials", client_id = product.ClientId, client_secret = product.ClientSecret }, null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ServiceTokenResponse>(ct))!.AccessToken;
    }
}
