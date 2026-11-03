using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's audit log (UI03_T4): readable entries, filters, and loading more. No real API is involved.</summary>
public sealed class ConsoleAuditPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Priya = Guid.Parse("a1b2c3d4-0000-4000-8000-0000000000aa");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/audit";

    [Fact]
    public async Task EntriesAreInPlainWordsWithWhoWhenAndWhereFrom()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Audit log · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Audit log</h1>""");
        html.ShouldContain("Role changed");
        html.ShouldContain("Product access given");
        html.ShouldContain("Subscription suspended");
        html.ShouldContain("Sign-in failed");
        html.ShouldContain("Thing happened", customMessage: "a code with no name reads as words, not as a code");
        Rows(html).ShouldNotContain("auth.role.updated");
        Rows(html).ShouldNotContain("auth.future.thing_happened");
        html.ShouldContain("2 Nov 2026, 09:15");
        html.ShouldContain("203.0.113.5");
        html.ShouldContain("Unknown", customMessage: "an entry with no address says so");
        html.ShouldContain("You");
        html.ShouldContain("A person (a1b2c3d4)");
        html.ShouldContain("Helpdesk");
        html.ShouldContain("Nobody identified");
        html.ShouldContain("Showing 5 entries. That's everything that matches.");
    }

    [Fact]
    public async Task APersonIsNamedWhenTheApiGivesTheirNameAndOnlyAnIdWhenItDoesNot()
    {
        var api = Api();
        api.AuditJson = api.AuditJson.Replace(
            $"\"actorId\":\"{Priya}\"",
            $"\"actorId\":\"{Priya}\",\"actorName\":\"Priya Shah\"");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?actor={Priya}", Token));

        Rows(html).ShouldContain("Priya Shah");
        Rows(html).ShouldNotContain("A person (a1b2c3d4)");
        html.ShouldContain("Only actions by Priya Shah");
        (await client.GetStringAsync(PagePath, Token)).ShouldContain("Priya Shah");
    }

    [Fact]
    public async Task NewestIsFirstAsTheApiReturnsThem()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        var order = new[] { "Role changed", "Product access given", "Subscription suspended", "Sign-in failed", "Thing happened" }
            .Select(text => html.IndexOf($"<span>{text}</span>", StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(index => index > 0);
        order.ShouldBe(order.OrderBy(index => index).ToArray());
    }

    [Fact]
    public async Task TheLogIsReadAsTheAdminForTheirOwnOrganisationOnly()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        var call = api.AuditCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Query.ShouldBe("?limit=50");
        call.Query.ShouldNotContain("tenant", Case.Insensitive, "the organisation comes from the token, never from the address");
    }

    [Fact]
    public async Task FiltersAreSentToTheApiAndKeptInTheForm()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?action=auth.role.updated&actor={Priya}&from=2026-10-01&to=2026-11-03", Token);

        var query = api.AuditCalls.ShouldHaveSingleItem().Query;
        query.ShouldContain("limit=50");
        query.ShouldContain("action=auth.role.updated");
        query.ShouldContain($"actorId={Priya}");
        query.ShouldContain("before=2026-11-04T00%3A00%3A00.0000000%2B00%3A00", customMessage: "up to and including a day means before the start of the next");
        html.ShouldContain("<option value=\"auth.role.updated\" selected");
        html.ShouldContain("name=\"from\" type=\"date\" class=\"input\" value=\"2026-10-01\"");
        html.ShouldContain("value=\"2026-11-03\"");
        html.ShouldContain($"<input type=\"hidden\" name=\"actor\" value=\"{Priya}\"");
        html.ShouldContain("Clear filters");
        html.ShouldContain("Only actions by A person (a1b2c3d4)");
    }

    [Fact]
    public async Task TheFilterOffersEveryEventByName()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        var select = Regex.Match(html, "<select[^>]*name=\"action\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Anything");
        select.ShouldContain("API key rotated");
        select.ShouldContain("Member disabled");
        select.ShouldContain("Invitation accepted");
        html.ShouldNotContain("Clear filters", customMessage: "nothing is filtered yet");
    }

    [Fact]
    public async Task EntriesBeforeTheStartDateAreLeftOutAndNothingMoreIsOffered()
    {
        var api = Api();
        api.AuditJson = api.AuditJson.Replace("\"next\":null", """
            "next":{"before":"2026-10-10T08:00:00Z","beforeId":"90000000-0000-4000-8000-000000000005"}
            """.Trim());
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?from=2026-11-01", Token));

        html.ShouldContain("Role changed");
        html.ShouldContain("Product access given");
        Rows(html).ShouldNotContain("Subscription suspended");
        Rows(html).ShouldNotContain("Sign-in failed");
        html.ShouldNotContain("Load more");
        html.ShouldContain("Showing 2 entries. That's everything that matches.");
    }

    [Fact]
    public async Task LoadMoreAsksForFiftyMoreUpToTheMostTheApiAllows()
    {
        var api = Api();
        api.AuditJson = api.AuditJson.Replace("\"next\":null", """
            "next":{"before":"2026-10-10T08:00:00Z","beforeId":"90000000-0000-4000-8000-000000000005"}
            """.Trim());
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var first = await client.GetStringAsync(PagePath, Token);
        var second = await client.GetStringAsync($"{PagePath}?take=150&action=auth.role.updated", Token);
        var last = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?take=200", Token));
        var odd = await client.GetStringAsync($"{PagePath}?take=999", Token);

        first.ShouldContain($"href=\"{PagePath}?take=100\"");
        first.ShouldNotContain("That&#x27;s everything");
        second.ShouldContain($"href=\"{PagePath}?action=auth.role.updated&amp;take=200\"");
        last.ShouldNotContain("Load more");
        last.ShouldContain("This is the most that can be shown at once.");
        api.AuditCalls.Select(call => call.Query).ShouldBe(["?limit=50", "?limit=150&action=auth.role.updated", "?limit=200", "?limit=50"]);
        odd.ShouldContain($"href=\"{PagePath}?take=100\"");
    }

    [Fact]
    public async Task AnotherPersonsActionsCanBeSingledOutButYoursAndProductsCannot()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        Regex.Matches(html, "Only this person").Count.ShouldBe(1);
        html.ShouldContain($"href=\"{PagePath}?actor={Priya}\"");
    }

    [Fact]
    public async Task StartingAfterTheEndIsExplainedAndTheApiIsNotAsked()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?from=2026-11-03&to=2026-10-01", Token);

        html.ShouldContain("Check the dates");
        html.ShouldContain("The start date is after the end date.");
        api.AuditCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task DatesThatAreNotDatesAreIgnored()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync($"{PagePath}?from=yesterday&to=31/12/2026", Token);

        api.AuditCalls.ShouldHaveSingleItem().Query.ShouldBe("?limit=50");
    }

    [Fact]
    public async Task AnEmptyLogAndAnEmptySearchSayDifferentThings()
    {
        var api = Api();
        api.AuditJson = """{"items":[],"next":null}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var plain = await client.GetStringAsync(PagePath, Token);
        var filtered = await client.GetStringAsync($"{PagePath}?action=auth.role.deleted", Token);

        plain.ShouldContain("Nothing recorded yet");
        filtered.ShouldContain("No matching entries");
        filtered.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task WithoutPermissionToReadTheLogThePageSaysWhichPermissionIsNeeded()
    {
        var api = Api();
        api.ConsolePermissions = ["members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("You can&#x27;t see the audit log");
        html.ShouldContain("'Audit log · Read only' permission");
        api.AuditCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALogTheApiRefusesReadsAsAPermissionMessageAndAFailureAsACalmError()
    {
        var refusing = Api();
        refusing.AuditResponse = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var failing = Api();
        failing.AuditResponse = _ => throw new HttpRequestException("connection refused");
        await using var refusingFactory = new WebAppFactory(refusing);
        await using var failingFactory = new WebAppFactory(failing);
        using var refusingClient = await SignedInAsync(refusingFactory);
        using var failingClient = await SignedInAsync(failingFactory);

        (await refusingClient.GetStringAsync(PagePath, Token)).ShouldContain("You can&#x27;t see the audit log");
        var failed = await failingClient.GetStringAsync(PagePath, Token);
        failed.ShouldContain("We couldn&#x27;t load the audit log");
        failed.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ThePageIsInTheNavigationForAnyoneWhoMayReadIt()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain($"href=\"/console/{Acme}/audit\"");
    }

    /// <summary>Just the entries of the list: the filter above it names every event too.</summary>
    private static string Rows(string html) => Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value;

    private static FakeAuthApi Api()
    {
        var api = new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = "[]",
            SubscriptionsJson = $$"""[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":5,"seatsUsed":1}]""",
        };
        api.AuditJson = $$"""
            {"items":[
              {"id":"90000000-0000-4000-8000-000000000001","occurredAt":"2026-11-02T09:15:00Z","action":"auth.role.updated","actorType":"User","actorId":"{{api.UserId}}","tenantId":"{{Acme}}","sourceIp":"203.0.113.5","detail":"5d1f0c52-0000-4000-8000-00000000abcd"},
              {"id":"90000000-0000-4000-8000-000000000002","occurredAt":"2026-11-01T16:40:00Z","action":"auth.membership.access_granted","actorType":"User","actorId":"{{Priya}}","tenantId":"{{Acme}}","sourceIp":"198.51.100.7","detail":null},
              {"id":"90000000-0000-4000-8000-000000000003","occurredAt":"2026-10-28T08:00:00Z","action":"auth.subscription.suspended","actorType":"Product","actorId":"{{Helpdesk}}","tenantId":"{{Acme}}","sourceIp":null,"detail":"helpdesk"},
              {"id":"90000000-0000-4000-8000-000000000004","occurredAt":"2026-10-20T08:00:00Z","action":"auth.login.failed","actorType":"Anonymous","actorId":null,"tenantId":"{{Acme}}","sourceIp":"192.0.2.44","detail":null},
              {"id":"90000000-0000-4000-8000-000000000005","occurredAt":"2026-10-10T08:00:00Z","action":"auth.future.thing_happened","actorType":"System","actorId":null,"tenantId":"{{Acme}}","sourceIp":"192.0.2.45","detail":null}
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
