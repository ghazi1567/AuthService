using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Sub06;

/// <summary>
/// SUB-06 (AUTH-133): a product registers its callback URL and gets a signing secret, and the platform admin lists and resends webhooks.
/// The worker is off here (the default), so events stay queued and only the registration and admin endpoints are exercised.
/// </summary>
public sealed class WebhookConfigTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string Url = "https://product.test/hooks";

    [Fact]
    public async Task SUB06_RegisteringACallbackReturnsTheSigningSecretOnceAndItIsStoredEncrypted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _) = await ProductAsync(ct);

        var first = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, null), product.ServiceToken, ct);
        var second = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url + "2", null), product.ServiceToken, ct);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        first.Headers.CacheControl?.NoStore.ShouldBeTrue("the response holds a secret and must never be cached");
        var created = (await first.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;
        created.SigningSecret.ShouldStartWith("whsec_");
        (await second.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!.SigningSecret.ShouldBeNull("the secret is shown only when it is created or rotated");

        var config = (await (await GetAsync(WebhookConfigEndpoints.Route, product.ServiceToken, ct)).Content.ReadFromJsonAsync<WebhookConfigResponse>(Json, ct))!;
        (config.Url, config.HasSecret).ShouldBe((Url + "2", true));
        await WithDbAsync(async db =>
        {
            var stored = await db.Applications.AsNoTracking().SingleAsync(application => application.Id == product.Id, ct);
            stored.WebhookSecretProtected.ShouldNotBeNullOrEmpty();
            stored.WebhookSecretProtected.ShouldNotContain(created.SigningSecret!);
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.WebhookConfigured, ct)).ShouldBe(2);
            (await db.AuditEntries.AsNoTracking().Where(entry => entry.Action == AuditActions.WebhookConfigured).Select(entry => entry.Detail).Distinct().SingleAsync(ct))
                .ShouldBe("whatsapp-saas");
        });
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(created.SigningSecret!));
    }

    [Fact]
    public async Task SUB06_AskingForARotationGivesANewSecretAndKeepsTheUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _) = await ProductAsync(ct);
        var first = (await (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, null), product.ServiceToken, ct)).Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;

        var rotated = (await (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, true), product.ServiceToken, ct)).Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;

        rotated.SigningSecret.ShouldNotBeNull();
        rotated.SigningSecret.ShouldNotBe(first.SigningSecret);
        rotated.Url.ShouldBe(Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    [InlineData("ftp://product.test/hooks")]
    [InlineData("https://user:pass@product.test/hooks")]
    [InlineData("https://product.test/hooks#fragment")]
    public async Task SUB06_AMissingOrUnusableUrlIsRejectedWith400(string? url)
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _) = await ProductAsync(ct);

        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(url, null), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var config = (await (await GetAsync(WebhookConfigEndpoints.Route, product.ServiceToken, ct)).Content.ReadFromJsonAsync<WebhookConfigResponse>(Json, ct))!;
        (config.Url, config.HasSecret).ShouldBe((null, false), "nothing was stored");
    }

    [Fact]
    public async Task SUB06_OnlyAProductsServiceTokenCanRegisterOrReadItsCallback()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, adminToken) = await ProductAsync(ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var ownerIdentity = (await LoginForTokensAsync("owner@acme.com", ct)).AccessToken;
        var tenantToken = await TenantTokenAsync(ownerIdentity, owner.TenantId, ct);

        foreach (var token in new[] { adminToken, ownerIdentity, tenantToken })
        {
            (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, null), token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            (await GetAsync(WebhookConfigEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, null), null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(Url, null), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SUB06_TheAdminListFiltersAndPagesAndNeedsAPlatformAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, adminToken) = await ProductAsync(ct);
        for (var index = 0; index < 3; index++)
        {
            var owner = await SignUpVerifiedAsync($"Tenant {index}", $"owner{index}@example.com", ct);
            (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        await WithDbAsync(db => db.WebhookOutbox.OrderBy(message => message.CreatedAt).Take(1).ExecuteUpdateAsync(
            setters => setters.SetProperty(message => message.Status, WebhookStatus.Failed).SetProperty(message => message.LastError, "HTTP 500"), ct));

        var all = await ListAsync(adminToken, "", ct);
        var failed = await ListAsync(adminToken, "?status=failed", ct);
        var pending = await ListAsync(adminToken, "?status=Pending", ct);
        var firstPage = await ListAsync(adminToken, "?limit=2", ct);
        var secondPage = await ListAsync(adminToken, $"?limit=2&before={Uri.EscapeDataString(firstPage.Next!.Before.ToString("O"))}&beforeId={firstPage.Next.BeforeId}", ct);

        all.Items.Count.ShouldBe(3);
        all.Items.Select(item => item.CreatedAt).ShouldBe(all.Items.Select(item => item.CreatedAt).OrderDescending());
        (failed.Items.Count, pending.Items.Count).ShouldBe((1, 2));
        failed.Items.Single().LastError.ShouldBe("HTTP 500");
        (firstPage.Items.Count, secondPage.Items.Count, secondPage.Next).ShouldBe((2, 1, null));
        firstPage.Items.Concat(secondPage.Items).Select(item => item.Id).Distinct().Count().ShouldBe(3);

        (await GetAsync($"{WebhookAdminEndpoints.Route}?status=nonsense", adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync($"{WebhookAdminEndpoints.Route}?limit=0", adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await GetAsync(WebhookAdminEndpoints.Route, (await LoginForTokensAsync("owner0@example.com", ct)).AccessToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(WebhookAdminEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SUB06_OnlyAFailedWebhookCanBeResentAndAnUnknownOneIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, adminToken) = await ProductAsync(ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = await ReadAsync(db => db.WebhookOutbox.Select(message => message.Id).SingleAsync(ct));

        var pending = await PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct);
        var unknown = await PostAsync(WebhookAdminEndpoints.ResendRouteFor(Guid.NewGuid()), new { }, adminToken, ct);

        pending.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(pending, ct)).Code.ShouldBe("webhook_not_failed");
        unknown.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await WithDbAsync(db => db.WebhookOutbox.ExecuteUpdateAsync(setters => setters.SetProperty(message => message.Status, WebhookStatus.Failed).SetProperty(message => message.Attempts, 7), ct));
        var responses = await Task.WhenAll(
            PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct),
            PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1, "two admins pressing resend queue it once");
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        await WithDbAsync(async db =>
        {
            var message = await db.WebhookOutbox.AsNoTracking().SingleAsync(ct);
            (message.Status, message.Attempts).ShouldBe((WebhookStatus.Pending, 0));
            (await db.AuditEntries.CountAsync(entry => entry.Action == AuditActions.WebhookResent, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task SUB06_EventsQueuedByTheSubscriptionAreCreatedWithA24HourDeadline()
    {
        var ct = TestContext.Current.CancellationToken;
        var (product, _) = await ProductAsync(ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        await WithDbAsync(async db =>
        {
            var message = await db.WebhookOutbox.AsNoTracking().SingleAsync(ct);
            (message.DeadlineAt - message.CreatedAt).ShouldBe(TimeSpan.FromHours(24));
            message.Status.ShouldBe(WebhookStatus.Pending);
        });
    }

    private async Task<(TestProduct Product, string AdminToken)> ProductAsync(CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        return (await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct), adminToken);
    }

    private async Task<WebhookAdminPage> ListAsync(string adminToken, string query, CancellationToken ct)
    {
        var response = await GetAsync(WebhookAdminEndpoints.Route + query, adminToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<WebhookAdminPage>(Json, ct))!;
    }

    private async Task<T> ReadAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}

/// <summary>The production rule for callback URLs (https, public addresses only), which the other tests relax for a fake product.</summary>
public sealed class WebhookStrictUrlTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options => new() { Settings = { ["Auth:Webhooks:AllowPrivateAddresses"] = "false" } };

    [Theory]
    [InlineData("http://product.example.com/hooks")]
    [InlineData("https://localhost/hooks")]
    [InlineData("https://127.0.0.1/hooks")]
    [InlineData("https://10.0.0.5/hooks")]
    [InlineData("https://192.168.1.10/hooks")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://[::1]/hooks")]
    [InlineData("https://intranet/hooks")]
    [InlineData("https://db.internal/hooks")]
    public async Task SUB06_InProductionAPlainHttpOrPrivateCallbackIsRejectedWith400(string url)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);

        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(url, null), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SUB06_InProductionAPublicHttpsCallbackIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);

        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest("https://hooks.product.example.com/auth", null), product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
