using System.Net;
using AuthService.Web.Api;
using Microsoft.AspNetCore.Http;

namespace AuthService.Web.Tests;

/// <summary>
/// Behind a proxy the web app calls the API for its visitors, so the visitor's address has to travel with each call: the API's per-address
/// limits and audit entries depend on it. The address is the one the web app worked out itself, believing only its listed proxies.
/// </summary>
public sealed class ClientAddressTests
{
    private const string Proxy = "10.0.0.5";
    private const string Visitor = "198.51.100.9";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AVisitorBehindATrustedProxyReachesTheApiWithTheirOwnAddress()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = Factory(api, ("AuthService:Proxy:TrustedProxies:0", Proxy));

        await ForgotAsync(factory, peer: Proxy, forwardedFor: Visitor);

        ForwardedForSeenByTheApi(api).ShouldBe(Visitor);
    }

    [Fact]
    public async Task ValuesTheVisitorPrependedToTheForwardedHeaderAreNotBelieved()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = Factory(api, ("AuthService:Proxy:TrustedProxies:0", Proxy));

        // The visitor sent "6.6.6.6"; the proxy appended the address it really saw.
        await ForgotAsync(factory, peer: Proxy, forwardedFor: $"6.6.6.6, {Visitor}");

        ForwardedForSeenByTheApi(api).ShouldBe(Visitor);
    }

    [Fact]
    public async Task AHeaderFromAnAddressThatIsNotAListedProxyIsIgnored()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = Factory(api, ("AuthService:Proxy:TrustedProxies:0", Proxy));

        await ForgotAsync(factory, peer: "203.0.113.77", forwardedFor: "6.6.6.6");

        ForwardedForSeenByTheApi(api).ShouldBe("203.0.113.77", "a visitor cannot choose their own address");
    }

    [Fact]
    public async Task WithNoProxyListedTheForwardedHeaderIsNeverRead()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = Factory(api);

        await ForgotAsync(factory, peer: "203.0.113.77", forwardedFor: "6.6.6.6");

        ForwardedForSeenByTheApi(api).ShouldBe("203.0.113.77");
    }

    [Fact]
    public async Task ANetworkCanBeListedInsteadOfASingleProxy()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = Factory(api, ("AuthService:Proxy:TrustedNetworks:0", "172.29.200.0/24"));

        await ForgotAsync(factory, peer: "172.29.200.7", forwardedFor: Visitor);

        ForwardedForSeenByTheApi(api).ShouldBe(Visitor);
    }

    [Theory]
    [InlineData("AuthService:Proxy:TrustedProxies:0", "not an address")]
    [InlineData("AuthService:Proxy:TrustedNetworks:0", "10.0.0.5")]
    [InlineData("AuthService:Proxy:ForwardLimit", "0")]
    public void AnUnusableProxySettingStopsTheAppFromStarting(string key, string value)
    {
        var failure = Should.Throw<Exception>(() =>
        {
            using var factory = Factory(FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json"), (key, value));
            factory.CreateClient();
        });

        Flatten(failure).ShouldContain("AuthService:Proxy");
    }

    [Fact]
    public async Task TheHandlerReplacesAnyAddressTheRequestAlreadyCarriedAndSendsNoneWithoutARequest()
    {
        var seen = new List<string?>();
        var inner = new FakeApiHandler(request =>
        {
            seen.Add(request.Headers.TryGetValues(ClientAddressHandler.Header, out var values) ? string.Join(",", values) : null);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var context = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("::ffff:198.51.100.9") } };
        using var withRequest = new HttpClient(new ClientAddressHandler(new FixedAccessor(context)) { InnerHandler = inner });
        using var withoutRequest = new HttpClient(new ClientAddressHandler(new FixedAccessor(null)) { InnerHandler = inner });

        var spoofed = new HttpRequestMessage(HttpMethod.Get, "https://auth-api.test/x");
        spoofed.Headers.TryAddWithoutValidation(ClientAddressHandler.Header, "6.6.6.6");
        await withRequest.SendAsync(spoofed, Token);
        var unspoofed = new HttpRequestMessage(HttpMethod.Get, "https://auth-api.test/x");
        unspoofed.Headers.TryAddWithoutValidation(ClientAddressHandler.Header, "6.6.6.6");
        await withoutRequest.SendAsync(unspoofed, Token);

        seen.ShouldBe([Visitor, null], "an IPv4-mapped address is sent as plain IPv4, and nothing is invented without a request");
    }

    [Fact]
    public async Task WithAFolderSetTheKeysThatProtectTheCookiesAreKeptThere()
    {
        var folder = Path.Combine(Path.GetTempPath(), "authservice-web-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var factory = Factory(FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json"), ("AuthService:DataProtection:KeysPath", folder));

            // A page with a form makes the app protect something, which creates a key.
            using var client = factory.CreateClient();
            (await client.GetAsync("/forgot-password", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

            Directory.GetFiles(folder, "key-*.xml").ShouldNotBeEmpty("a restart will find the same keys, so cookies stay readable");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>HttpContextAccessor keeps its context in shared ambient storage, so two of them would see each other's request.</summary>
    private sealed class FixedAccessor(HttpContext? context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static WebAppFactory Factory(FakeApiHandler api, params (string Key, string Value)[] settings) =>
        new(api, settings: settings.ToDictionary(setting => setting.Key, setting => setting.Value), peerFromHeader: true);

    private static async Task ForgotAsync(WebAppFactory factory, string peer, string forwardedFor)
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(PeerStartupFilter.Header, peer);
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwardedFor);

        (await WebForms.PostFormAsync(client, "/forgot-password", "forgot", new Dictionary<string, string> { ["forgot-email"] = "anna@acme.com" }))
            .StatusCode.ShouldBeOneOf(HttpStatusCode.OK, HttpStatusCode.Redirect);
    }

    private static string? ForwardedForSeenByTheApi(FakeApiHandler api)
    {
        var request = api.Requests.ShouldHaveSingleItem();
        return request.Headers.TryGetValues(ClientAddressHandler.Header, out var values) ? string.Join(",", values) : null;
    }

    private static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var inner = exception; inner is not null; inner = inner.InnerException)
        {
            messages.Add(inner.Message);
        }

        return string.Join(" | ", messages);
    }
}
