using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>
/// UI01_T7: the pages that open from an email link (verify email, reset password, accept invitation) keep the link's token out of the
/// address bar, the history, referrers and logs, and no password field ever shows a value. A browser is not run here, so the address
/// bar and history are covered by what the page and its script do; the web UI has been checked by hand in a real browser as well.
/// </summary>
public sealed class LinkTokenSecurityTests
{
    private const string LinkToken = "lnk_0e5a7c3b91d64f28";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> EmailLinkPages => ["/verify-email", "/reset-password", "/accept-invitation"];

    [Theory]
    [MemberData(nameof(EmailLinkPages))]
    public async Task TheTokenIsInOneHiddenFieldAndNowhereElseOnThePage(string path)
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{path}?token={LinkToken}", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        Regex.Matches(html, Regex.Escape(LinkToken)).Count.ShouldBe(1);
        Regex.IsMatch(html, $"""<input[^>]*type="hidden"[^>]*name="token"[^>]*value="{LinkToken}""").ShouldBeTrue();
        Regex.IsMatch(html, $"""(href|src|action)="[^"]*{LinkToken}""").ShouldBeFalse("no link, script or form address carries the token");
        html.ShouldNotContain($"token={LinkToken}");
    }

    [Theory]
    [MemberData(nameof(EmailLinkPages))]
    public async Task TheFormPostsToACleanAddressAndTheScriptRemovesTheTokenFromTheAddressAndHistory(string path)
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync($"{path}?token={LinkToken}", Token);

        Regex.IsMatch(html, $"""<form[^>]*action="{Regex.Escape(path)}"[^>]*data-link-form="(auto|clean)""").ShouldBeTrue(
            "the form posts to the page without its query, and tells the script to clean the address");

        var script = Regex.Match(html, """<script src="(?<src>[^"]*app[^"]*\.js)"></script>""").Groups["src"].Value;
        var js = await client.GetStringAsync("/" + script.TrimStart('/'), Token);
        js.ShouldContain("window.history.replaceState(null, \"\", window.location.pathname)");
        js.ShouldContain("form[data-link-form]");
    }

    [Theory]
    [MemberData(nameof(EmailLinkPages))]
    public async Task NoReferrerIsSentAndTheResponseIsNeverCached(string path)
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"{path}?token={LinkToken}", Token);

        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"], "a link followed from this page tells the next site nothing");
        response.Headers.GetValues("Cache-Control").ShouldContain("no-store");
        response.Headers.ToString().ShouldNotContain(LinkToken);
        response.Content.Headers.ToString().ShouldNotContain(LinkToken);
    }

    [Theory]
    [MemberData(nameof(EmailLinkPages))]
    public async Task TheTokenIsNeverLoggedWhenThePageIsOpenedOrAnswered(string path)
    {
        var logs = new CapturingLogs();
        var api = new FakeAuthApi { AcceptResponse = () => new HttpResponseMessage(HttpStatusCode.Gone) };
        await using var factory = new WebAppFactory(api, logs: logs);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        await client.GetAsync($"{path}?token={LinkToken}", Token);
        await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = LinkToken }), Token);

        string.Join('\n', logs.Lines).ShouldNotContain(LinkToken);
    }

    [Theory]
    [InlineData("/login", "current-password")]
    [InlineData("/signup", "new-password")]
    [InlineData("/reset-password?token=t", "new-password")]
    [InlineData("/accept-invitation?token=t", "new-password")]
    public async Task EveryPasswordFieldSaysWhetherItIsAnOldOrANewPasswordAndStartsEmpty(string path, string autocomplete)
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(path, Token);

        var passwordInputs = Regex.Matches(html, """<input[^>]*type="password"[^>]*>""").Select(match => match.Value).ToList();
        passwordInputs.ShouldNotBeEmpty();
        foreach (var input in passwordInputs)
        {
            input.ShouldContain($"autocomplete=\"{autocomplete}\"");
            Regex.IsMatch(input, "value=\"[^\"]+\"").ShouldBeFalse("a password field never starts with a value");
        }
    }

    [Fact]
    public async Task APasswordIsNeverSentBackIntoAFormEvenWhenTheFormIsShownAgain()
    {
        const string password = "my-secret-password-77";
        var api = new FakeAuthApi
        {
            AcceptResponse = () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"status":429,"title":"x","detail":"x","code":"rate_limited"}""", System.Text.Encoding.UTF8, "application/problem+json"),
            },
        };
        await using var factory = new WebAppFactory(api);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var accept = await WebForms.PostFormAsync(
            client,
            $"/accept-invitation?token={LinkToken}",
            "accept-new",
            new Dictionary<string, string> { ["accept-password"] = password, ["accept-confirm"] = password });
        var signup = await WebForms.PostFormAsync(
            client,
            "/signup",
            "signup",
            new Dictionary<string, string>
            {
                ["signup-name"] = "Anna", ["signup-organisation"] = "Acme", ["signup-email"] = "anna", ["signup-password"] = password,
            });
        var login = await WebForms.PostLoginAsync(client, "anna@acme.com", password);

        foreach (var response in new[] { accept, signup, login })
        {
            (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain(password);
        }
    }
}
