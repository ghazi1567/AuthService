using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The verify-email page (UI01_T4): opened from an email link, it checks the link with a form post and says what happened.</summary>
public sealed class VerifyEmailPageTests
{
    private const string LinkToken = "tok_7f3a9c1e5b2d4a68";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningTheLinkShowsTheCheckingStateAndHoldsTheTokenOnlyInAFormField()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/verify-email?token={LinkToken}", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        html.ShouldContain("Checking your link…");
        html.ShouldContain("we remove the link from the address bar");
        html.ShouldContain("""data-link-form="auto" """.TrimEnd(), customMessage: "the script sends the form once the address is clean");
        Regex.Matches(html, Regex.Escape(LinkToken)).Count.ShouldBe(1, "the token appears once, in the hidden field, and in no link or text");
        Regex.IsMatch(html, """<input[^>]*type="hidden"[^>]*name="token"[^>]*value="tok_7f3a9c1e5b2d4a68""").ShouldBeTrue();
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"], "the token cannot leak through a referrer");
        response.Headers.GetValues("Cache-Control").ShouldContain("no-store");
    }

    [Fact]
    public async Task TheScriptTakesTheTokenOutOfTheAddressBarAndHistoryBeforeSendingTheForm()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync($"/verify-email?token={LinkToken}", Token);
        var script = Regex.Match(html, """<script src="(?<src>[^"]*app[^"]*\.js)"></script>""").Groups["src"].Value;
        script.ShouldNotBeNullOrEmpty();

        var js = await client.GetStringAsync("/" + script.TrimStart('/'), Token);

        var replace = js.IndexOf("history.replaceState(null, \"\", window.location.pathname)", StringComparison.Ordinal);
        var submit = js.IndexOf("form.requestSubmit()", replace, StringComparison.Ordinal);
        replace.ShouldBeGreaterThan(-1);
        submit.ShouldBeGreaterThan(replace, "the address is cleaned before the form is sent");
    }

    [Fact]
    public async Task CheckingAGoodLinkVerifiesTheEmailAndLeadsOnToSignIn()
    {
        string? sentBody = null;
        var api = new FakeApiHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"verified":true}""", Encoding.UTF8, "application/json"),
            };
        });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, $"/verify-email?token={LinkToken}", "verify", new Dictionary<string, string>());

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var next = posted.Headers.Location!.ToString();
        next.ShouldEndWith("/verify-email?result=verified");
        next.ShouldNotContain(LinkToken);
        sentBody.ShouldBe($$"""{"token":"{{LinkToken}}"}""");
        api.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldBe("/api/auth/email/verify");

        var html = await client.GetStringAsync("/verify-email?result=verified", Token);
        html.ShouldContain("Your email is verified");
        html.ShouldContain("""href="/login">Continue to sign in</a>""");
    }

    [Fact]
    public async Task AnExpiredOrUsedLinkSaysSoAndOffersANewOne()
    {
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Gone, "verification_link_invalid"));
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, $"/verify-email?token={LinkToken}", "verify", new Dictionary<string, string>());

        posted.Headers.Location!.ToString().ShouldEndWith("/verify-email?result=invalid");
        var html = await client.GetStringAsync("/verify-email?result=invalid", Token);
        html.ShouldContain("This link can't be used");
        html.ShouldContain("It has expired or was already used.");
        html.ShouldContain("""<label for="verify-email">Email</label>""");
        html.ShouldContain("Send a new link");
    }

    [Fact]
    public async Task OpeningThePageWithoutALinkOffersANewOneToo()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/verify-email", Token);

        html.ShouldContain("This link can't be used");
        html.ShouldContain("Send a new link");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedKeepsTheLinkOnThePageForAnotherTryWithoutSendingItAgainByItself()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("connection refused")));
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, $"/verify-email?token={LinkToken}", "verify", new Dictionary<string, string>());
        var html = await posted.Content.ReadAsStringAsync(Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("We couldn&#x27;t check your link");
        html.ShouldContain("Your link is still good.");
        html.ShouldContain(">Try again</button>");
        html.ShouldNotContain("data-link-form", customMessage: "no loop of automatic retries");
        html.ShouldNotContain("connection refused");
        Regex.IsMatch(html, """<input[^>]*name="token"[^>]*value="tok_7f3a9c1e5b2d4a68""").ShouldBeTrue("the link is kept for the retry");
    }

    [Fact]
    public async Task AskingForANewLinkGivesTheSameNeutralAnswerAndStartsTheWait()
    {
        string? resendBody = null;
        var api = new FakeApiHandler(request =>
        {
            resendBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("""{"message":"x"}""", Encoding.UTF8, "application/json") };
        });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, "/verify-email?result=invalid", "resend", new Dictionary<string, string> { ["verify-email"] = " anna@acme.com " });
        var html = await posted.Content.ReadAsStringAsync(Token);

        html.ShouldContain("Check your email");
        html.ShouldContain("If anna@acme.com belongs to an account that isn&#x27;t verified yet, we&#x27;ve sent a new link. The link works for 24 hours.");
        html.ShouldContain("""data-countdown="60" """.TrimEnd());
        resendBody.ShouldBe("""{"email":"anna@acme.com"}""");
        api.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldBe("/api/auth/email/verify/resend");
    }

    [Fact]
    public async Task ABadEmailIsRefusedBeforeTheApiIsAsked()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, "/verify-email?result=invalid", "resend", new Dictionary<string, string> { ["verify-email"] = "anna" });
        var html = await posted.Content.ReadAsStringAsync(Token);

        html.ShouldContain("Enter a valid email address, like anna@acme.com.");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AskingTooOftenShowsTheWaitOnTheButton()
    {
        var api = new FakeApiHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"status":429,"title":"x","detail":"x","code":"rate_limited"}""", Encoding.UTF8, "application/problem+json"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await WebForms.PostFormAsync(client, "/verify-email?result=invalid", "resend", new Dictionary<string, string> { ["verify-email"] = "anna@acme.com" });
        var html = await posted.Content.ReadAsStringAsync(Token);

        html.ShouldContain("Too many requests");
        html.ShouldContain("Wait 30 seconds, then try again.");
        html.ShouldContain("""data-countdown="30" """.TrimEnd());
    }

    [Fact]
    public async Task ResendingFromTheConfirmationSendsAnotherLinkAndSaysSo()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, """{"message":"x"}""", "application/json");
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        var first = await WebForms.PostFormAsync(client, "/verify-email?result=invalid", "resend", new Dictionary<string, string> { ["verify-email"] = "anna@acme.com" });
        var sent = await first.Content.ReadAsStringAsync(Token);

        var again = await WebForms.PostFormFromHtmlAsync(client, "/verify-email", sent, "again");
        var html = await again.Content.ReadAsStringAsync(Token);

        html.ShouldContain("We&#x27;ve sent it again");
        html.ShouldContain("""data-countdown="60" """.TrimEnd());
        api.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheLinkTokenIsNeverLogged()
    {
        var logs = new CapturingLogs();
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Gone, "verification_link_invalid"), logs: logs);
        using var client = NewClient(factory);

        await client.GetAsync($"/verify-email?token={LinkToken}", Token);
        await WebForms.PostFormAsync(client, $"/verify-email?token={LinkToken}", "verify", new Dictionary<string, string>());

        string.Join('\n', logs.Lines).ShouldNotContain(LinkToken);
    }

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static FakeApiHandler Problem(HttpStatusCode status, string code) =>
        FakeApiHandler.Json(status, $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""");
}
