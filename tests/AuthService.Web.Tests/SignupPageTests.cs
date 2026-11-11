using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The sign-up page (UI01_T2): what it shows, and one wording for each answer of the API. No real API is involved.</summary>
public sealed class SignupPageTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Valid = new()
    {
        ["signup-name"] = "Anna Kowalski",
        ["signup-organisation"] = "Acme Ltd",
        ["signup-email"] = "anna@acme.com",
        ["signup-password"] = "a-long-password",
    };

    [Fact]
    public async Task TheSignupPageAsksForNameOrganisationEmailAndPasswordAndStatesThePasswordRule()
    {
        await using var factory = new WebAppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/signup", Token);

        html.ShouldContain("<title>Create your account · Auth Service</title>");
        html.ShouldContain("""<h1 class="auth-title">Create your account</h1>""");
        html.ShouldContain("""<label for="signup-name">Full name</label>""");
        html.ShouldContain("""<label for="signup-organisation">Organisation name</label>""");
        html.ShouldContain("""<label for="signup-email">Work email</label>""");
        html.ShouldContain("""<label for="signup-password">Password</label>""");
        html.ShouldContain("You can&#x27;t change it later.");
        html.ShouldContain("At least 8 characters");
        html.ShouldContain("""autocomplete="new-password" """.TrimEnd());
        html.ShouldContain("""aria-describedby="signup-rules" """.TrimEnd(), customMessage: "the password field is described by its rules");
        html.ShouldContain("""href="/login" """.TrimEnd());
        Regex.Matches(html, "<h1[ >]").Count.ShouldBe(1);
    }

    [Fact]
    public async Task SubmittingNothingNamesEveryProblemAndNeverCallsTheApi()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Created, "{}");
        await using var factory = new WebAppFactory(api);

        var html = await SignUpAsync(factory, new Dictionary<string, string>());

        html.ShouldContain("Fix 4 problems to create your account");
        html.ShouldContain("Enter your full name.");
        html.ShouldContain("Enter your organisation&#x27;s name.");
        html.ShouldContain("Enter your work email.");
        html.ShouldContain("Choose a password.");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATooShortPasswordIsRefusedBeforeTheApiIsAsked()
    {
        var api = FakeApiHandler.Json(HttpStatusCode.Created, "{}");
        await using var factory = new WebAppFactory(api);

        var html = await SignUpAsync(factory, With("signup-password", "short"));

        html.ShouldContain("Fix 1 problem to create your account");
        html.ShouldContain("Use between 8 and 128 characters.");
        html.ShouldNotContain("short</", customMessage: "the password is never echoed back");
        api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ASuccessfulSignupSendsTheDetailsToTheApiAndSaysToCheckEmailWithoutEchoingThePassword()
    {
        string? sentBody = null;
        var api = new FakeApiHandler(request =>
        {
            sentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11"}""", Encoding.UTF8, "application/json"),
            };
        });
        await using var factory = new WebAppFactory(api);

        var html = await SignUpAsync(factory, With("signup-email", "  anna@acme.com "));

        html.ShouldContain("Check your email");
        html.ShouldContain("If anna@acme.com can be used to create an account, we&#x27;ve sent a link to finish signing up. The link works for 24 hours.");
        html.ShouldContain("""data-countdown="60" """.TrimEnd(), customMessage: "resend waits a minute");
        html.ShouldNotContain("a-long-password");

        api.Requests.ShouldHaveSingleItem().RequestUri!.AbsolutePath.ShouldBe("/api/auth/signup");
        sentBody.ShouldNotBeNull();
        sentBody.ShouldContain("\"tenantName\":\"Acme Ltd\"");
        sentBody.ShouldContain("\"email\":\"anna@acme.com\"", customMessage: "the email is trimmed");
        sentBody.ShouldContain("\"displayName\":\"Anna Kowalski\"");
        sentBody.ShouldContain("\"password\":\"a-long-password\"");
    }

    [Fact]
    public async Task AnEmailThatAlreadyHasAnAccountGetsExactlyTheSameScreenAsANewOne()
    {
        await using var created = new WebAppFactory(FakeApiHandler.Json(HttpStatusCode.Created, "{}", "application/json"));
        await using var existing = new WebAppFactory(Problem(HttpStatusCode.Conflict, "email_already_registered"));

        var forNew = await SignUpAsync(created, Valid);
        var forExisting = await SignUpAsync(existing, Valid);

        StripTokens(forExisting).ShouldBe(StripTokens(forNew), "nothing on the page may reveal whether an address has an account");
        forExisting.ShouldNotContain("already");
    }

    [Fact]
    public async Task WhenTheApiRejectsAFieldItsReasonIsShownBesideThatField()
    {
        var api = FakeApiHandler.Json(
            HttpStatusCode.BadRequest,
            """{"status":400,"title":"x","errors":{"Email":["Email is not a valid email address."],"Password":["Password must be between 8 and 128 characters."]}}""");
        await using var factory = new WebAppFactory(api);

        var html = await SignUpAsync(factory, Valid);

        html.ShouldContain("Email is not a valid email address.");
        html.ShouldContain("Password must be between 8 and 128 characters.");
        html.ShouldNotContain("Check your email");
    }

    [Fact]
    public async Task ARateLimitSaysHowLongToWaitAndHandsTheCountdownToTheButton()
    {
        await using var factory = new WebAppFactory(RateLimited(42));

        var html = await SignUpAsync(factory, Valid);

        html.ShouldContain("Too many attempts");
        html.ShouldContain("Wait 42 seconds, then try again.");
        html.ShouldContain("""data-countdown="42" """.TrimEnd());
        Regex.IsMatch(html, "<button[^>]*\\bdisabled\\b").ShouldBeFalse("only the script disables the button, so the page works without it");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedGivesACalmMessage()
    {
        await using var factory = new WebAppFactory(FakeApiHandler.Throwing(new HttpRequestException("connection refused")));

        var html = await SignUpAsync(factory, Valid);

        html.ShouldContain("We couldn&#x27;t create your account");
        html.ShouldContain("Your details are safe");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ResendingAsksTheApiForANewLinkAndRestartsTheWait()
    {
        string? resentBody = null;
        var api = new FakeApiHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/resend"))
            {
                resentBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent("""{"message":"x"}""", Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        var sent = await ReadAsync(await WebForms.PostFormAsync(client, "/signup", "signup", Valid));

        var again = await ReadAsync(await WebForms.PostFormFromHtmlAsync(client, "/signup", sent, "resend"));

        again.ShouldContain("We&#x27;ve sent it again");
        again.ShouldContain("""data-countdown="60" """.TrimEnd());
        resentBody.ShouldNotBeNull();
        resentBody.ShouldContain("\"email\":\"anna@acme.com\"");
    }

    [Fact]
    public async Task ResendingTooOftenShowsTheWaitOnTheButton()
    {
        var api = new FakeApiHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/resend")
            ? RateLimitedResponse(30)
            : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);
        var sent = await ReadAsync(await WebForms.PostFormAsync(client, "/signup", "signup", Valid));

        var again = await ReadAsync(await WebForms.PostFormFromHtmlAsync(client, "/signup", sent, "resend"));

        again.ShouldContain("Too many requests");
        again.ShouldContain("Wait 30 seconds, then try again.");
        again.ShouldContain("""data-countdown="30" """.TrimEnd());
    }

    private static Dictionary<string, string> With(string id, string value) => new(Valid) { [id] = value };

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> ReadAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Token);

    private static async Task<string> SignUpAsync(WebAppFactory factory, Dictionary<string, string> fields)
    {
        using var client = NewClient(factory);
        return await ReadAsync(await WebForms.PostFormAsync(client, "/signup", "signup", fields));
    }

    /// <summary>Anti-forgery tokens and Blazor's encrypted state differ on every page; everything else must be identical.</summary>
    private static string StripTokens(string html) =>
        Regex.Replace(
            Regex.Replace(Regex.Replace(html, "value=\"[^\"]{40,}\"", "value=\"TOKEN\""), "nonce=\"[^\"]*\"", "nonce=\"NONCE\""),
            "<!--Blazor-Server-Component-State:[^>]*-->",
            "<!--STATE-->");

    private static FakeApiHandler Problem(HttpStatusCode status, string code) =>
        FakeApiHandler.Json(status, $$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""");

    private static FakeApiHandler RateLimited(int seconds) => new(_ => RateLimitedResponse(seconds));

    private static HttpResponseMessage RateLimitedResponse(int seconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"status":429,"title":"x","detail":"x","code":"rate_limited"}""", Encoding.UTF8, "application/problem+json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        return response;
    }
}
