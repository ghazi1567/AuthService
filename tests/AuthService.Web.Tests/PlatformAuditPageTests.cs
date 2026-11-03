using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's audit log (UI04_T6): everything across every organisation, filtered and paged. No real API is involved.</summary>
public sealed class PlatformAuditPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Priya = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000aa");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private const string PagePath = "/platform/audit";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EntriesAreInPlainWordsWithWhoWhenWhichOrganisationAndWhereFrom()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Platform audit log · Auth Service</title>");
        html.ShouldContain("Signing key rotated");
        html.ShouldContain("Role changed");
        html.ShouldContain("Product registered");
        Rows(html).ShouldNotContain("auth.signing_key.rotated");
        html.ShouldContain("2 Nov 2026, 09:15");
        html.ShouldContain("You");
        html.ShouldContain("A person (a1b2c3d4)");
        html.ShouldContain("A product (20000000)");
        html.ShouldContain("7d8e6b92");
        html.ShouldContain("None", customMessage: "an entry for no organisation says so");
        html.ShouldContain("203.0.113.5");
        html.ShouldContain("helpdesk", customMessage: "the entry's detail is shown");
        html.ShouldContain("Showing 4 entries. That's everything that matches.");
    }

    [Fact]
    public async Task PeopleAndProductsAreNamedWhenTheApiGivesTheirNames()
    {
        var api = Api();
        api.AdminAuditJson = api.AdminAuditJson
            .Replace($"\"actorId\":\"{Priya}\"", $"\"actorId\":\"{Priya}\",\"actorName\":\"Priya Shah\"")
            .Replace($"\"actorId\":\"{Helpdesk}\"", $"\"actorId\":\"{Helpdesk}\",\"actorName\":\"Helpdesk\"");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        Rows(html).ShouldContain("Priya Shah");
        Rows(html).ShouldContain("Helpdesk");
        Rows(html).ShouldNotContain("A person (a1b2c3d4)");
        Rows(html).ShouldNotContain("A product (20000000)");
    }

    [Fact]
    public async Task TheLogIsReadWithThePersonsOwnSignInToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        api.AdminCalls.Count.ShouldBe(2, "the check and the log");
        var call = api.AdminCalls[1];
        call.PathAndQuery.ShouldBe("/api/auth/admin/audit?limit=50");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
    }

    [Fact]
    public async Task FiltersAreSentToTheApiAndKeptInTheForm()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?action=auth.signing_key.rotated&tenant={Acme}&actor={Priya}&from=2026-10-01&to=2026-11-03", Token);

        var query = api.AdminCalls[1].PathAndQuery;
        query.ShouldContain("action=auth.signing_key.rotated");
        query.ShouldContain($"tenantId={Acme}");
        query.ShouldContain($"actorId={Priya}");
        query.ShouldContain("before=2026-11-04T00%3A00%3A00.0000000%2B00%3A00");
        html.ShouldContain("<option value=\"auth.signing_key.rotated\" selected");
        html.ShouldContain($"value=\"{Acme}\"");
        html.ShouldContain("value=\"2026-10-01\"");
        html.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task ThePlatformFilterOffersThePlatformEventsToo()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        var select = Regex.Match(html, "<select[^>]*name=\"action\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Signing key rotated");
        select.ShouldContain("Product secret replaced");
        select.ShouldContain("Webhook sent again");
        select.ShouldContain("Platform audit log viewed");
        select.ShouldContain("Member disabled");
    }

    [Fact]
    public async Task AnOrganisationIdThatIsNotOneIsExplainedAndTheApiIsNotAskedForTheLog()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?tenant=acme", Token));

        html.ShouldContain("Check the organisation id");
        html.ShouldContain("An organisation id looks like");
        api.AdminCalls.ShouldHaveSingleItem().PathAndQuery.ShouldBe("/api/auth/admin/webhooks?limit=1", "only the check was made");
    }

    [Fact]
    public async Task StartingAfterTheEndIsExplainedAndTheApiIsNotAskedForTheLog()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?from=2026-11-03&to=2026-10-01", Token);

        html.ShouldContain("The start date is after the end date.");
        api.AdminCalls.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnotherPersonOrOrganisationCanBeSingledOutFromAnyEntry()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"{PagePath}?actor={Priya}\"");
        html.ShouldContain($"href=\"{PagePath}?tenant={Acme}\"");
        Regex.Matches(html, "Only this person").Count.ShouldBe(1);
    }

    [Fact]
    public async Task LoadMoreAsksForFiftyMoreUpToTheMostTheApiAllows()
    {
        var api = Api();
        api.AdminAuditJson = api.AdminAuditJson.Replace("\"next\":null", """
            "next":{"before":"2026-10-10T08:00:00Z","beforeId":"90000000-0000-4000-8000-000000000005"}
            """.Trim());
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var first = await client.GetStringAsync(PagePath, Token);
        var last = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?take=200", Token));

        first.ShouldContain($"href=\"{PagePath}?take=100\"");
        last.ShouldNotContain("Load more");
        last.ShouldContain("This is the most that can be shown at once.");
        api.AdminCalls.Where(call => call.PathAndQuery.Contains("audit")).Select(call => call.PathAndQuery)
            .ShouldBe(["/api/auth/admin/audit?limit=50", "/api/auth/admin/audit?limit=200"]);
    }

    [Fact]
    public async Task AnEmptyLogAndAnEmptySearchSayDifferentThings()
    {
        var api = Api();
        api.AdminAuditJson = """{"items":[],"next":null}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("Nothing recorded yet");
        var filtered = await client.GetStringAsync($"{PagePath}?action=auth.role.deleted", Token);
        filtered.ShouldContain("No matching entries");
        filtered.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task ALogTheApiCannotReadIsACalmErrorAndAnEndedSignInGoesToSignIn()
    {
        var failing = Api();
        failing.AdminResponse = path => path.Contains("/audit") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        var expired = Api();
        expired.AdminResponse = path => path.Contains("/audit") ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : null;
        await using var failingFactory = new WebAppFactory(failing);
        await using var expiredFactory = new WebAppFactory(expired);
        using var failingClient = await SignedInAsync(failingFactory);
        using var expiredClient = await SignedInAsync(expiredFactory);

        var failed = await failingClient.GetStringAsync(PagePath, Token);
        var gone = await expiredClient.GetAsync(PagePath, Token);

        failed.ShouldContain("We couldn&#x27;t load the audit log");
        failed.ShouldContain("Try again");
        gone.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        gone.Headers.Location!.OriginalString.ShouldContain("/login?returnUrl=");
    }

    /// <summary>Just the entries of the list: the filter above it names every event too.</summary>
    private static string Rows(string html) => Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value;

    private static FakeAuthApi Api()
    {
        var api = new FakeAuthApi
        {
            IsPlatformAdmin = true,
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = "[]",
        };
        api.AdminAuditJson = $$"""
            {"items":[
              {"id":"90000000-0000-4000-8000-000000000001","occurredAt":"2026-11-02T09:15:00Z","action":"auth.signing_key.rotated","actorType":"User","actorId":"{{api.UserId}}","tenantId":null,"sourceIp":"203.0.113.5","detail":null},
              {"id":"90000000-0000-4000-8000-000000000002","occurredAt":"2026-11-01T16:40:00Z","action":"auth.role.updated","actorType":"User","actorId":"{{Priya}}","tenantId":"{{Acme}}","sourceIp":"198.51.100.7","detail":"5d1f0c52-0000-4000-8000-00000000abcd"},
              {"id":"90000000-0000-4000-8000-000000000003","occurredAt":"2026-10-28T08:00:00Z","action":"auth.application.registered","actorType":"Product","actorId":"{{Helpdesk}}","tenantId":null,"sourceIp":null,"detail":"helpdesk"},
              {"id":"90000000-0000-4000-8000-000000000004","occurredAt":"2026-10-20T08:00:00Z","action":"auth.login.failed","actorType":"Anonymous","actorId":null,"tenantId":"{{Acme}}","sourceIp":"192.0.2.44","detail":null}
            ],"next":null}
            """;
        return api;
    }

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
