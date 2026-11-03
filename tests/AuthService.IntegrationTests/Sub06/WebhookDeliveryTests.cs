using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Sub06;

/// <summary>
/// SUB-06 (AUTH-133) Webhook Delivery, end to end: a real subscription queues an event, the real worker signs and posts it to a fake
/// product, and retries are walked through on a controllable clock instead of waiting 24 hours.
/// Test method names start with their Test Case ID (SUB_TC17..SUB_TC20) per the workbook convention.
/// </summary>
public sealed class WebhookDeliveryTests : AuthApiTestBase
{
    private const string AdminEmail = "ops@platform.test";
    private const string CallbackUrl = "https://product.test/hooks";

    // Starts on a whole microsecond: the database stores microseconds, and the deadline is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
    private readonly FakeWebhookReceiver _product;

    public WebhookDeliveryTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
        _product = new FakeWebhookReceiver(_clock);
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:DispatcherEnabled"] = "true" },
        ConfigureServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() => _product);
        },
    };

    [Fact]
    public async Task SUB_TC17_SubscribingATenantDeliversOneWebhookWithAValidSignatureEventIdAndTimestamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);

        var tenantId = await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);

        await Eventually.UntilAsync(() => _product.Requests.Count >= 1, "the webhook arrives");
        _product.Requests.Count.ShouldBe(1, "exactly one");
        var request = _product.Requests[0];
        request.Url.ToString().ShouldBe(CallbackUrl);

        // What a product does to trust it: recompute the signature itself, with the secret it was given.
        var timestamp = long.Parse(request.Headers["X-Auth-Timestamp"]);
        request.Headers["X-Auth-Signature"].ShouldBe(IndependentSignature(t.Secret, timestamp, request.Body));
        timestamp.ShouldBe(_clock.GetUtcNow().ToUnixTimeSeconds());

        using var payload = JsonDocument.Parse(request.Body);
        var eventId = payload.RootElement.GetProperty("eventId").GetGuid();
        request.Headers["X-Auth-Event-Id"].ShouldBe(eventId.ToString());
        request.Headers["X-Auth-Event-Type"].ShouldBe("ApplicationSubscribed");
        payload.RootElement.GetProperty("eventType").GetString().ShouldBe("ApplicationSubscribed");
        payload.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(tenantId);
        payload.RootElement.GetProperty("applicationId").GetGuid().ShouldBe(t.Product.Id);
        payload.RootElement.TryGetProperty("occurredAt", out _).ShouldBeTrue();

        await Eventually.UntilAsync(async () => (await MessageAsync(eventId, ct)).Status == WebhookStatus.Delivered, "it is marked delivered");
        var stored = await MessageAsync(eventId, ct);
        (stored.Attempts, stored.DeliveredAt, stored.LastError).ShouldBe((1, _clock.GetUtcNow(), null));
    }

    [Fact]
    public async Task SUB_TC18_ProductEndpointDownThenRecoversIsRetriedWithIncreasingDelaysAndSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        var failures = 2;
        _product.Respond = _ => new HttpResponseMessage(failures-- > 0 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the first attempt");
        await AdvanceToNextAttemptAsync(id, ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 2, "the second attempt");
        await AdvanceToNextAttemptAsync(id, ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "delivered after recovery");

        var times = _product.Requests.Select(request => request.At).ToList();
        times.Count.ShouldBe(3);
        (times[1] - times[0]).ShouldBe(TimeSpan.FromMinutes(1));
        (times[2] - times[1]).ShouldBe(TimeSpan.FromMinutes(5), "the delay grows");
        _product.Requests.Select(request => request.Headers["X-Auth-Event-Id"]).Distinct().Count().ShouldBe(1, "the same event id every time, so a product can deduplicate");
        (await MessageAsync(id, ct)).Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task SUB_TC19_ProductEndpointDownForMoreThan24HoursMarksTheWebhookFailedVisibleToTheAdminAndAResendDeliversIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        _product.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        var created = (await MessageAsync(id, ct)).CreatedAt;
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the first attempt");

        for (var round = 0; round < 20 && (await MessageAsync(id, ct)).Status == WebhookStatus.Pending; round++)
        {
            await AdvanceToNextAttemptAsync(id, ct);
        }

        var failed = await MessageAsync(id, ct);
        failed.Status.ShouldBe(WebhookStatus.Failed);
        failed.LastError.ShouldBe("HTTP 503");
        failed.DeadlineAt.ShouldBe(created.AddHours(24));
        _product.Requests[^1].At.ShouldBe(failed.DeadlineAt, "the last attempt is made at the deadline, never after it");
        _product.Requests.ShouldAllBe(request => request.At <= failed.DeadlineAt);

        // The platform admin sees it...
        var list = await GetAsync($"{WebhookAdminEndpoints.Route}?status=Failed", adminToken, ct);
        var page = (await list.Content.ReadFromJsonAsync<WebhookAdminPage>(Json, ct))!;
        page.Items.Single().ShouldSatisfyAllConditions(
            item => item.Id.ShouldBe(id),
            item => item.Status.ShouldBe("Failed"),
            item => item.ApplicationKey.ShouldBe("whatsapp-saas"),
            item => item.LastError.ShouldBe("HTTP 503"));

        // ...and once the product is back, a resend delivers it with a fresh 24 hours.
        _product.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
        var resend = await PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct);
        resend.StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "the resend is delivered");
        var delivered = await MessageAsync(id, ct);
        (delivered.Attempts, delivered.DeadlineAt).ShouldBe((1, _clock.GetUtcNow().AddHours(24)));
    }

    // SUB_TC20 (a subscription that rolls back queues no webhook) is covered where the subscription is made: SUB_TC20 in the SUB-01 tests.

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task SUB06_AnEventWaitsWhileTheProductHasNoCallbackAndIsSentOnceItRegistersOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: false, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).NextAttemptAt > _clock.GetUtcNow(), "the worker looked and moved on");

        var waiting = await MessageAsync(id, ct);
        (waiting.Status, waiting.Attempts, _product.Requests.Count).ShouldBe((WebhookStatus.Pending, 0, 0));

        var register = await RegisterCallbackAsync(t, CallbackUrl, ct);
        register.Secret.ShouldNotBeNull();
        await AdvanceToNextAttemptAsync(id, ct);

        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "delivered after registering");
        _product.Requests[0].Headers["X-Auth-Signature"].ShouldBe(IndependentSignature(register.Secret!, long.Parse(_product.Requests[0].Headers["X-Auth-Timestamp"]), _product.Requests[0].Body));
    }

    [Fact]
    public async Task SUB06_AnEventForAProductThatNeverRegistersFailsAfter24HoursAndCanBeResentOnceItDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: false, ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Failed, "it gives up");

        (await MessageAsync(id, ct)).LastError.ShouldBe("No callback URL was registered in time.");
        _product.Requests.ShouldBeEmpty();
        await RegisterCallbackAsync(t, CallbackUrl, ct);
        (await PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "delivered after the resend");
    }

    [Fact]
    public async Task SUB06_ARedirectATimeoutAndAConnectionErrorAllCountAsFailuresWithoutRecordingTheUrl()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        var behaviour = 0;
        _product.Respond = _ => behaviour++ switch
        {
            0 => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://elsewhere.test/") } },
            1 => throw new HttpRequestException("could not reach https://product.test/hooks?secret=abc"),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        };

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Attempts == 1, "attempt one is recorded");
        (await MessageAsync(id, ct)).LastError.ShouldBe("HTTP 302", "a redirect is not followed and not success");

        await AdvanceToNextAttemptAsync(id, ct);
        var second = await MessageAsync(id, ct);
        second.LastError.ShouldBe("HttpRequestException", "only the kind of failure is kept");
        second.LastError!.ShouldNotContain("product.test");
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains("secret=abc") || entry.Contains("product.test/hooks"), "the URL never reaches the logs");

        await AdvanceToNextAttemptAsync(id, ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "then it works");
    }

    [Fact]
    public async Task SUB06_RotatingTheSecretMakesLaterEventsSignedWithTheNewOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "the first event");
        var rotated = await RegisterCallbackAsync(t, CallbackUrl, ct, rotate: true, overlapHours: 0);

        await SubscribeNewTenantAsync(t, "Other Co", "boss@other.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 2, "the second event");

        var second = _product.Requests[1];
        rotated.Secret.ShouldNotBe(t.Secret);
        second.Headers["X-Auth-Signature"].ShouldBe(IndependentSignature(rotated.Secret!, long.Parse(second.Headers["X-Auth-Timestamp"]), second.Body));
        second.Headers["X-Auth-Signature"].ShouldNotBe(IndependentSignature(t.Secret, long.Parse(second.Headers["X-Auth-Timestamp"]), second.Body));
    }

    [Fact]
    public async Task SUB06_ASuspensionIsDeliveredAsApplicationUnsubscribedWithTheNewStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        var tenantId = await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        await Eventually.UntilAsync(() => _product.Requests.Count == 1, "subscribed");

        (await PatchJsonAsync(SubscriptionEndpoints.StatusRouteFor(tenantId, t.Product.Id), """{"status": "Suspended"}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        await Eventually.UntilAsync(() => _product.Requests.Count == 2, "unsubscribed");
        _product.Requests[1].Headers["X-Auth-Event-Type"].ShouldBe("ApplicationUnsubscribed");
        using var payload = JsonDocument.Parse(_product.Requests[1].Body);
        (payload.RootElement.GetProperty("status").GetString(), payload.RootElement.GetProperty("tenantId").GetGuid()).ShouldBe(("Suspended", tenantId));
    }

    [Fact]
    public async Task SUB06_TwoWorkersRunningAtOnceDeliverEveryEventExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await SetUpAsync(registerCallback: true, ct);
        _product.Respond = _ => { Thread.Sleep(30); return new HttpResponseMessage(HttpStatusCode.OK); };

        for (var index = 0; index < 4; index++)
        {
            await SubscribeNewTenantAsync(t, $"Tenant {index}", $"owner{index}@example.com", ct);
        }

        // Two more workers join the one already running, all taking from the same outbox at once.
        async Task ExtraWorkerAsync()
        {
            await using var scope = Harness.CreateScope();
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>().DeliverDueAsync(ct);
        }

        await Task.WhenAll(ExtraWorkerAsync(), ExtraWorkerAsync());

        await Eventually.UntilAsync(async () => (await ReadAsync(db => db.WebhookOutbox.CountAsync(m => m.Status == WebhookStatus.Delivered, ct))) == 4, "all four delivered");
        _product.Requests.Count.ShouldBe(4, "three workers never send the same event twice at once");
        _product.Requests.Select(request => request.Headers["X-Auth-Event-Id"]).Distinct().Count().ShouldBe(4);
    }

    // ---- helpers ----

    private sealed record Setup(TestProduct Product, string Secret);

    private async Task<Setup> SetUpAsync(bool registerCallback, CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var secret = string.Empty;
        if (registerCallback)
        {
            secret = (await RegisterCallbackAsync(new Setup(product, string.Empty), CallbackUrl, ct)).Secret!;
        }

        return new Setup(product, secret);
    }

    private sealed record Registered(string Url, string? Secret);

    private async Task<Registered> RegisterCallbackAsync(Setup t, string url, CancellationToken ct, bool rotate = false, int? overlapHours = null)
    {
        var response = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(url, rotate, overlapHours), t.Product.ServiceToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!;
        return new Registered(body.Url, body.SigningSecret);
    }

    private async Task<Guid> SubscribeNewTenantAsync(Setup t, string tenantName, string email, CancellationToken ct)
    {
        var owner = await SignUpVerifiedAsync(tenantName, email, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return owner.TenantId;
    }

    private Task<WebhookOutboxMessage> MessageAsync(Guid id, CancellationToken ct) =>
        ReadAsync(db => db.WebhookOutbox.AsNoTracking().SingleAsync(message => message.Id == id, ct));

    private Task<Guid> OnlyMessageIdAsync(CancellationToken ct) => ReadAsync(db => db.WebhookOutbox.AsNoTracking().Select(message => message.Id).SingleAsync(ct));

    /// <summary>Moves the test clock to when the event is next due, and waits for the worker to make that attempt.</summary>
    private async Task AdvanceToNextAttemptAsync(Guid id, CancellationToken ct)
    {
        // The product sees a request before the worker has saved what came of it, so first wait for the previous attempt to be
        // recorded: the event is rescheduled into the future, or it has reached an end state.
        await Eventually.UntilAsync(async () =>
        {
            var current = await MessageAsync(id, ct);
            return current.Status != WebhookStatus.Pending || current.NextAttemptAt > _clock.GetUtcNow();
        }, "the previous attempt is recorded");

        var before = await MessageAsync(id, ct);
        var wait = before.NextAttemptAt - _clock.GetUtcNow();
        if (wait > TimeSpan.Zero)
        {
            _clock.Advance(wait);
        }

        await Eventually.UntilAsync(async () =>
        {
            var now = await MessageAsync(id, ct);
            return now.Attempts > before.Attempts || now.Status != WebhookStatus.Pending || now.NextAttemptAt != before.NextAttemptAt;
        }, "the worker handles the due event");
    }

    /// <summary>The signature as a product would compute it, written out here rather than by the service's own signer.</summary>
    private static string IndependentSignature(string secret, long timestamp, string body) =>
        "v1=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}"))).ToLowerInvariant();

    private async Task<T> ReadAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}
