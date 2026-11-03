using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.EmailVerification;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Features.Applications;
using AuthService.Api.RateLimiting;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.IntegrationTests.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AuthService.IntegrationTests.Plt04;

/// <summary>
/// PLT-04 (AUTH-201): the rate-limit counters live in the database, so every instance of the service shares them. Two services run
/// against one database here, as two instances behind a load balancer would. The tests have no workbook Test Case ID: the story's
/// acceptance criteria cover them. In-process requests have no address, so a test says which peer a request comes from with the
/// <c>X-Test-Peer</c> header.
/// </summary>
public sealed class SharedRateLimitTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string Peer = "203.0.113.9";

    protected override HarnessOptions Options => StoreOptions(RateLimitOptions.Postgres);

    internal static HarnessOptions StoreOptions(string store, params (string Key, string Value)[] more)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:RateLimit:Store"] = store,
            ["Auth:LoginProtection:IpLimit"] = "2",
        };
        foreach (var (key, value) in more)
        {
            settings[key] = value;
        }

        return new HarnessOptions
        {
            Settings = settings,
            ConfigureServices = services => services.AddTransient<IStartupFilter, PeerStartupFilter>(),
        };
    }

    [Fact]
    public async Task PLT04_ALimitReachedOnOneInstanceIsEnforcedOnTheOther()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await using var second = new AuthApiFactory(Database.ConnectionString, Options, new CapturingLoggerProvider());
        using var secondClient = second.CreateClient();

        (await WrongPasswordAsync(Client, Peer, ct)).ShouldBe(HttpStatusCode.Unauthorized);
        (await WrongPasswordAsync(secondClient, Peer, ct)).ShouldBe(HttpStatusCode.Unauthorized);

        // Two uses were counted, one on each instance. The limit is two for the whole service, not for each instance.
        var refusedHere = await SendLoginAsync(Client, Peer, ct);
        var refusedThere = await SendLoginAsync(secondClient, Peer, ct);
        refusedHere.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refusedThere.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        refusedThere.Headers.RetryAfter!.Delta!.Value.ShouldBeGreaterThan(TimeSpan.Zero);
        refusedThere.Headers.RetryAfter.Delta.Value.ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(1));
        (await refusedThere.Content.ReadAsStringAsync(ct)).ShouldContain("rate_limited");
    }

    [Fact]
    public async Task PLT04_ADifferentAddressHasItsOwnCounterOnEveryInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await using var second = new AuthApiFactory(Database.ConnectionString, Options, new CapturingLoggerProvider());
        using var secondClient = second.CreateClient();
        await WrongPasswordAsync(Client, Peer, ct);
        await WrongPasswordAsync(secondClient, Peer, ct);

        (await SendLoginAsync(secondClient, Peer, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await WrongPasswordAsync(secondClient, "198.51.100.77", ct)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PLT04_ACounterStartsAgainWhenItsWindowEnds()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await WrongPasswordAsync(Client, Peer, ct);
        await WrongPasswordAsync(Client, Peer, ct);
        (await SendLoginAsync(Client, Peer, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await PostgresContainerFixture.ExecuteAsync(Database.ConnectionString, "UPDATE rate_limit_counters SET expires_at = clock_timestamp() - interval '1 second'", ct);

        (await WrongPasswordAsync(Client, Peer, ct)).ShouldBe(HttpStatusCode.Unauthorized, "the window is over, so the next use is the first of a new one");
        (await HitsAsync(ct)).ShouldBe([1L]);
    }

    [Fact]
    public async Task PLT04_CountersWhoseWindowsAreOverAreRemovedAndLiveOnesAreKept()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await WrongPasswordAsync(Client, "198.51.100.1", ct);
        await WrongPasswordAsync(Client, "198.51.100.2", ct);
        await PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString,
            "UPDATE rate_limit_counters SET expires_at = clock_timestamp() - interval '5 minutes' WHERE key_hash = (SELECT min(key_hash) FROM rate_limit_counters)",
            ct);
        (await HitsAsync(ct)).Count.ShouldBe(2);

        await using var scope = Harness.CreateScope();
        var removed = await scope.ServiceProvider.GetRequiredService<IRateLimitStore>().PurgeExpiredAsync(ct);

        removed.ShouldBe(1);
        (await HitsAsync(ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task PLT04_TheCleanupWorkerRemovesExpiredCountersOnItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var quick = await AuthApiHarness.CreateAsync(Postgres, ct, StoreOptions(RateLimitOptions.Postgres, ("Auth:RateLimit:CleanupSeconds", "1")));
        await PostgresContainerFixture.ExecuteAsync(
            quick.Database.ConnectionString,
            "INSERT INTO rate_limit_counters (key_hash, hits, expires_at) VALUES ('old', 3, clock_timestamp() - interval '10 minutes')",
            ct);

        await Eventually.UntilAsync(
            async () => (long)(await PostgresContainerFixture.ScalarAsync(quick.Database.ConnectionString, "SELECT count(*) FROM rate_limit_counters", ct))! == 0,
            "the worker deletes it");
    }

    [Fact]
    public async Task PLT04_OnlyAHashOfTheCallerIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await WrongPasswordAsync(Client, Peer, ct);

        var keys = await KeysAsync(ct);

        keys.Count.ShouldBe(1);
        keys[0].Length.ShouldBe(43, "a base64url SHA-256");
        keys[0].ShouldNotContain("203");
        keys[0].ShouldNotContain("login");
    }

    [Fact]
    public async Task PLT04_EndpointsWithNoLimitNeverTouchTheStore()
    {
        var ct = TestContext.Current.CancellationToken;

        (await Client.GetAsync("/health/live", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/.well-known/jwks.json", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await KeysAsync(ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task PLT04_ManySimultaneousUsesAreEachCountedExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Harness.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRateLimitStore>();

        var hits = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => store.HitAsync("busy", TimeSpan.FromHours(1), ct)));

        hits.Select(hit => hit.Hits).Order().ShouldBe(Enumerable.Range(1, 40).Select(number => (long)number), "no use lost, none counted twice");
        hits.ShouldAllBe(hit => hit.WindowRemaining > TimeSpan.FromMinutes(59));
    }

    // ---- When the store cannot be reached, nothing fails open ----

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/password/forgot")]
    [InlineData("/api/auth/password/reset")]
    [InlineData("/api/auth/email/verify/resend")]
    [InlineData("/api/auth/token")]
    public async Task PLT04_WhenTheStoreCannotBeReachedTheRequestIsRefusedWith503AndNeverLetThrough(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await PostgresContainerFixture.ExecuteAsync(Database.ConnectionString, "DROP TABLE rate_limit_counters", ct);
        var auditBefore = await AuditCountAsync(ct);

        var response = await Client.PostAsJsonAsync(route, new { email = Email, password = DefaultPassword, token = "x", newPassword = "x", clientId = "x", clientSecret = "x" }, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(5));
        (await response.Content.ReadAsStringAsync(ct)).ShouldContain("rate_limit_unavailable");
        (await AuditCountAsync(ct)).ShouldBe(auditBefore, "the request was refused before it did anything");
        string.Join('\n', Harness.Logs.Entries).ShouldContain("could not be reached");
    }

    [Fact]
    public async Task PLT04_TokenExchangeAlsoFailsClosedWhenTheStoreCannotBeReached()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var identity = (await LoginForTokensAsync(Email, ct)).AccessToken;
        await PostgresContainerFixture.ExecuteAsync(Database.ConnectionString, "DROP TABLE rate_limit_counters", ct);

        var response = await PostAsync("/api/auth/token/exchange", new { tenantId = account.TenantId, applicationId = BuiltInApplication.Id }, identity, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    // ---- Choosing the store ----

    [Fact]
    public async Task PLT04_TheDefaultStoreIsPostgresAndMemoryIsOptIn()
    {
        new RateLimitOptions().Store.ShouldBe(RateLimitOptions.Postgres);
        await using var scope = Harness.CreateScope();
        scope.ServiceProvider.GetRequiredService<IRateLimitStore>().ShouldBeOfType<PostgresRateLimitStore>();

        await using var memory = await AuthApiHarness.CreateAsync(Postgres, TestContext.Current.CancellationToken, StoreOptions(RateLimitOptions.Memory));
        await using var memoryScope = memory.CreateScope();
        memoryScope.ServiceProvider.GetRequiredService<IRateLimitStore>().ShouldBeOfType<InMemoryRateLimitStore>();
    }

    [Theory]
    [InlineData("Auth:RateLimit:Store", "Redis")]
    [InlineData("Auth:RateLimit:Store", "")]
    [InlineData("Auth:RateLimit:CleanupSeconds", "0")]
    public async Task PLT04_AnUnusableSettingStopsTheServiceFromStarting(string key, string value)
    {
        var failure = await Should.ThrowAsync<Exception>(() => AuthApiHarness.CreateAsync(
            Postgres, TestContext.Current.CancellationToken, new HarnessOptions { Settings = new() { [key] = value } }));

        var messages = new List<string>();
        for (var inner = failure; inner is not null; inner = inner.InnerException)
        {
            messages.Add(inner.Message);
        }

        string.Join(" | ", messages).ShouldContain("Auth:RateLimit");
    }

    // ---- helpers ----

    private Task<HttpResponseMessage> SendLoginAsync(HttpClient client, string peer, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginEndpoints.Route)
        {
            Content = JsonContent.Create(new { email = Email, password = "not the password" }),
        };
        request.Headers.Add(PeerStartupFilter.Header, peer);
        return client.SendAsync(request, ct);
    }

    private async Task<HttpStatusCode> WrongPasswordAsync(HttpClient client, string peer, CancellationToken ct) =>
        (await SendLoginAsync(client, peer, ct)).StatusCode;

    private async Task<List<long>> HitsAsync(CancellationToken ct)
    {
        var hits = new List<long>();
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT hits FROM rate_limit_counters ORDER BY hits", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            hits.Add(reader.GetInt64(0));
        }

        return hits;
    }

    private async Task<List<string>> KeysAsync(CancellationToken ct)
    {
        var keys = new List<string>();
        await using var connection = new NpgsqlConnection(Database.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT key_hash FROM rate_limit_counters", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private async Task<long> AuditCountAsync(CancellationToken ct) =>
        (long)(await PostgresContainerFixture.ScalarAsync(Database.ConnectionString, "SELECT count(*) FROM audit_entries", ct))!;
}

/// <summary>The other mode: with <c>Memory</c> each instance counts on its own, which is why it is not the production default.</summary>
public sealed class InMemoryRateLimitTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string Peer = "203.0.113.9";

    protected override HarnessOptions Options => SharedRateLimitTests.StoreOptions(RateLimitOptions.Memory);

    [Fact]
    public async Task PLT04_WithTheMemoryStoreEachInstanceHasItsOwnCountersAndNothingIsWrittenToTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await using var second = new AuthApiFactory(Database.ConnectionString, Options, new CapturingLoggerProvider());
        using var secondClient = second.CreateClient();

        (await Send(Client, ct)).ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(Client, ct)).ShouldBe(HttpStatusCode.Unauthorized);
        (await Send(Client, ct)).ShouldBe(HttpStatusCode.TooManyRequests, "this instance's limit is reached");

        (await Send(secondClient, ct)).ShouldBe(HttpStatusCode.Unauthorized, "the other instance has not counted any of it");
        ((long)(await PostgresContainerFixture.ScalarAsync(Database.ConnectionString, "SELECT count(*) FROM rate_limit_counters", ct))!).ShouldBe(0L);
    }

    [Fact]
    public async Task PLT04_TheMemoryStoreCountsCallersApartAndLetsAWindowEnd()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryRateLimitStore();

        (await store.HitAsync("a", TimeSpan.FromHours(1), ct)).Hits.ShouldBe(1);
        (await store.HitAsync("a", TimeSpan.FromHours(1), ct)).Hits.ShouldBe(2);
        (await store.HitAsync("b", TimeSpan.FromHours(1), ct)).Hits.ShouldBe(1);

        (await store.HitAsync("short", TimeSpan.FromMilliseconds(50), ct)).Hits.ShouldBe(1);
        await Task.Delay(120, ct);
        (await store.HitAsync("short", TimeSpan.FromMilliseconds(50), ct)).Hits.ShouldBe(1, "a new window");

        await Task.Delay(120, ct);
        (await store.PurgeExpiredAsync(ct)).ShouldBe(1, "only the finished one goes");
    }

    private async Task<HttpStatusCode> Send(HttpClient client, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, LoginEndpoints.Route)
        {
            Content = JsonContent.Create(new { email = Email, password = "not the password" }),
        };
        request.Headers.Add(PeerStartupFilter.Header, Peer);
        return (await client.SendAsync(request, ct)).StatusCode;
    }
}
