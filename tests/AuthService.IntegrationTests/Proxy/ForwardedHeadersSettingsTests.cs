using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Proxy;

/// <summary>
/// The other ways <c>Auth:Proxy</c> can be set: not at all, as a range, and wrongly. Each test starts its own copy of the service
/// with its own settings and reads the address recorded for a signup.
/// </summary>
public sealed class ForwardedHeadersSettingsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task WithNoProxyListedTheHeaderIsNeverRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await StartAsync(new Dictionary<string, string?>(), ct);

        await SignUpFromAsync(harness, "a@acme.com", peer: "10.0.0.5", forwardedFor: "198.51.100.9", ct);

        (await SignupIpsAsync(harness, ct)).ShouldBe(["10.0.0.5"], "a forwarded address is only believed from a listed proxy");
    }

    [Fact]
    public async Task AProxyInsideAListedRangeIsTrustedAndOneOutsideIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await StartAsync(new Dictionary<string, string?> { ["Auth:Proxy:TrustedNetworks:0"] = "10.1.0.0/16" }, ct);

        await SignUpFromAsync(harness, "a@acme.com", peer: "10.1.7.7", forwardedFor: "198.51.100.9", ct);
        await SignUpFromAsync(harness, "b@acme.com", peer: "10.2.7.7", forwardedFor: "198.51.100.10", ct);

        (await SignupIpsAsync(harness, ct)).ShouldBe(["198.51.100.9", "10.2.7.7"], ignoreOrder: true, "the second peer is outside the range, so its header was ignored");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankProxySettingCountsAsNotSetSoAnEmptyEnvironmentVariableIsHarmless(string blank)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await StartAsync(
            new Dictionary<string, string?> { ["Auth:Proxy:TrustedProxies:0"] = blank, ["Auth:Proxy:TrustedNetworks:0"] = blank }, ct);

        await SignUpFromAsync(harness, "a@acme.com", peer: "10.0.0.5", forwardedFor: "198.51.100.9", ct);

        (await SignupIpsAsync(harness, ct)).ShouldBe(["10.0.0.5"], "nothing is configured, so the header is not read");
    }

    [Theory]
    [InlineData("Auth:Proxy:TrustedProxies:0", "not-an-ip")]
    [InlineData("Auth:Proxy:TrustedNetworks:0", "10.0.0.0/99")]
    [InlineData("Auth:Proxy:TrustedNetworks:0", "10.0.0.5")]
    public async Task AnUnusableProxySettingStopsTheServiceFromStarting(string key, string value)
    {
        var failure = await Should.ThrowAsync<Exception>(() => AuthApiHarness.CreateAsync(
            Postgres, TestContext.Current.CancellationToken, new HarnessOptions { Settings = new() { [key] = value } }));

        Flatten(failure).ShouldContain("Auth:Proxy");
    }

    [Fact]
    public async Task AForwardLimitOutsideTheAllowedRangeStopsTheServiceFromStarting()
    {
        var failure = await Should.ThrowAsync<Exception>(() => AuthApiHarness.CreateAsync(
            Postgres,
            TestContext.Current.CancellationToken,
            new HarnessOptions { Settings = new() { ["Auth:Proxy:TrustedProxies:0"] = "10.0.0.5", ["Auth:Proxy:ForwardLimit"] = "0" } }));

        Flatten(failure).ShouldContain("ForwardLimit");
    }

    private Task<AuthApiHarness> StartAsync(Dictionary<string, string?> settings, CancellationToken ct) =>
        AuthApiHarness.CreateAsync(Postgres, ct, new HarnessOptions
        {
            Settings = settings,
            ConfigureServices = services => services.AddTransient<IStartupFilter, PeerStartupFilter>(),
        });

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            messages.Add(inner.Message);
        }

        return string.Join(" | ", messages);
    }

    private static async Task SignUpFromAsync(AuthApiHarness harness, string email, string peer, string forwardedFor, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/signup")
        {
            Content = JsonContent.Create(new { tenantName = "Acme " + email, email, password = DefaultPassword }),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        request.Headers.Add(PeerStartupFilter.Header, peer);
        (await harness.Client.SendAsync(request, ct)).EnsureSuccessStatusCode();
    }

    private static async Task<List<string?>> SignupIpsAsync(AuthApiHarness harness, CancellationToken ct)
    {
        await using var scope = harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.AuditEntries.AsNoTracking()
            .Where(entry => entry.Action == AuditActions.SignupCompleted)
            .Select(entry => entry.SourceIp)
            .ToListAsync(ct);
    }
}
