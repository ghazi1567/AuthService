using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Proxy;

/// <summary>
/// Behind a reverse proxy the client's address comes from X-Forwarded-For, but only from a proxy that is listed in
/// <c>Auth:Proxy</c>. In-process requests have no address, so the tests say which peer a request comes from with the
/// <c>X-Test-Peer</c> header. These tests have no ticket: they close a known gap (per-IP limits and audit entries saw the proxy).
/// </summary>
public sealed class ForwardedHeadersTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Proxy = "10.0.0.5";
    private const string Client1 = "198.51.100.9";
    private const string Client2 = "198.51.100.10";

    protected override HarnessOptions Options => new()
    {
        Settings = new()
        {
            ["Auth:Proxy:TrustedProxies:0"] = Proxy,
            ["Auth:LoginProtection:IpLimit"] = "2",
        },
        ConfigureServices = services => services.AddTransient<IStartupFilter, PeerStartupFilter>(),
    };

    [Fact]
    public async Task ARequestFromATrustedProxyIsAttributedToTheClientItForwards()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        await FailedLoginAsync(peer: Proxy, forwardedFor: Client1, ct);

        (await LoginFailureIpsAsync(ct)).ShouldBe([Client1]);
    }

    [Fact]
    public async Task AHeaderFromAnAddressThatIsNotAListedProxyIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        await FailedLoginAsync(peer: "203.0.113.77", forwardedFor: "6.6.6.6", ct);

        (await LoginFailureIpsAsync(ct)).ShouldBe(["203.0.113.77"], "a caller cannot choose their own address");
    }

    [Fact]
    public async Task ValuesTheClientPrependedToTheHeaderAreNotBelieved()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        // The client sent "6.6.6.6"; the proxy appended the address it really saw.
        await FailedLoginAsync(peer: Proxy, forwardedFor: $"6.6.6.6, {Client1}", ct);

        (await LoginFailureIpsAsync(ct)).ShouldBe([Client1]);
    }

    [Fact]
    public async Task EachClientBehindTheProxyHasItsOwnRateLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        (await FailedLoginAsync(Proxy, Client1, ct)).ShouldBe(HttpStatusCode.Unauthorized);
        (await FailedLoginAsync(Proxy, Client1, ct)).ShouldBe(HttpStatusCode.Unauthorized);
        (await FailedLoginAsync(Proxy, Client1, ct)).ShouldBe(HttpStatusCode.TooManyRequests, "the first client used its two attempts");

        (await FailedLoginAsync(Proxy, Client2, ct)).ShouldBe(HttpStatusCode.Unauthorized, "another client behind the same proxy is not affected");
    }

    [Fact]
    public async Task ARequestWithNoHeaderFromTheProxyIsAttributedToTheProxy()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        await FailedLoginAsync(peer: Proxy, forwardedFor: null, ct);

        (await LoginFailureIpsAsync(ct)).ShouldBe([Proxy]);
    }

    private async Task<HttpStatusCode> FailedLoginAsync(string peer, string? forwardedFor, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = "owner@acme.com", password = "not the password" }),
        };
        request.Headers.Add(PeerStartupFilter.Header, peer);
        if (forwardedFor is not null)
        {
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        }

        return (await Client.SendAsync(request, ct)).StatusCode;
    }

    private async Task<List<string?>> LoginFailureIpsAsync(CancellationToken ct)
    {
        var ips = new List<string?>();
        await WithDbAsync(async db => ips = await db.AuditEntries.AsNoTracking()
            .Where(entry => entry.Action == AuditActions.LoginFailed)
            .Select(entry => entry.SourceIp)
            .ToListAsync(ct));
        return ips;
    }
}

/// <summary>Sets the connection's remote address from the <c>X-Test-Peer</c> header, as a real socket would.</summary>
internal sealed class PeerStartupFilter : IStartupFilter
{
    public const string Header = "X-Test-Peer";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue(Header, out var peer))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(peer.ToString());
            }

            await nextMiddleware();
        });
        next(app);
    };
}
