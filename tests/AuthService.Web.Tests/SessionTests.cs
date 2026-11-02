using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>
/// The signed-in session (UI01_T1): the cookie holds no token, the tokens are renewed before they run out and only once at a time, a
/// session that has ended sends the person back to sign in, and sign-out ends it everywhere. The API is a fake that, like the real one,
/// lets each refresh token be used once.
/// </summary>
public sealed class SessionTests
{
    private const string Email = "anna@acme.com";
    private const string Password = "a-long-password";
    private static readonly TimeSpan AlmostExpired = TimeSpan.FromMinutes(14.5);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SigningInStartsASessionWhoseCookieHoldsNoToken()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api, time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);

        var response = await WebForms.PostLoginAsync(client, Email, Password);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var cookie = SessionCookie(response);
        cookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.ToLowerInvariant().ShouldContain("samesite=lax");
        cookie.ShouldNotContain(api.AccessTokensIssued.Single());
        cookie.ShouldNotContain(api.RefreshTokensIssued.Single());

        var account = await client.GetAsync("/account", Token);
        account.StatusCode.ShouldBe(HttpStatusCode.OK);
        var html = await account.Content.ReadAsStringAsync(Token);
        html.ShouldContain(Email);
        html.ShouldContain("action=\"/logout\"");
        html.ShouldNotContain(api.AccessTokensIssued.Single());
        html.ShouldNotContain(api.RefreshTokensIssued.Single());
    }

    [Fact]
    public async Task TheCookieEndsWithTheBrowserUnlessThePersonKeepsSigningIn()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi(), time: new FakeClock(DateTimeOffset.UtcNow));
        using var forThisVisit = NewClient(factory);
        using var forAWhile = NewClient(factory);

        var visit = await WebForms.PostLoginAsync(forThisVisit, Email, Password, remember: false);
        var kept = await WebForms.PostLoginAsync(forAWhile, Email, Password, remember: true);

        SessionCookie(visit).ToLowerInvariant().ShouldNotContain("expires=", customMessage: "a session cookie has no end date");
        SessionCookie(kept).ToLowerInvariant().ShouldContain("expires=");
    }

    [Fact]
    public async Task TheCookieIsOnlyEverSentOverHttpsOutsideDevelopment()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi(), time: new FakeClock(DateTimeOffset.UtcNow), environment: "Production");
        using var client = NewClient(factory);

        var response = await WebForms.PostLoginAsync(client, Email, Password);

        SessionCookie(response).ToLowerInvariant().ShouldContain("secure");
    }

    [Fact]
    public async Task APageThatNeedsSigningInSendsAnonymousVisitorsToTheLoginPageAndBackAfterwards()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi(), time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);

        var redirect = await client.GetAsync("/account", Token);

        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(redirect).ShouldBe("/login?returnUrl=%2Faccount", "someone who was never signed in is not told a session ended");

        var signedIn = await WebForms.PostLoginAsync(client, Email, Password, path: "/login?returnUrl=%2Faccount");
        Where(signedIn).ShouldBe("/account");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/path")]
    [InlineData("/\\evil.example")]
    [InlineData("account")]
    [InlineData("javascript:alert(1)")]
    public async Task OnlyAPageOnThisSiteIsEverGoneBackTo(string returnUrl)
    {
        await using var factory = new WebAppFactory(new FakeAuthApi(), time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);

        var response = await WebForms.PostLoginAsync(client, Email, Password, path: "/login?returnUrl=" + Uri.EscapeDataString(returnUrl));

        Where(response).ShouldBe("/");
    }

    [Fact]
    public async Task SomeoneAlreadySignedInIsSentOnFromTheLoginPage()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi(), time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);

        var response = await client.GetAsync("/login?returnUrl=%2Faccount", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe("/account");
    }

    [Fact]
    public async Task TheAccessTokenIsRenewedShortlyBeforeItRunsOutAndEachRefreshTokenIsUsedOnce()
    {
        var api = new FakeAuthApi();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);

        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.RefreshTokensSent.ShouldBeEmpty("a fresh token is not renewed");

        clock.Advance(AlmostExpired);
        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.RefreshTokensSent.ShouldBe(["refresh-token-1"]);

        clock.Advance(AlmostExpired);
        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        api.RefreshTokensSent.ShouldBe(["refresh-token-1", "refresh-token-2"], "the new refresh token, never the spent one");
    }

    [Fact]
    public async Task ManyRequestsAtOnceRenewTheTokensOnlyOnce()
    {
        var api = new FakeAuthApi { RefreshDelay = TimeSpan.FromMilliseconds(200) };
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);
        clock.Advance(AlmostExpired);

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync("/account", Token)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK, "a reused refresh token would have ended the session");
        api.RefreshTokensSent.ShouldBe(["refresh-token-1"]);
    }

    [Fact]
    public async Task WhenTheApiWillNotRenewTheSessionThePersonIsSentToSignInAgain()
    {
        var api = new FakeAuthApi();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);
        api.RefusesRefresh = true;
        clock.Advance(AlmostExpired);

        var response = await client.GetAsync("/account", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe("/login?returnUrl=%2Faccount&reason=expired");
        response.Headers.GetValues("Set-Cookie").ShouldContain(cookie => cookie.StartsWith("auth_session=;"), "the cookie is cleared");

        var again = await client.GetAsync("/account", Token);
        Where(again).ShouldBe("/login?returnUrl=%2Faccount", "the notice is shown once");

        var loginPage = await client.GetStringAsync("/login?reason=expired", Token);
        loginPage.ShouldContain("Your session ended");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedForARenewalDoesNotSignThePersonOut()
    {
        var api = new FakeAuthApi();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);
        clock.Advance(AlmostExpired);

        api.Unreachable = true;
        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);

        api.Unreachable = false;
        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
        // The first attempt never reached the API, so its token is still unspent and the retry works.
        api.RefreshTokensSent.ShouldBe(["refresh-token-1"]);
        api.AccessTokensIssued.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ASessionEndsWhenItsLifeRunsOutButAKeptOneLastsLonger()
    {
        var api = new FakeAuthApi();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock);
        using var forThisVisit = NewClient(factory);
        using var forAWhile = NewClient(factory);
        await WebForms.PostLoginAsync(forThisVisit, Email, Password, remember: false);
        await WebForms.PostLoginAsync(forAWhile, Email, Password, remember: true);

        clock.Advance(TimeSpan.FromHours(13));

        var visit = await forThisVisit.GetAsync("/account", Token);
        visit.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(visit).ShouldContain("reason=expired");

        // The kept session survives a long idle gap: its tokens are simply renewed (the fake API accepts the newest refresh token).
        api.RefusesRefresh = false;
        var kept = await forAWhile.GetAsync("/account", Token);
        kept.StatusCode.ShouldBe(HttpStatusCode.OK);

        clock.Advance(TimeSpan.FromDays(31));
        var gone = await forAWhile.GetAsync("/account", Token);
        gone.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(gone).ShouldContain("reason=expired");
    }

    [Fact]
    public async Task SigningOutEndsTheSessionHereAndAtTheApiSoACopyOfTheCookieNoLongerWorks()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api, time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);
        var login = await WebForms.PostLoginAsync(client, Email, Password);
        var stolen = SessionCookie(login).Split(';')[0];

        var response = await WebForms.PostLogoutAsync(client);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe("/login");
        api.LogoutTokensSent.ShouldBe(["refresh-token-1"]);
        Where(await client.GetAsync("/account", Token)).ShouldBe("/login?returnUrl=%2Faccount");

        using var replay = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        replay.DefaultRequestHeaders.Add("Cookie", stolen);
        var replayed = await replay.GetAsync("/account", Token);
        replayed.StatusCode.ShouldBe(HttpStatusCode.Redirect, "the session is gone on the server, whatever the cookie says");
    }

    [Fact]
    public async Task SigningOutNeedsTheFormsAntiForgeryToken()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api, time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);

        var response = await WebForms.PostLogoutAsync(client, withToken: false);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.LogoutTokensSent.ShouldBeEmpty("another site cannot sign someone out");
        (await client.GetAsync("/account", Token)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SigningOutStillWorksWhenTheApiCannotBeTold()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api, time: new FakeClock(DateTimeOffset.UtcNow));
        using var client = NewClient(factory);
        await WebForms.PostLoginAsync(client, Email, Password);
        api.Unreachable = true;

        var response = await WebForms.PostLogoutAsync(client);

        Where(response).ShouldBe("/login");
        Where(await client.GetAsync("/account", Token)).ShouldBe("/login?returnUrl=%2Faccount");
    }

    [Fact]
    public async Task NoPasswordTokenOrSessionIdIsEverLogged()
    {
        const string password = "my-private-password-1";
        var logs = new CapturingLogs();
        var api = new FakeAuthApi();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        await using var factory = new WebAppFactory(api, time: clock, logs: logs);
        using var client = NewClient(factory);

        var login = await WebForms.PostLoginAsync(client, Email, password);
        var sessionId = SessionCookie(login).Split(';')[0].Split('=', 2)[1];
        clock.Advance(AlmostExpired);
        await client.GetAsync("/account", Token);
        api.Unreachable = true;
        await WebForms.PostLogoutAsync(client);
        api.Unreachable = false;
        await WebForms.PostLoginAsync(client, Email, password);
        api.RefusesRefresh = true;
        clock.Advance(AlmostExpired);
        await client.GetAsync("/account", Token);

        logs.Lines.ShouldNotBeEmpty("the flows above log something");
        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(password);
        everything.ShouldNotContain(sessionId);
        foreach (var secret in api.AccessTokensIssued.Concat(api.RefreshTokensIssued))
        {
            everything.ShouldNotContain(secret);
        }
    }

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string SessionCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("auth_session=") && !cookie.StartsWith("auth_session=;"));

    /// <summary>The path and query a redirect points to.</summary>
    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
