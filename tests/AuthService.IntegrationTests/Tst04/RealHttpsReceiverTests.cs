using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AuthService.Api.Features.Subscriptions;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Tst04;

/// <summary>
/// TST-04 (AUTH-207): webhook delivery against a real HTTPS receiver. The service's real HTTP handler (connect-time address check, no
/// redirects, real timeouts) calls Kestrel on a real socket with a real TLS certificate, through a real DNS lookup of <c>localhost</c>.
/// The only thing the tests change is which certificate authority the client trusts, since a test certificate cannot chain to a public
/// one; the chain, the dates and the name are all still checked. Time is a test clock so the 24-hour schedule is walked, not waited for.
/// The tests have no workbook Test Case ID: the story's acceptance criteria cover them. Public DNS, a publicly trusted certificate and
/// the production network path cannot be reached from CI; <c>docs/release-checklist.md</c> covers those.
/// </summary>
public sealed class RealHttpsReceiverTests : AuthApiTestBase
{
    private const string AdminEmail = "ops@platform.test";

    // Starts on a whole microsecond: the database stores microseconds, and the deadline is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));
    private readonly TestAuthority _authority = new("Webhook test authority");

    public RealHttpsReceiverTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
    }

    protected override HarnessOptions Options => new()
    {
        Settings = { ["Auth:Webhooks:DispatcherEnabled"] = "true", ["Auth:Webhooks:TimeoutSeconds"] = "2" },
        ConfigureServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
            {
                // The service's own handler. The harness allows loopback, which is where the test receiver is.
                var handler = WebhookHttp.CreateHandler(allowPrivate: true);
                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => _authority.Trusts(certificate, errors);
                return handler;
            });
        },
    };

    [Fact]
    public async Task TST04_AnEventIsDeliveredOverRealTlsAndTheSignatureAndHeadersVerifyAsAProductWould()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        var t = await SetUpAsync(receiver.HooksUrl, ct);

        var tenantId = await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);

        await Eventually.UntilAsync(() => receiver.Requests.Count >= 1, "the event arrives");
        receiver.Requests.Count.ShouldBe(1);
        var request = receiver.Requests[0];

        var timestamp = long.Parse(request.Headers["X-Auth-Timestamp"]);
        timestamp.ShouldBe(_clock.GetUtcNow().ToUnixTimeSeconds());
        request.Headers["X-Auth-Signature"].ShouldBe(IndependentSignature(t.Secret, timestamp, request.Body), "a product recomputes it with its secret");
        request.Headers["Content-Type"].ShouldStartWith("application/json");
        request.Headers["User-Agent"].ShouldBe("AuthService-Webhooks/1");
        request.Headers["X-Auth-Event-Type"].ShouldBe("ApplicationSubscribed");

        using var payload = JsonDocument.Parse(request.Body);
        request.Headers["X-Auth-Event-Id"].ShouldBe(payload.RootElement.GetProperty("eventId").GetGuid().ToString());
        payload.RootElement.GetProperty("tenantId").GetGuid().ShouldBe(tenantId);

        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "it is marked delivered");
    }

    [Fact]
    public async Task TST04_ARedirectIsNotFollowedAndTheDeliveryCountsAsFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        receiver.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status302Found;
            context.Response.Headers.Location = $"https://localhost:{receiver.Port}/moved";
            return Task.CompletedTask;
        };
        var t = await SetUpAsync(receiver.HooksUrl, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);

        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Attempts == 1, "the attempt is recorded");
        var message = await MessageAsync(id, ct);
        (message.Status, message.LastError).ShouldBe((WebhookStatus.Pending, "HTTP 302"), "not success, so it will be retried");
        receiver.MovedVisits.ShouldBe(0, "the address in the redirect was never called");
    }

    [Fact]
    public async Task TST04_AReceiverThatIsSlowPastTheTimeoutIsAFailedAttemptAndIsRetriedOnSchedule()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        receiver.Respond = async context =>
        {
            // Longer than the 2 second timeout. Stops when the service gives up on the request.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), context.RequestAborted);
            }
            catch (OperationCanceledException)
            {
            }
        };
        var t = await SetUpAsync(receiver.HooksUrl, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Attempts == 1, "the first attempt times out", TimeSpan.FromSeconds(30));

        var first = await MessageAsync(id, ct);
        (first.Status, first.LastError).ShouldBe((WebhookStatus.Pending, "Timed out"));
        first.NextAttemptAt.ShouldBe(_clock.GetUtcNow().AddMinutes(1), "the first retry is a minute later");

        receiver.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };
        _clock.Advance(TimeSpan.FromMinutes(1));

        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "the retry is delivered");
        receiver.Requests.Count.ShouldBe(2);
        receiver.Requests.Select(request => request.Headers["X-Auth-Event-Id"]).Distinct().Count().ShouldBe(1, "the same event, so a product can deduplicate");
        (receiver.Requests[1].At - receiver.Requests[0].At).ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task TST04_AReceiverWhoseCertificateNobodyTrustsIsNeverSentTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stranger = new TestAuthority("Some other authority");
        using var certificate = stranger.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        var t = await SetUpAsync(receiver.HooksUrl, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);

        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Attempts == 1, "the attempt is recorded");
        var message = await MessageAsync(id, ct);
        (message.Status, message.LastError).ShouldBe((WebhookStatus.Pending, "HttpRequestException"), "the handshake failed, so the request was never made");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TST04_AReceiverWhoseCertificateHasExpiredIsNeverSentTheEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate(validFor: TimeSpan.FromHours(1));
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        var t = await SetUpAsync(receiver.HooksUrl, ct);

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);

        var id = await OnlyMessageIdAsync(ct);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Attempts == 1, "the attempt is recorded");
        (await MessageAsync(id, ct)).LastError.ShouldBe("HttpRequestException");
        receiver.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task TST04_RetriesTheDeadlineAndAResendAreObservedAgainstTheRealReceiver()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, _clock);
        receiver.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        };
        var t = await SetUpAsync(receiver.HooksUrl, ct);
        var adminToken = (await LoginForTokensAsync(AdminEmail, ct)).AccessToken;

        await SubscribeNewTenantAsync(t, "Acme Ltd", "owner@acme.com", ct);
        var id = await OnlyMessageIdAsync(ct);
        var created = (await MessageAsync(id, ct)).CreatedAt;
        await Eventually.UntilAsync(() => receiver.Requests.Count == 1, "the first attempt");

        for (var round = 0; round < 20 && (await MessageAsync(id, ct)).Status == WebhookStatus.Pending; round++)
        {
            await AdvanceToNextAttemptAsync(id, ct);
        }

        var failed = await MessageAsync(id, ct);
        failed.Status.ShouldBe(WebhookStatus.Failed);
        failed.LastError.ShouldBe("HTTP 503");
        failed.DeadlineAt.ShouldBe(created.AddHours(24));
        receiver.Requests[^1].At.ShouldBe(failed.DeadlineAt, "the last attempt is made at the deadline, never after it");
        var gaps = receiver.Requests.Zip(receiver.Requests.Skip(1), (a, b) => b.At - a.At).Take(6).ToList();
        gaps.ShouldBe(new[] { TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(12) }.Take(gaps.Count).ToList());

        // Once the product is back, a platform admin's resend delivers it with a fresh 24 hours.
        receiver.Respond = context =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };
        (await PostAsync(WebhookAdminEndpoints.ResendRouteFor(id), new { }, adminToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await Eventually.UntilAsync(async () => (await MessageAsync(id, ct)).Status == WebhookStatus.Delivered, "the resend is delivered");
        (await MessageAsync(id, ct)).DeadlineAt.ShouldBe(_clock.GetUtcNow().AddHours(24));
    }

    // ---- helpers ----

    private sealed record Setup(TestProduct Product, string Secret);

    private async Task<Setup> SetUpAsync(string callbackUrl, CancellationToken ct)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var response = await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest(callbackUrl, null), product.ServiceToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return new Setup(product, (await response.Content.ReadFromJsonAsync<SetWebhookResponse>(Json, ct))!.SigningSecret!);
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

    private async Task<T> ReadAsync<T>(Func<AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}

/// <summary>
/// The production rule at connect time (TST-04): with private addresses off, a callback whose name resolves to a loopback or private
/// address is refused when the service connects, whatever it resolved to when it was registered. The service's own handler and a
/// real DNS lookup are used, so nothing about the check is faked.
/// </summary>
public sealed class RealHttpsReceiverStrictAddressTests : AuthApiTestBase
{
    private const string AdminEmail = "ops@platform.test";
    private readonly TestAuthority _authority = new("Webhook test authority");

    public RealHttpsReceiverStrictAddressTests(PostgresContainerFixture postgres)
        : base(postgres)
    {
    }

    protected override HarnessOptions Options => new()
    {
        Settings =
        {
            ["Auth:Webhooks:DispatcherEnabled"] = "true",
            ["Auth:Webhooks:AllowPrivateAddresses"] = "false",
        },
    };

    [Fact]
    public async Task TST04_ANameThatResolvesToALoopbackAddressIsRefusedAtConnectTimeAndTheReceiverIsNeverCalled()
    {
        var ct = TestContext.Current.CancellationToken;
        using var certificate = _authority.IssueServerCertificate();
        await using var receiver = await RealHttpsReceiver.StartAsync(certificate, TimeProvider.System);
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);

        // Registered with a public-looking address, which the service accepts. The name is then made to lead inside the network, as a
        // DNS change would: the check at registration cannot see it, the one at connect time must.
        (await PutAsync(WebhookConfigEndpoints.Route, new SetWebhookRequest("https://hooks.product.example.com/auth", null), product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        await WithDbAsync(async db => await db.Applications
            .Where(application => application.Id == product.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(application => application.WebhookUrl, receiver.HooksUrl), ct));

        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new { }, product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        WebhookOutboxMessage message = null!;
        await Eventually.UntilAsync(
            async () =>
            {
                await WithDbAsync(async db => message = await db.WebhookOutbox.AsNoTracking().SingleAsync(ct));
                return message.Attempts == 1;
            },
            "the attempt is recorded");

        (message.Status, message.LastError).ShouldBe((WebhookStatus.Pending, "HttpRequestException"));
        message.LastError!.ShouldNotContain("localhost");
        receiver.Requests.ShouldBeEmpty("the service never connected");
    }
}
