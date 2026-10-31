using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Sub02;

/// <summary>
/// SUB-02 (AUTH-19) Unsubscribe / Suspend Tenant Application Access, through the real
/// <c>PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}</c> called with a product's service token.
/// Test method names start with their Test Case ID (SUB_TC4..SUB_TC6) per the workbook convention.
/// </summary>
public sealed class SubscriptionStatusTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";

    [Fact]
    public async Task SUB_TC4_ProductSuspendsAnActiveSubscriptionAndIsToldWithAnApplicationUnsubscribedEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);

        var response = await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.Status.ShouldBe("Suspended");
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).Status.ShouldBe(SubscriptionStatus.Suspended);

            var message = await db.WebhookOutbox.AsNoTracking().SingleAsync(candidate => candidate.EventType == "ApplicationUnsubscribed", ct);
            message.ApplicationId.ShouldBe(product.Id);
            message.Status.ShouldBe(WebhookStatus.Pending);
            using var payload = JsonDocument.Parse(message.PayloadJson);
            payload.RootElement.GetProperty("eventId").GetGuid().ShouldBe(message.Id);
            payload.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(owner.TenantId);
            payload.RootElement.GetProperty("applicationId").GetGuid().ShouldBe(product.Id);
            payload.RootElement.GetProperty("status").GetString().ShouldBe("Suspended");

            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.SubscriptionSuspended, ct);
            entry.ActorType.ShouldBe(AuditActorType.Product);
            entry.ActorId.ShouldBe(product.Id);
            entry.TenantId.ShouldBe(owner.TenantId);
            entry.Detail.ShouldBe("whatsapp-saas");
        });
    }

    [Fact(Skip = "Enabled once Token Exchange (TOK-01) exists: it must return 403 for a Suspended or Cancelled subscription.")]
    public Task SUB_TC5_TokenExchangeForAJustSuspendedApplicationIsRejectedWith403() => Task.CompletedTask;

    [Fact(Skip = "Enabled once Token Exchange (TOK-01) exists: a product token issued before the suspension stays valid until it expires.")]
    public Task SUB_TC6_APreSuspensionAccessTokenKeepsWorkingUntilItExpires() => Task.CompletedTask;

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB02_ProductCancelsASubscription()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);

        var response = await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "cancelled"}""", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!.Status.ShouldBe("Cancelled", "the status is not case sensitive");
        await WithDbAsync(async db =>
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.SubscriptionCancelled, ct)).ShouldBe(1));
    }

    [Fact]
    public async Task SUB02_SettingTheSameStatusAgainIsANoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);
        await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);

        var again = await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        await WithDbAsync(async db =>
        {
            (await db.WebhookOutbox.CountAsync(message => message.EventType == "ApplicationUnsubscribed", ct)).ShouldBe(1, "the product is not told twice");
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.SubscriptionSuspended, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task SUB02_ASuspendedSubscriptionCanBeCancelledButACancelledOneStaysCancelled()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);
        await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);

        (await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Cancelled"}""", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var back = await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);

        back.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(back, ct)).Code.ShouldBe("subscription_cancelled");
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).Status.ShouldBe(SubscriptionStatus.Cancelled);
            (await db.WebhookOutbox.CountAsync(message => message.EventType == "ApplicationUnsubscribed", ct)).ShouldBe(2);
        });
    }

    [Fact]
    public async Task SUB02_SuspendingKeepsEveryonesSeatAndSubscribingAgainRestoresTheTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);

        await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct);
        await WithDbAsync(async db => (await db.ApplicationAccess.CountAsync(ct)).ShouldBe(1, "seats are kept so reinstating restores everyone"));

        var resubscribed = await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct);

        resubscribed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var subscription = (await resubscribed.Content.ReadFromJsonAsync<SubscriptionResponse>(Json, ct))!;
        (subscription.Status, subscription.SeatsUsed).ShouldBe(("Active", 1));
    }

    [Theory]
    [InlineData("""{"status": "Active"}""")]
    [InlineData("""{"status": "active"}""")]
    [InlineData("""{"status": "Deleted"}""")]
    [InlineData("""{"status": ""}""")]
    [InlineData("""{"status": null}""")]
    [InlineData("""{"status": 1}""")]
    [InlineData("""{"status": "1"}""")]
    [InlineData("""{"status": "Suspended, Cancelled"}""")]
    [InlineData("""{}""")]
    public async Task SUB02_AnythingButSuspendedOrCancelledIsRejectedWith400AndChangesNothing(string body)
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);

        var response = await SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, body, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await WithDbAsync(async db =>
        {
            (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).Status.ShouldBe(SubscriptionStatus.Active);
            (await db.WebhookOutbox.CountAsync(message => message.EventType == "ApplicationUnsubscribed", ct)).ShouldBe(0);
        });
    }

    [Fact]
    public async Task SUB02_AnotherProductAnUnknownSubscriptionAndAnUnknownTenantAllReturn404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (productA, owner) = await SubscribedTenantAsync(ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        var productB = await CreateActiveProductAsync(adminToken, "product-b", ct);

        var otherProduct = await SetStatusAsync(productB.ServiceToken, owner.TenantId, productA.Id, """{"status": "Suspended"}""", ct);
        var neverSubscribed = await SetStatusAsync(productB.ServiceToken, owner.TenantId, productB.Id, """{"status": "Suspended"}""", ct);
        var noSuchTenant = await SetStatusAsync(productA.ServiceToken, Guid.NewGuid(), productA.Id, """{"status": "Suspended"}""", ct);

        foreach (var response in new[] { otherProduct, neverSubscribed, noSuchTenant })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            (await ReadProblemAsync(response, ct)).Code.ShouldBe("subscription_not_found");
        }

        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).Status.ShouldBe(SubscriptionStatus.Active));
    }

    [Fact]
    public async Task SUB02_OnlyTheProductsServiceTokenCanChangeTheStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);
        var ownerToken = (await LoginForTokensAsync(OwnerEmail, ct)).AccessToken;
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;

        // Not the tenant's own Owner, and not a platform admin: it is the product's call.
        (await SetStatusAsync(ownerToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetStatusAsync(adminToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await SetStatusAsync(null, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await WithDbAsync(async db => (await db.Subscriptions.AsNoTracking().SingleAsync(ct)).Status.ShouldBe(SubscriptionStatus.Active));
    }

    [Fact]
    public async Task SUB02_SimultaneousSuspensionsQueueExactlyOneEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, owner) = await SubscribedTenantAsync(ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            SetStatusAsync(product.ServiceToken, owner.TenantId, product.Id, """{"status": "Suspended"}""", ct)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        await WithDbAsync(async db =>
            (await db.WebhookOutbox.CountAsync(message => message.EventType == "ApplicationUnsubscribed", ct)).ShouldBe(1));
    }

    private Task<HttpResponseMessage> SetStatusAsync(string? token, Guid tenantId, Guid applicationId, string rawJson, CancellationToken ct) =>
        PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(tenantId, applicationId), rawJson, token, ct);

    /// <summary>An active product (with its service token), a tenant, and the tenant subscribed to it.</summary>
    private async Task<(TestProduct Product, AuthService.Api.Features.Signup.SignupResponse Owner)> SubscribedTenantAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);

        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return (product, owner);
    }
}
