using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The sign-in page (UI-01): what it shows, and one wording for each way the API can answer. No real API is involved.</summary>
public sealed partial class LoginPageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheLoginPageHasALabelledFormInThePublicShell()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login", Token);

        html.ShouldContain("<title>Sign in · Auth Service</title>");
        html.ShouldContain("""<h1 class="auth-title">Sign in</h1>""");
        html.ShouldContain("""<label for="login-email">Email</label>""");
        html.ShouldContain("""<label for="login-password">Password</label>""");
        html.ShouldContain("""href="/forgot-password" """.TrimEnd());
        html.ShouldContain("""href="/signup" """.TrimEnd());
        html.ShouldContain("shell-auth", customMessage: "it uses the public shell");
        Regex.Matches(html, "<h1[ >]").Count.ShouldBe(1, "each page owns exactly one h1");
        html.ShouldNotContain("alert-error", customMessage: "a fresh page shows no error");
    }

    [Fact]
    public async Task ThePasswordToggleStartsHiddenSoThePageWorksWithoutScripts()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/login", Token);

        Regex.IsMatch(html, """<button[^>]*\bhidden\b[^>]*data-toggle-password="login-password""").ShouldBeTrue();
        html.ShouldContain("""aria-label="Show password" """.TrimEnd());
    }

    [Fact]
    public async Task SubmittingNothingNamesWhatToFixAndNeverCallsTheApi()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.OK, "{}");
        await using var factory = new WebAppFactory(api);

        var (_, html) = await PostLoginAsync(factory, email: "", password: "");

        html.ShouldContain("Fix 2 problems to sign in");
        html.ShouldContain("Enter your email address.");
        html.ShouldContain("Enter your password.");
        html.ShouldContain("""aria-invalid="true" """.TrimEnd());
        html.ShouldContain("""aria-describedby="login-email-message" """.TrimEnd());
        api.Requests.ShouldBeEmpty("a form with missing fields is not sent");
    }

    [Fact]
    public async Task AnEmailWithoutAnAtSignIsRefusedBeforeTheApiIsAsked()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.OK, "{}");
        await using var factory = new WebAppFactory(api);

        var (_, html) = await PostLoginAsync(factory, email: "anna", password: "a-long-password");

        html.ShouldContain("Fix 1 problem to sign in");
        html.ShouldContain("Enter a valid email address, like anna@acme.com.");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task WrongCredentialsShowOneMessageKeepTheEmailAndNeverEchoThePassword()
    {
        var api = Problem(HttpStatusCode.Unauthorized, "invalid_credentials");
        await using var factory = new WebAppFactory(api);

        var (response, html) = await PostLoginAsync(factory, "anna@acme.com", "not-the-password");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("The email or password is incorrect");
        html.ShouldContain("""href="/forgot-password">reset it</a>""");
        html.ShouldContain("anna@acme.com");
        html.ShouldNotContain("not-the-password");
        html.ShouldContain("""role="alert" """.TrimEnd());
    }

    [Fact]
    public async Task ASuccessfulSignInSendsTheEmailAndPasswordToTheApiAndLeavesTheLoginPage()
    {
        // The request is disposed once it is sent, so the body is read while the fake API is answering.
        string? sentBody = null;
        var api = new FakeApiHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"accessToken":"secret-access","tokenType":"Bearer","expiresIn":900,"refreshToken":"secret-refresh","memberships":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        await using var factory = new WebAppFactory(api);

        var (response, html) = await PostLoginAsync(factory, "  anna@acme.com ", "a-long-password");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.AbsolutePath.ShouldBe("/");
        html.ShouldNotContain("secret-access");
        html.ShouldNotContain("secret-refresh");

        var request = api.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.AbsolutePath.ShouldBe("/api/auth/login");
        sentBody.ShouldNotBeNull();
        sentBody.ShouldContain("\"email\":\"anna@acme.com\"", customMessage: "the email is trimmed");
        sentBody.ShouldContain("\"password\":\"a-long-password\"");
    }

    [Fact]
    public async Task APersonWhoHasNotVerifiedTheirEmailIsToldToDoThatFirst()
    {
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Forbidden, "email_not_verified"));

        var (_, html) = await PostLoginAsync(factory, "sam@acme.com", "a-long-password");

        html.ShouldContain("Verify your email first");
        html.ShouldContain("We sent a link to sam@acme.com when you signed up.");
    }

    [Fact]
    public async Task ADisabledAccountIsToldToContactSupport()
    {
        await using var factory = new WebAppFactory(Problem(HttpStatusCode.Forbidden, "account_disabled"));

        var (_, html) = await PostLoginAsync(factory, "lena@acme.com", "a-long-password");

        html.ShouldContain("This account is turned off");
        html.ShouldContain("contact Auth Service support");
    }

    [Fact]
    public async Task ARateLimitSaysHowLongToWaitAndHandsTheCountdownToTheButton()
    {
        var api = new FakeApiHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"status":429,"title":"Too many requests","detail":"Try again later.","code":"rate_limited"}""", Encoding.UTF8, "application/problem+json"),
            };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });
        await using var factory = new WebAppFactory(api);

        var (_, html) = await PostLoginAsync(factory, "anna@acme.com", "a-long-password");

        html.ShouldContain("Too many attempts");
        html.ShouldContain("Wait 42 seconds, then try again.");
        html.ShouldContain("""data-countdown="42" """.TrimEnd());
        html.ShouldContain("""id="login-announce" """.TrimEnd());
        Regex.IsMatch(html, "<button[^>]*\bdisabled\b").ShouldBeFalse("the button is only disabled by the script, so the page still works without it");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedGivesACalmMessageNotAnError()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("connection refused")));

        var (response, html) = await PostLoginAsync(factory, "anna@acme.com", "a-long-password");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("We couldn&#x27;t sign you in");
        html.ShouldContain("Your details are safe");
        html.ShouldNotContain("connection refused");
        html.ShouldNotContain("HttpRequestException");
    }

    private static FakeApiHandler Problem(HttpStatusCode status, string code) =>
        FakeApiHandler.Json(status, $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""");

    /// <summary>Loads the page for its anti-forgery token, then posts the form the way a browser would.</summary>
    private static async Task<(HttpResponseMessage Response, string Html)> PostLoginAsync(WebAppFactory factory, string email, string password)
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var page = await client.GetStringAsync("/login", Token);

        // Field names come from the rendered page, as a browser would send them, so a naming mismatch cannot hide.
        var form = new Dictionary<string, string>();
        foreach (Match input in InputTag().Matches(page))
        {
            var tag = input.Value;
            var fieldName = WebUtility.HtmlDecode(Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value);
            if (tag.Contains("id=\"login-email\""))
            {
                form[fieldName] = email;
                continue;
            }

            if (tag.Contains("id=\"login-password\""))
            {
                form[fieldName] = password;
                continue;
            }

            if (!tag.Contains("type=\"hidden\""))
            {
                continue;
            }

            var name = Regex.Match(tag, "name=\"([^\"]+)\"").Groups[1].Value;
            var value = Regex.Match(tag, "value=\"([^\"]*)\"").Groups[1].Value;
            form[WebUtility.HtmlDecode(name)] = WebUtility.HtmlDecode(value);
        }

        var response = await client.PostAsync("/login", new FormUrlEncodedContent(form), Token);
        return (response, await response.Content.ReadAsStringAsync(Token));
    }

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex InputTag();
}
