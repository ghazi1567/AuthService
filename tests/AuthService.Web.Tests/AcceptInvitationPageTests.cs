using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The accept-invitation page (UI01_T6): a new person chooses a password, an existing one accepts while signed in.</summary>
public sealed class AcceptInvitationPageTests
{
    private const string LinkToken = "inv_91c4e7a02f5b6d38";
    private const string Password = "a-brand-new-password";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OpeningTheLinkAsANewPersonShowsAPasswordFormWithTheTokenOnlyInAFormField()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/accept-invitation?token={LinkToken}", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        html.ShouldContain("""<h1 class="auth-title">Accept your invitation</h1>""");
        html.ShouldContain("Choose a password to create your account and join the team.");
        html.ShouldContain("""<label for="accept-password">Password</label>""");
        html.ShouldContain("""<label for="accept-confirm">Confirm password</label>""");
        html.ShouldContain("At least 8 characters");
        html.ShouldContain("""action="/accept-invitation" """.TrimEnd());
        html.ShouldContain("""data-link-form="clean" """.TrimEnd());
        Regex.Matches(html, Regex.Escape(LinkToken)).Count.ShouldBe(1, "the token is in the hidden field and nowhere else");
        html.ShouldContain("""href="/login?returnUrl=%2Faccept-invitation">Sign in to accept</a>""", customMessage: "the sign-in link carries no token");
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("Cache-Control").ShouldContain("no-store");
    }

    [Fact]
    public async Task TheLinkWaitsInAnEncryptedHttpOnlyCookieSoItNeverTravelsInTheLoginAddress()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = NewClient(factory);

        var response = await client.GetAsync($"/accept-invitation?token={LinkToken}", Token);

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("invitation_pending="));
        cookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.ToLowerInvariant().ShouldContain("samesite=lax");
        cookie.ShouldNotContain(LinkToken, customMessage: "the cookie is encrypted");

        // Coming back after signing in, the address has no token but the link is still there.
        var again = await client.GetStringAsync("/accept-invitation", Token);
        again.ShouldContain("Accept your invitation");
        Regex.IsMatch(again, """<input[^>]*name="token"[^>]*value="inv_91c4e7a02f5b6d38""").ShouldBeTrue();
    }

    [Fact]
    public async Task ATamperedCookieIsTreatedAsNoLinkAtAll()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", "invitation_pending=not-a-real-cookie");

        var html = await client.GetStringAsync("/accept-invitation", Token);

        html.ShouldContain("This invitation can't be used");
    }

    [Fact]
    public async Task OpeningThePageWithoutALinkSaysTheInvitationCannotBeUsed()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/accept-invitation", Token);

        html.ShouldContain("This invitation can't be used");
        html.ShouldContain("It has expired, was already used, or was withdrawn.");
    }

    [Fact]
    public async Task ANewPersonWhoChoosesAPasswordJoinsAndTheWaitingLinkIsCleared()
    {
        var api = new FakeAuthApi { AcceptResponse = () => Json(HttpStatusCode.OK, """{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","membershipId":"1b8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d12"}""") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await AcceptNewAsync(client, Password, Password);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var next = posted.Headers.Location!.ToString();
        next.ShouldEndWith("/accept-invitation?result=done");
        next.ShouldNotContain(LinkToken);
        var call = api.AcceptCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBeNull("a new person is not signed in");
        call.Body.ShouldBe($$"""{"token":"{{LinkToken}}","password":"{{Password}}"}""");
        posted.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith("invitation_pending=;"), "the used link stops waiting");

        var html = await client.GetStringAsync("/accept-invitation?result=done", Token);
        html.ShouldContain("You've joined the team");
        html.ShouldContain("""href="/">Continue</a>""");
    }

    [Fact]
    public async Task PasswordsThatDoNotMatchOrAreTooShortAreRefusedBeforeTheApiIsAsked()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var mismatch = await ReadAsync(await AcceptNewAsync(client, Password, "something-different"));
        var tooShort = await ReadAsync(await AcceptNewAsync(client, "short", "short"));

        mismatch.ShouldContain("The two passwords don&#x27;t match.");
        tooShort.ShouldContain("Use between 8 and 128 characters.");
        api.AcceptCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnInvitationThatIsExpiredUsedOrWithdrawnSaysSoAndStopsWaiting()
    {
        var api = new FakeAuthApi { AcceptResponse = () => Problem(HttpStatusCode.Gone, "invitation_invalid") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await AcceptNewAsync(client, Password, Password);

        posted.Headers.Location!.ToString().ShouldEndWith("/accept-invitation?result=invalid");
        posted.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith("invitation_pending=;"));
        var html = await client.GetStringAsync("/accept-invitation?result=invalid", Token);
        html.ShouldContain("This invitation can't be used");
        html.ShouldContain("Ask the person who invited you to send a new one.");
    }

    [Fact]
    public async Task AnEmailThatAlreadyHasAnAccountIsAskedToSignInAndTheLinkKeepsWaiting()
    {
        var api = new FakeAuthApi { AcceptResponse = () => Problem(HttpStatusCode.Unauthorized, "login_required") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await AcceptNewAsync(client, Password, Password);
        var html = await ReadAsync(posted);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Sign in to accept");
        html.ShouldContain("already has an account");
        html.ShouldContain("""href="/login?returnUrl=%2Faccept-invitation">Sign in</a>""");
        html.ShouldNotContain(LinkToken);
        html.ShouldNotContain(Password);
        posted.Headers.TryGetValues("Set-Cookie", out var cookies).ShouldBeTrue();
        cookies!.ShouldNotContain(cookie => cookie.StartsWith("invitation_pending=;"), "the link must survive the sign-in");
    }

    [Fact]
    public async Task APasswordTheApiRefusesShowsItsReasonBesideTheFieldAndKeepsTheLink()
    {
        var api = new FakeAuthApi
        {
            AcceptResponse = () => Json(HttpStatusCode.BadRequest, """{"status":400,"title":"x","errors":{"Password":["Password must be between 8 and 128 characters."]}}"""),
        };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var html = await ReadAsync(await AcceptNewAsync(client, Password, Password));

        html.ShouldContain("Password must be between 8 and 128 characters.");
        html.ShouldNotContain(Password);
        Regex.IsMatch(html, """<input[^>]*name="token"[^>]*value="inv_91c4e7a02f5b6d38""").ShouldBeTrue();
    }

    [Fact]
    public async Task ARateLimitKeepsTheLinkAndShowsTheWait()
    {
        var api = new FakeAuthApi
        {
            AcceptResponse = () =>
            {
                var response = Problem(HttpStatusCode.TooManyRequests, "rate_limited");
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return response;
            },
        };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var html = await ReadAsync(await AcceptNewAsync(client, Password, Password));

        html.ShouldContain("Too many attempts");
        html.ShouldContain("Wait 30 seconds, then try again.");
        html.ShouldContain("""data-countdown="30" """.TrimEnd());
        html.ShouldContain(LinkToken);
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedKeepsTheLinkAndGivesACalmMessage()
    {
        var api = new FakeAuthApi { AcceptResponse = () => throw new HttpRequestException("connection refused") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var html = await ReadAsync(await AcceptNewAsync(client, Password, Password));

        html.ShouldContain("We couldn&#x27;t accept the invitation");
        html.ShouldContain("Your invitation is still good.");
        html.ShouldNotContain("connection refused");
        html.ShouldContain(LinkToken);
    }

    [Fact]
    public async Task SomeoneWithAnAccountWhoIsSignedInAcceptsAsThemselvesWithoutAPassword()
    {
        var api = new FakeAuthApi { AcceptResponse = () => Json(HttpStatusCode.OK, """{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","membershipId":"1b8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d12"}""") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password");

        var page = await client.GetStringAsync($"/accept-invitation?token={LinkToken}", Token);
        page.ShouldContain("You&#x27;re signed in as", Case.Sensitive);
        page.ShouldContain("anna@acme.com");
        page.ShouldContain(">Accept invitation</button>");
        page.ShouldContain("Sign out and switch account");
        page.ShouldNotContain("Confirm password", customMessage: "an existing account sets no password");

        var posted = await WebForms.PostFormAsync(client, $"/accept-invitation?token={LinkToken}", "accept-existing", new Dictionary<string, string>());

        posted.Headers.Location!.ToString().ShouldEndWith("/accept-invitation?result=done");
        var call = api.AcceptCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}", "accepting is done as the signed-in person");
        call.Body.ShouldBe($$"""{"token":"{{LinkToken}}","password":null}""");
    }

    [Fact]
    public async Task SignedInAsTheWrongPersonIsExplainedAndSwitchingBringsThemBack()
    {
        var api = new FakeAuthApi { AcceptResponse = () => Problem(HttpStatusCode.Forbidden, "wrong_account") };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password");

        var posted = await WebForms.PostFormAsync(client, $"/accept-invitation?token={LinkToken}", "accept-existing", new Dictionary<string, string>());
        var html = await ReadAsync(posted);

        html.ShouldContain("This invitation is for a different account");
        html.ShouldContain("anna@acme.com");
        html.ShouldContain("Sign out and switch account");
        html.ShouldContain("""name="returnUrl" value="/accept-invitation" """.TrimEnd());

        var switched = await WebForms.PostLogoutFromHtmlAsync(client, html);
        switched.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        switched.Headers.Location!.ToString().ShouldEndWith("/login?returnUrl=%2Faccept-invitation");
    }

    [Fact]
    public async Task SomeoneSignedInWhoseInvitedEmailHasNoAccountYetIsAskedToChooseAPassword()
    {
        var api = new FakeAuthApi
        {
            AcceptResponse = () => Json(HttpStatusCode.BadRequest, """{"status":400,"title":"x","errors":{"Password":["Password is required."]}}"""),
        };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password");

        var posted = await WebForms.PostFormAsync(client, $"/accept-invitation?token={LinkToken}", "accept-existing", new Dictionary<string, string>());
        var html = await ReadAsync(posted);

        html.ShouldContain("This invitation is for a new account. Choose a password to finish.");
        html.ShouldContain("""<label for="accept-password">Password</label>""");
    }

    [Fact]
    public async Task SigningOutNeverSendsSomeoneToAnotherSite()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password");
        var account = await client.GetStringAsync("/account", Token);
        var forged = account.Replace("""<form method="post" action="/logout">""", """<form method="post" action="/logout"><input type="hidden" name="returnUrl" value="//evil.example/x" />""");

        var response = await WebForms.PostLogoutFromHtmlAsync(client, forged);

        response.Headers.Location!.ToString().ShouldEndWith("/login");
    }

    [Fact]
    public async Task TheLinkTokenAndPasswordAreNeverLogged()
    {
        var logs = new CapturingLogs();
        var api = new FakeAuthApi { AcceptResponse = () => Problem(HttpStatusCode.Gone, "invitation_invalid") };
        await using var factory = new WebAppFactory(api, logs: logs);
        using var client = NewClient(factory);

        await client.GetAsync($"/accept-invitation?token={LinkToken}", Token);
        await AcceptNewAsync(client, Password, Password);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(LinkToken);
        everything.ShouldNotContain(Password);
    }

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> ReadAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Token);

    private static Task<HttpResponseMessage> AcceptNewAsync(HttpClient client, string password, string confirm) =>
        WebForms.PostFormAsync(
            client,
            $"/accept-invitation?token={LinkToken}",
            "accept-new",
            new Dictionary<string, string> { ["accept-password"] = password, ["accept-confirm"] = confirm });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, status == HttpStatusCode.OK ? "application/json" : "application/problem+json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) =>
        Json(status, $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""");
}
