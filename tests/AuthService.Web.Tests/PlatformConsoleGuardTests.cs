using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's guard (UI04_T7): only people the API accepts as platform admins get in, and everyone else gets a plain page with no admin data. No real API is involved.</summary>
public sealed class PlatformConsoleGuardTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task APlatformAdminGetsInAndTheHeaderAlwaysSaysPlatformAdmin()
    {
        var api = Api(admin: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync("/platform/audit", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var header = Regex.Match(html, "<header class=\"console-header\">.*?</header>", RegexOptions.Singleline).Value;
        header.ShouldContain("Platform admin");
        html.ShouldContain("""<h1 class="page-title">Audit log</h1>""");
        html.ShouldContain("""href="/platform/audit" aria-current="page" """.TrimEnd());
        html.ShouldNotContain("You don't have access");
    }

    [Fact]
    public async Task TheConsoleIsForALargerScreen()
    {
        await using var factory = new WebAppFactory(Api(admin: true));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync("/platform/audit", Token);

        html.ShouldContain("Use a larger screen");
        html.ShouldContain("platform-small-notice");
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminGetsAPlainNotAllowedPageAndNoAdminData()
    {
        var api = Api(admin: false);
        api.AdminAuditJson = """{"items":[{"id":"90000000-0000-4000-8000-000000000001","occurredAt":"2026-11-02T09:15:00Z","action":"auth.signing_key.rotated","actorType":"User","actorId":null,"tenantId":null,"sourceIp":"203.0.113.5","detail":"secret-detail"}],"next":null}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync("/platform/audit?tenant=" + Acme, Token);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        html.ShouldContain("You don't have access to this page");
        html.ShouldContain("This part of Auth Service is for the people who run the platform.");
        html.ShouldNotContain("secret-detail");
        html.ShouldNotContain("Signing key rotated");
        html.ShouldNotContain("203.0.113.5");
        html.ShouldNotContain("""<h1 class="page-title">Audit log</h1>""");
        Regex.IsMatch(html, "<nav aria-label=\"Main\"").ShouldBeFalse("the navigation never offers the console to them");
        api.AdminCalls.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/api/auth/admin/webhooks?limit=1", "only the check was made, never the audit log");
    }

    [Fact]
    public async Task TheRootOfTheConsoleLeadsToTheFirstPartWhichIsGuardedForSomeoneWhoIsNotAnAdmin()
    {
        var api = Api(admin: false);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var root = await client.GetAsync("/platform", Token);
        var landing = await client.GetAsync(Where(root), Token);

        root.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(root).ShouldBe("/platform/audit");
        landing.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        WebUtility.HtmlDecode(await landing.Content.ReadAsStringAsync(Token)).ShouldContain("You don't have access to this page");
    }

    [Fact]
    public async Task APlatformAdminAtTheRootIsSentToTheFirstPartThatExists()
    {
        await using var factory = new WebAppFactory(Api(admin: true));
        using var client = await SignedInAsync(factory);

        var root = await client.GetAsync("/platform", Token);
        var landing = await client.GetAsync(Where(root), Token);

        root.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(root).ShouldBe("/platform/audit");
        landing.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheCheckIsMadeAsThePersonWithTheirOwnSignInTokenAndNothingFromTheBrowserCounts()
    {
        var api = Api(admin: false);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        client.DefaultRequestHeaders.Add("X-Platform-Admin", "true");
        client.DefaultRequestHeaders.Add("Cookie", "platform_admin=1; is_platform_admin=true");

        var response = await client.GetAsync("/platform/audit?admin=1&isPlatformAdmin=true", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var call = api.AdminCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain(api.AccessTokensIssued[0]);
    }

    [Fact]
    public async Task SomeoneSignedOutIsSentToSignIn()
    {
        await using var factory = new WebAppFactory(Api(admin: true));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/platform/audit", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldStartWith("/login");
    }

    [Fact]
    public async Task AnEndedSignInIsSentToSignInAgainNotShownAsNotAllowed()
    {
        var api = Api(admin: true);
        api.AdminResponse = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync("/platform/audit", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldStartWith("/login?returnUrl=");
    }

    [Fact]
    public async Task AnApiThatCannotAnswerIsACalmErrorNotAClaimThatThePersonIsNotAllowed()
    {
        var api = Api(admin: true);
        api.AdminResponse = _ => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync("/platform/audit", Token);

        html.ShouldContain("We couldn&#x27;t open the platform console");
        html.ShouldNotContain("You don't have access to this page");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task TheOrganisationsConsoleNeverLinksToThePlatformConsole()
    {
        await using var factory = new WebAppFactory(Api(admin: true));
        using var client = await SignedInAsync(factory);

        foreach (var page in new[] { "members", "roles", "api-keys", "subscriptions", "audit" })
        {
            (await client.GetStringAsync($"/console/{Acme}/{page}", Token)).ShouldNotContain("/platform", customMessage: page);
        }

        (await client.GetStringAsync("/choose-organisation?change=1", Token)).ShouldNotContain("/platform");
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api(bool admin) => new()
    {
        IsPlatformAdmin = admin,
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
