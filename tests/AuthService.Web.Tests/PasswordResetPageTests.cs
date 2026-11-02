using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The forgot-password and reset-password pages (UI01_T5). No real API is involved.</summary>
public sealed class PasswordResetPageTests
{
    private const string LinkToken = "rst_4b8d1f6a93c7e205";
    private const string NewPassword = "a-brand-new-password";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- /forgot-password

    [Fact]
    public async Task TheForgotPageAsksOnlyForAnEmail()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/forgot-password", Token);

        html.ShouldContain("<title>Reset your password · Auth Service</title>");
        html.ShouldContain("""<h1 class="auth-title">Reset your password</h1>""");
        html.ShouldContain("""<label for="forgot-email">Email</label>""");
        html.ShouldContain("""href="/login">Back to sign in</a>""");
    }

    [Fact]
    public async Task AMissingOrBadEmailIsRefusedBeforeTheApiIsAsked()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Accepted, "{}", "application/json");
        await using var factory = new WebAppFactory(api);

        var empty = await ForgotAsync(factory, "");
        var bad = await ForgotAsync(factory, "anna");

        empty.ShouldContain("Enter your email address.");
        bad.ShouldContain("Enter a valid email address, like anna@acme.com.");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AskingForALinkSendsTheEmailAndSaysToCheckEmailWithAWaitBeforeResending()
    {
        string? body = null;
        var api = new FakeApiHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Accepted("If that email belongs to an account, a password reset link has been sent.");
        });
        await using var factory = new WebAppFactory(api);

        var html = await ForgotAsync(factory, " anna@acme.com ");

        html.ShouldContain("Check your email");
        html.ShouldContain("If an account exists for anna@acme.com, we&#x27;ve sent a link to reset your password. The link works for 1 hour.");
        html.ShouldContain("""data-countdown="60" """.TrimEnd());
        body.ShouldBe("""{"email":"anna@acme.com"}""");
        api.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldBe("/api/auth/password/forgot");
    }

    [Fact]
    public async Task TheConfirmationIsTheSameForAnyAnswerTheApiGives()
    {
        await using var known = new WebAppFactory(new FakeApiHandler(_ => Accepted("If that email belongs to an account, a password reset link has been sent.")));
        await using var unknown = new WebAppFactory(new FakeApiHandler(_ => Accepted("something else entirely")));

        var forKnown = await ForgotAsync(known, "anna@acme.com");
        var forUnknown = await ForgotAsync(unknown, "anna@acme.com");

        Normalise(forUnknown).ShouldBe(Normalise(forKnown), "nothing on the page may reveal whether an address has an account");
    }

    [Fact]
    public async Task AForgotRateLimitShowsTheWaitOnTheButton()
    {
        await using var factory = new WebAppFactory(new FakeApiHandler(_ => RateLimited(42)));

        var html = await ForgotAsync(factory, "anna@acme.com");

        html.ShouldContain("Too many attempts");
        html.ShouldContain("Wait 42 seconds, then try again.");
        html.ShouldContain("""data-countdown="42" """.TrimEnd());
        Regex.IsMatch(html, "<button[^>]*\\bdisabled\\b").ShouldBeFalse("only the script disables the button");
    }

    [Fact]
    public async Task AForgotApiThatCannotBeReachedGivesACalmMessage()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("connection refused")));

        var html = await ForgotAsync(factory, "anna@acme.com");

        html.ShouldContain("We couldn&#x27;t send the link");
        html.ShouldContain("Your details are safe");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ResendingFromTheConfirmationAsksAgainAndSaysSo()
    {
        var api = new FakeApiHandler(_ => Accepted("x"));
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        var first = await ReadAsync(await WebForms.PostFormAsync(client, "/forgot-password", "forgot", new Dictionary<string, string> { ["forgot-email"] = "anna@acme.com" }));

        var again = await ReadAsync(await WebForms.PostFormFromHtmlAsync(client, "/forgot-password", first, "again"));

        again.ShouldContain("We&#x27;ve sent it again");
        again.ShouldContain("""data-countdown="60" """.TrimEnd());
        api.Requests.Count.ShouldBe(2);
    }

    // ---- /reset-password

    [Fact]
    public async Task OpeningTheResetLinkShowsTheFormAndHoldsTheTokenOnlyInAFormField()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/reset-password?token={LinkToken}", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        html.ShouldContain("""<h1 class="auth-title">Choose a new password</h1>""");
        html.ShouldContain("You'll be signed out on your other devices.");
        html.ShouldContain("""<label for="reset-password">New password</label>""");
        html.ShouldContain("""<label for="reset-confirm">Confirm new password</label>""");
        html.ShouldContain("At least 8 characters");
        html.ShouldContain("""action="/reset-password" """.TrimEnd(), customMessage: "the form posts to a clean address");
        html.ShouldContain("""data-link-form="clean" """.TrimEnd(), customMessage: "the script takes the token out of the address bar");
        Regex.Matches(html, Regex.Escape(LinkToken)).Count.ShouldBe(1, "the token is in the hidden field and nowhere else");
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.GetValues("Cache-Control").ShouldContain("no-store");
    }

    [Fact]
    public async Task OpeningThePageWithoutALinkOffersANewOne()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/reset-password", Token);

        html.ShouldContain("This link can't be used");
        html.ShouldContain("""href="/forgot-password">Send a new link</a>""");
    }

    [Fact]
    public async Task PasswordsThatDoNotMatchOrAreTooShortAreRefusedBeforeTheApiIsAsked()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.OK, """{"reset":true}""", "application/json");
        await using var factory = new WebAppFactory(api);

        var mismatch = await ResetAsync(factory, NewPassword, "something-different");
        var tooShort = await ResetAsync(factory, "short", "short");

        mismatch.ShouldContain("The two passwords don&#x27;t match.");
        tooShort.ShouldContain("Use between 8 and 128 characters.");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ChangingThePasswordSendsTheLinkAndThePasswordAndLeadsOnToSignIn()
    {
        string? body = null;
        var api = new FakeApiHandler(request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"reset":true}""", Encoding.UTF8, "application/json") };
        });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await ResetPostAsync(client, NewPassword, NewPassword);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var next = posted.Headers.Location!.ToString();
        next.ShouldEndWith("/reset-password?result=done");
        next.ShouldNotContain(LinkToken);
        body.ShouldBe($$"""{"token":"{{LinkToken}}","newPassword":"{{NewPassword}}"}""");
        api.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldBe("/api/auth/password/reset");

        var html = await client.GetStringAsync("/reset-password?result=done", Token);
        html.ShouldContain("Your password is changed");
        html.ShouldContain("you've been signed out everywhere else");
        html.ShouldContain("""href="/login">Continue to sign in</a>""");
    }

    [Fact]
    public async Task AnExpiredOrUsedResetLinkSaysSoAndOffersANewOne()
    {
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Gone, "reset_link_invalid"));
        using var client = NewClient(factory);

        var posted = await ResetPostAsync(client, NewPassword, NewPassword);

        posted.Headers.Location!.ToString().ShouldEndWith("/reset-password?result=invalid");
        var html = await client.GetStringAsync("/reset-password?result=invalid", Token);
        html.ShouldContain("This link can't be used");
        html.ShouldContain("""href="/forgot-password">Send a new link</a>""");
    }

    [Fact]
    public async Task APasswordTheApiRefusesShowsItsReasonAndKeepsTheLinkForAnotherTry()
    {
        var api = FakeApiHandler.Json(
            HttpStatusCode.BadRequest,
            """{"status":400,"title":"x","errors":{"NewPassword":["Password must be between 8 and 128 characters."]}}""");
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var posted = await ResetPostAsync(client, NewPassword, NewPassword);
        var html = await posted.Content.ReadAsStringAsync(Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Password must be between 8 and 128 characters.");
        html.ShouldNotContain(NewPassword, customMessage: "a password is never echoed back");
        Regex.IsMatch(html, """<input[^>]*name="token"[^>]*value="rst_4b8d1f6a93c7e205""").ShouldBeTrue("the link is still good, so it stays on the page");
    }

    [Fact]
    public async Task AResetRateLimitKeepsTheLinkAndShowsTheWait()
    {
        await using var factory = new WebAppFactory(new FakeApiHandler(_ => RateLimited(30)));
        using var client = NewClient(factory);

        var html = await (await ResetPostAsync(client, NewPassword, NewPassword)).Content.ReadAsStringAsync(Token);

        html.ShouldContain("Too many attempts");
        html.ShouldContain("Wait 30 seconds, then try again.");
        html.ShouldContain("""data-countdown="30" """.TrimEnd());
        html.ShouldContain(LinkToken);
    }

    [Fact]
    public async Task AResetApiThatCannotBeReachedKeepsTheLinkAndGivesACalmMessage()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("connection refused")));
        using var client = NewClient(factory);

        var html = await (await ResetPostAsync(client, NewPassword, NewPassword)).Content.ReadAsStringAsync(Token);

        html.ShouldContain("We couldn&#x27;t change your password");
        html.ShouldContain("Your link is still good.");
        html.ShouldNotContain("connection refused");
        html.ShouldContain(LinkToken);
        html.ShouldNotContain(NewPassword);
    }

    [Fact]
    public async Task TheLinkTokenAndThePasswordAreNeverLogged()
    {
        var logs = new CapturingLogs();
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Gone, "reset_link_invalid"), logs: logs);
        using var client = NewClient(factory);

        await client.GetAsync($"/reset-password?token={LinkToken}", Token);
        await ResetPostAsync(client, NewPassword, NewPassword);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(LinkToken);
        everything.ShouldNotContain(NewPassword);
    }

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> ReadAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Token);

    private static async Task<string> ForgotAsync(WebAppFactory factory, string email)
    {
        using var client = NewClient(factory);
        return await ReadAsync(await WebForms.PostFormAsync(client, "/forgot-password", "forgot", new Dictionary<string, string> { ["forgot-email"] = email }));
    }

    private static Task<HttpResponseMessage> ResetPostAsync(HttpClient client, string password, string confirm) =>
        WebForms.PostFormAsync(
            client,
            $"/reset-password?token={LinkToken}",
            "reset",
            new Dictionary<string, string> { ["reset-password"] = password, ["reset-confirm"] = confirm });

    private static async Task<string> ResetAsync(WebAppFactory factory, string password, string confirm)
    {
        using var client = NewClient(factory);
        return await ReadAsync(await ResetPostAsync(client, password, confirm));
    }

    /// <summary>Anti-forgery tokens and Blazor's encrypted state differ on every page; everything else must be identical.</summary>
    private static string Normalise(string html) =>
        Regex.Replace(
            Regex.Replace(html, "value=\"[^\"]{40,}\"", "value=\"TOKEN\""),
            "<!--Blazor-Server-Component-State:[^>]*-->",
            "<!--STATE-->");

    private static HttpResponseMessage Accepted(string message) =>
        new(HttpStatusCode.Accepted) { Content = new StringContent($$"""{"message":"{{message}}"}""", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage RateLimited(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"status":429,"title":"x","detail":"x","code":"rate_limited"}""", Encoding.UTF8, "application/problem+json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }

    private static FakeApiHandler Problem(HttpStatusCode status, string code) =>
        FakeApiHandler.Json(status, $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""");
}
