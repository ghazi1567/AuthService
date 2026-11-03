using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using AuthService.Api.Observability;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.IntegrationTests.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Plt05;

/// <summary>
/// PLT-05 (AUTH-202): the liveness and readiness endpoints and the metrics. In-process requests have no address, so a test says which
/// peer a request comes from with the <c>X-Test-Peer</c> header. The clock is controllable so a signing key can run out.
/// </summary>
public sealed class ObservabilityTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Loopback = "127.0.0.1";
    private const string MonitoringServer = "10.20.30.40";
    private const string Stranger = "203.0.113.50";

    // Starts on a whole microsecond: the database stores microseconds.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        Settings = new() { ["Observability:AllowedNetworks:0"] = "10.0.0.0/8" },
        ConfigureServices = services =>
        {
            services.AddSingleton<TimeProvider>(_clock);
            services.AddTransient<IStartupFilter, PeerStartupFilter>();
        },
    };

    [Fact]
    public async Task LivenessAnswers200WithoutLoggingIn()
    {
        var response = await Client.GetAsync(ObservabilityEndpoints.LiveRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Body(response)).GetProperty("status").GetString().ShouldBe("alive");
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadinessAnswers200WhenTheDatabaseIsMigratedAndASigningKeyIsReady()
    {
        var response = await Client.GetAsync(ObservabilityEndpoints.ReadyRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await Body(response);
        body.GetProperty("status").GetString().ShouldBe("ready");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("ok");
        body.GetProperty("checks").GetProperty("signingKey").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task ReadinessAnswers503WhenTheDatabaseCannotBeReachedAndTheBodyDoesNotSayWhy()
    {
        var ct = TestContext.Current.CancellationToken;
        await Postgres.DropDatabaseAsync(Database.Name);

        var response = await Client.GetAsync(ObservabilityEndpoints.ReadyRoute, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var text = await response.Content.ReadAsStringAsync(ct);
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("status").GetString().ShouldBe("unavailable");
        body.GetProperty("checks").GetProperty("database").GetString().ShouldBe("failed");
        body.GetProperty("checks").GetProperty("signingKey").GetString().ShouldBe("ok");

        text.ShouldNotContain(Database.Name, Case.Insensitive, "the database name is not for callers");
        text.ShouldNotContain("Host=", Case.Insensitive);
        text.ShouldNotContain("Password", Case.Insensitive);
        text.ShouldNotContain("Npgsql", Case.Insensitive);
        string.Join('\n', Harness.Logs.Entries).ShouldContain("Not ready", Case.Insensitive, "the reason is in the log, for the operator");
    }

    [Fact]
    public async Task ReadinessAnswers503WhenAMigrationHasNotBeenApplied()
    {
        var ct = TestContext.Current.CancellationToken;
        await PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString,
            "DELETE FROM \"__EFMigrationsHistory\" WHERE \"migration_id\" = (SELECT max(\"migration_id\") FROM \"__EFMigrationsHistory\")",
            ct);

        var response = await Client.GetAsync(ObservabilityEndpoints.ReadyRoute, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Body(response)).GetProperty("checks").GetProperty("database").GetString().ShouldBe("failed");
    }

    [Fact]
    public async Task ReadinessAnswers503WhenThereIsNoSigningKeyToSignWith()
    {
        var ct = TestContext.Current.CancellationToken;
        (await Client.GetAsync(ObservabilityEndpoints.ReadyRoute, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // As if the keys had not been loaded, which is how an instance looks that could not reach the database to load them. A key
        // in the database always has a successor before it stops signing, so the clock alone cannot leave an instance without one.
        using (var scope = Harness.CreateScope())
        {
            typeof(SigningKeyService)
                .GetField("_entries", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(scope.ServiceProvider.GetRequiredService<SigningKeyService>(), Array.CreateInstance(typeof(SigningKeyService).GetNestedType("KeyEntry", BindingFlags.NonPublic)!, 0));
        }

        var response = await Client.GetAsync(ObservabilityEndpoints.ReadyRoute, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var checks = (await Body(response)).GetProperty("checks");
        checks.GetProperty("signingKey").GetString().ShouldBe("failed");
        checks.GetProperty("database").GetString().ShouldBe("ok");
    }

    [Fact]
    public async Task MetricsCountRequestsByRouteTemplateAndStatus()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        (await LoginAsync("owner@acme.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync("owner@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var metrics = await MetricsAsync(Loopback, ct);

        metrics.ShouldContain("auth_http_requests_total{method=\"POST\",route=\"/api/auth/login\",status=\"200\"} 1");
        metrics.ShouldContain("auth_http_requests_total{method=\"POST\",route=\"/api/auth/login\",status=\"401\"} 1");
        metrics.ShouldContain("auth_http_request_duration_seconds_count{method=\"POST\",route=\"/api/auth/login\"} 2");
        metrics.ShouldContain("auth_http_request_duration_seconds_bucket{method=\"POST\",route=\"/api/auth/login\",le=\"+Inf\"} 2");
    }

    [Fact]
    public async Task MetricsCountRefusedLoginsByReason()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await LoginAsync("owner@acme.com", "not the password", ct);
        await LoginAsync("owner@acme.com", "still not the password", ct);
        await LoginAsync("nobody@acme.com", DefaultPassword, ct);

        var metrics = await MetricsAsync(Loopback, ct);

        metrics.ShouldContain("auth_login_failures_total{reason=\"wrong_password\"} 2");
        metrics.ShouldContain("auth_login_failures_total{reason=\"unknown_account\"} 1");
    }

    [Fact]
    public async Task MetricsHoldNoEmailAddressesNamesOrIdsAndAnUnknownPathIsOneLabel()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await LoginAsync("owner@acme.com", "not the password", ct);
        await LoginAsync("someone.else@example.org", "not the password", ct);
        await Client.GetAsync("/probing/for/a-secret-admin-page", ct);
        await Client.GetAsync($"/api/auth/tenants/{account.TenantId}/nothing-here", ct);

        var metrics = await MetricsAsync(Loopback, ct);

        metrics.ShouldNotContain("owner@acme.com");
        metrics.ShouldNotContain("someone.else@example.org");
        metrics.ShouldNotContain("Acme");
        metrics.ShouldNotContain(account.UserId.ToString());
        metrics.ShouldNotContain(account.TenantId.ToString());
        metrics.ShouldNotContain("a-secret-admin-page");
        metrics.ShouldContain("route=\"unmatched\"");
    }

    [Fact]
    public async Task MetricsShowTheSigningKeyAndTheOutboxes()
    {
        var ct = TestContext.Current.CancellationToken;
        _clock.Advance(TimeSpan.FromHours(2));
        var farAway = _clock.GetUtcNow().AddDays(30);
        var waitingSince = _clock.GetUtcNow().AddMinutes(-90);

        await WithDbAsync(async db =>
        {
            var application = await db.Applications.AsNoTracking().FirstAsync(ct);

            // Pending emails far in the future, so the dispatcher leaves them be; one failed email and two events.
            db.EmailOutbox.AddRange(
                Email(EmailOutboxStatus.Pending, waitingSince, farAway),
                Email(EmailOutboxStatus.Pending, waitingSince.AddMinutes(30), farAway),
                Email(EmailOutboxStatus.Failed, waitingSince, farAway));
            db.WebhookOutbox.AddRange(
                Event(application.Id, WebhookStatus.Pending, waitingSince, farAway),
                Event(application.Id, WebhookStatus.Failed, waitingSince, farAway),
                Event(application.Id, WebhookStatus.Failed, waitingSince, farAway));
            await db.SaveChangesAsync(ct);
        });

        var metrics = await MetricsAsync(Loopback, ct);

        metrics.ShouldContain("auth_email_outbox_messages{status=\"pending\"} 2");
        metrics.ShouldContain("auth_email_outbox_messages{status=\"failed\"} 1");
        metrics.ShouldContain("auth_email_outbox_oldest_pending_age_seconds 5400");
        metrics.ShouldContain("auth_webhook_outbox_messages{status=\"pending\"} 1");
        metrics.ShouldContain("auth_webhook_outbox_messages{status=\"failed\"} 2");
        metrics.ShouldContain("auth_webhook_outbox_oldest_pending_age_seconds 5400");
        metrics.ShouldContain("auth_signing_keys_published ");
        metrics.ShouldContain("auth_signing_key_age_seconds ");
        metrics.ShouldContain("auth_process_start_time_seconds ");
    }

    [Fact]
    public async Task MetricsAreServedAsPrometheusText()
    {
        var response = await GetFromAsync(ObservabilityEndpoints.MetricsRoute, Loopback, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/plain");
        response.Content.Headers.ContentType?.Parameters.ShouldContain(parameter => parameter.Name == "version" && parameter.Value == "0.0.4");
        response.Headers.CacheControl?.NoStore.ShouldBeTrue();
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("# TYPE auth_http_requests_total counter");
    }

    [Fact]
    public async Task MetricsAreServedToTheMonitoringNetworkButNotToAnyoneElse()
    {
        var ct = TestContext.Current.CancellationToken;

        (await GetFromAsync(ObservabilityEndpoints.MetricsRoute, MonitoringServer, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await GetFromAsync(ObservabilityEndpoints.MetricsRoute, Loopback, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var stranger = await GetFromAsync(ObservabilityEndpoints.MetricsRoute, Stranger, ct);
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound, "the endpoint does not show itself to the internet");
        (await stranger.Content.ReadAsStringAsync(ct)).ShouldNotContain("auth_");
    }

    [Fact]
    public async Task MetricsAreNotServedToACallerWhoseAddressIsUnknown()
    {
        var response = await Client.GetAsync(ObservabilityEndpoints.MetricsRoute, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OnlyLoopbackMayReadTheMetricsWhenNoNetworkIsListed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthApiHarness.CreateAsync(Postgres, ct, new HarnessOptions
        {
            ConfigureServices = services => services.AddTransient<IStartupFilter, PeerStartupFilter>(),
        });

        (await Get(harness, Loopback, ct)).ShouldBe(HttpStatusCode.OK);
        (await Get(harness, "::1", ct)).ShouldBe(HttpStatusCode.OK);
        (await Get(harness, "::ffff:127.0.0.1", ct)).ShouldBe(HttpStatusCode.OK);
        (await Get(harness, MonitoringServer, ct)).ShouldBe(HttpStatusCode.NotFound);

        static async Task<HttpStatusCode> Get(AuthApiHarness harness, string peer, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ObservabilityEndpoints.MetricsRoute);
            request.Headers.Add(PeerStartupFilter.Header, peer);
            return (await harness.Client.SendAsync(request, ct)).StatusCode;
        }
    }

    [Theory]
    [InlineData("10.0.0.0")]
    [InlineData("not a network")]
    [InlineData("10.0.0.0/33")]
    public async Task AnUnusableNetworkSettingStopsTheServiceFromStarting(string network)
    {
        var failure = await Should.ThrowAsync<Exception>(() => AuthApiHarness.CreateAsync(
            Postgres,
            TestContext.Current.CancellationToken,
            new HarnessOptions { Settings = new() { ["Observability:AllowedNetworks:0"] = network } }));

        var messages = new List<string>();
        for (var inner = failure; inner is not null; inner = inner.InnerException)
        {
            messages.Add(inner.Message);
        }

        string.Join(" | ", messages).ShouldContain("Observability:AllowedNetworks");
    }

    [Fact]
    public async Task ABlankNetworkSettingIsTreatedAsNotSet()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthApiHarness.CreateAsync(Postgres, ct, new HarnessOptions
        {
            Settings = new() { ["Observability:AllowedNetworks:0"] = "" },
        });

        (await harness.Client.GetAsync(ObservabilityEndpoints.LiveRoute, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<string> MetricsAsync(string peer, CancellationToken ct)
    {
        var response = await GetFromAsync(ObservabilityEndpoints.MetricsRoute, peer, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private Task<HttpResponseMessage> GetFromAsync(string path, string peer, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(PeerStartupFilter.Header, peer);
        return Client.SendAsync(request, ct);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken));

    private EmailOutboxMessage Email(EmailOutboxStatus status, DateTimeOffset createdAt, DateTimeOffset nextAttemptAt) => new()
    {
        Id = Guid.NewGuid(),
        ToAddress = "someone@acme.com",
        Template = EmailTemplate.Verification,
        Status = status,
        NextAttemptAt = nextAttemptAt,
        CreatedAt = createdAt,
    };

    private static WebhookOutboxMessage Event(Guid applicationId, WebhookStatus status, DateTimeOffset createdAt, DateTimeOffset nextAttemptAt) => new()
    {
        Id = Guid.NewGuid(),
        ApplicationId = applicationId,
        EventType = "test.event",
        PayloadJson = "{}",
        Status = status,
        NextAttemptAt = nextAttemptAt,
        DeadlineAt = nextAttemptAt.AddDays(1),
        CreatedAt = createdAt,
    };
}
