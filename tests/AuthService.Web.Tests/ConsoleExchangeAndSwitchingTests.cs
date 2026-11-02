using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>How the console reacts when the token exchange says no or slow down (UI05_T3), and that switching organisation starts fresh (UI05_T5). No real API is involved.</summary>
public sealed class ConsoleExchangeAndSwitchingTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Beta = Guid.Parse("8e9f7ca3-3e1c-4054-8f1b-1e7b1f1a5e22");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARateLimitedExchangeSaysSoWithACountdownAndMakesNoMoreExchanges()
    {
        var api = Api();
        api.ExchangeResponse = () => RateLimited(42);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/console/{Acme}/members", Token));

        html.ShouldContain("You're going a little fast");
        html.ShouldContain("You can try again in 42 seconds.");
        html.ShouldContain("data-countdown=\"42\"");
        html.ShouldContain("data-countdown-label=\"Try again in {0}s\"");
        html.ShouldContain($"action=\"/console/{Acme}/members\"");
        html.ShouldNotContain("We couldn't load the members", customMessage: "the rate limit is the reason, not a failure");
        api.ExchangeCalls.ShouldHaveSingleItem("the page asked once and did not retry");
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(0, 1)]
    [InlineData(100000, 3600)]
    public async Task AMissingOrOddWaitIsKeptSensible(int? retryAfter, int expected)
    {
        var api = Api();
        api.ExchangeResponse = () =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            if (retryAfter is { } seconds)
            {
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
            }

            return response;
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"/console/{Acme}/members", Token);

        html.ShouldContain($"data-countdown=\"{expected}\"");
    }

    [Fact]
    public async Task ARateLimitedConsoleExchangeNeverStopsThePersonChoosingAProduct()
    {
        var api = Api();
        api.ExchangeResponse = () => RateLimited(10);
        api.MyApplicationsJson = $$"""[{"applicationId":"{{Guid.NewGuid()}}","key":"helpdesk","name":"Helpdesk","state":"NoAccess","launchUrl":null}]""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"/choose-product?tenantId={Acme}", Token);

        html.ShouldContain("Helpdesk");
        html.ShouldNotContain("Admin console", customMessage: "the console entry waits until the exchange works");
        html.ShouldNotContain("data-countdown");
    }

    [Fact]
    public async Task ARefusedExchangeIsExplainedWithoutSayingMoreThanTheApiDoes()
    {
        var api = Api();
        api.ExchangeResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var console = WebUtility.HtmlDecode(await client.GetStringAsync($"/console/{Acme}/members", Token));

        console.ShouldContain("You don't have access");
        console.ShouldContain("You're not an active member of this organisation");
        console.ShouldNotContain("You're going a little fast");
    }

    [Fact]
    public async Task BrowsingTheConsoleRepeatedlyMakesOneExchange()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        foreach (var page in new[] { "members", "roles", "api-keys", "subscriptions", "audit", "members", "roles" })
        {
            (await client.GetAsync($"/console/{Acme}/{page}", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        api.ExchangeCalls.ShouldHaveSingleItem("the token is kept and reused until close to its end");
    }

    [Fact]
    public async Task SwitchingOrganisationGetsAFreshTokenAndTheOldOneIsNeverUsedAgain()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var inAcme = await client.GetStringAsync($"/console/{Acme}/members", Token);
        await client.GetStringAsync("/choose-organisation?change=1", Token);
        var inBeta = await client.GetStringAsync($"/console/{Beta}/members", Token);
        await client.GetStringAsync("/choose-organisation?change=1", Token);
        await client.GetStringAsync($"/console/{Acme}/members", Token);

        api.ProductTokensIssued.Count.ShouldBe(3, "each arrival traded for a new token");
        var acmeFirst = api.ProductTokensIssued[0];
        var betaToken = api.ProductTokensIssued[1];
        var acmeAgain = api.ProductTokensIssued[2];
        api.MembersAuthorizations.ShouldBe([$"Bearer {acmeFirst}", $"Bearer {betaToken}", $"Bearer {acmeAgain}"]);
        acmeAgain.ShouldNotBe(acmeFirst, "the token for the organisation left is not reused");
        api.ExchangeCalls.Select(call => Regex.Match(call.Body, "\"tenantId\":\"([^\"]+)\"").Groups[1].Value)
            .ShouldBe([Acme.ToString(), Beta.ToString(), Acme.ToString()], ignoreOrder: false);

        Header(inAcme).ShouldContain("Acme Ltd");
        Header(inBeta).ShouldContain("Beta Ltd");
        Header(inBeta).ShouldNotContain("Acme Ltd", customMessage: "the header shows the organisation the token is for");
    }

    [Fact]
    public async Task EachOrganisationsPagesUseOnlyItsOwnToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var acmeClient = await SignedInAsync(factory);

        await acmeClient.GetStringAsync($"/console/{Acme}/members", Token);
        await acmeClient.GetStringAsync($"/console/{Beta}/members", Token);

        var tenants = api.ProductTokensIssued.Select(Tenant).ToList();
        tenants.ShouldBe([Acme.ToString(), Beta.ToString()]);
        api.MembersAuthorizations.Count.ShouldBe(2);
        Tenant(api.MembersAuthorizations[0]!.Replace("Bearer ", "")).ShouldBe(Acme.ToString());
        Tenant(api.MembersAuthorizations[1]!.Replace("Bearer ", "")).ShouldBe(Beta.ToString());
    }

    private static string Header(string html) =>
        Regex.Match(html, "<header class=\"console-header\">.*?</header>", RegexOptions.Singleline).Value;

    /// <summary>The organisation a product token is for.</summary>
    private static string Tenant(string token)
    {
        var padded = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Regex.Match(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded)), "\"tid\":\"([^\"]+)\"").Groups[1].Value;
    }

    private static HttpResponseMessage RateLimited(int seconds)
    {
        var response = new HttpResponseMessage((HttpStatusCode)429);
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"},{"tenantId":"{{Beta}}","tenantName":"Beta Ltd","status":"Active"}]""",
        MembersJson = "[]",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
