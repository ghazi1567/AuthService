using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's webhook events (UI04_T4): list, filter and send a failed one again. No real API is involved.</summary>
public sealed class PlatformWebhooksPageTests
{
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid FailedSix = Guid.Parse("60000000-0000-4000-8000-000000000001");
    private static readonly Guid Pending = Guid.Parse("60000000-0000-4000-8000-000000000002");
    private static readonly Guid Delivered = Guid.Parse("60000000-0000-4000-8000-000000000003");
    private static readonly Guid FailedOne = Guid.Parse("60000000-0000-4000-8000-000000000004");
    private const string PagePath = "/platform/webhooks";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EventsAreInPlainWordsWithStatusAttemptsAndWhatHappenedNext()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Webhook events · Auth Service</title>");
        html.ShouldContain("An organisation subscribed");
        html.ShouldContain("An organisation unsubscribed");
        Rows(html).ShouldNotContain("ApplicationSubscribed");
        html.ShouldContain("helpdesk");
        html.ShouldContain("billing");
        html.ShouldContain("badge badge-error");
        html.ShouldContain("badge badge-success");
        html.ShouldContain("badge badge-warning");
        html.ShouldContain("Gave up after 6 attempts, at 2 Nov 2026, 09:00");
        html.ShouldContain("Gave up after 1 attempt, at");
        html.ShouldContain("Next try 2 Nov 2026, 12:05; gives up 3 Nov 2026, 12:00");
        html.ShouldContain("Delivered 30 Oct 2026, 10:01");
        html.ShouldContain("HTTP 503");
        html.ShouldContain("Showing 4 events. That's everything that matches.");
        html.ShouldNotContain("https://", customMessage: "no callback address is ever shown");
    }

    [Fact]
    public async Task OnlyFailedEventsOfferToBeSentAgain()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"resend={FailedSix}");
        html.ShouldContain($"resend={FailedOne}");
        html.ShouldNotContain($"resend={Pending}");
        html.ShouldNotContain($"resend={Delivered}");
        Regex.Matches(html, "Send again").Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheEventsAreReadWithThePersonsOwnSignInToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        var call = api.AdminCalls.Last(call => call.PathAndQuery.Contains("limit=50"));
        call.PathAndQuery.ShouldBe("/api/auth/admin/webhooks?limit=50");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
    }

    [Fact]
    public async Task FiltersAreSentToTheApiAndKeptInTheForm()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?status=failed&app={Helpdesk}", Token);

        var query = api.AdminCalls.Last(call => call.PathAndQuery.Contains("limit=50")).PathAndQuery;
        query.ShouldContain("status=Failed");
        query.ShouldContain($"applicationId={Helpdesk}");
        html.ShouldContain("<option value=\"Failed\" selected");
        html.ShouldContain($"<option value=\"{Helpdesk}\" selected");
        html.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task AStatusThatIsNotOneOfTheThreeIsIgnored()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync($"{PagePath}?status=Bogus", Token);

        api.AdminCalls.Last(call => call.PathAndQuery.Contains("limit=50")).PathAndQuery.ShouldBe("/api/auth/admin/webhooks?limit=50");
    }

    [Fact]
    public async Task TheProductFilterListsTheProductsAndTheListWorksWithoutIt()
    {
        var api = Api();
        var down = Api();
        down.ProductsResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        await using var downFactory = new WebAppFactory(down);
        using var client = await SignedInAsync(factory);
        using var downClient = await SignedInAsync(downFactory);

        var html = await client.GetStringAsync(PagePath, Token);
        var withoutProducts = await downClient.GetStringAsync(PagePath, Token);

        var select = Regex.Match(html, "<select[^>]*name=\"app\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Any product");
        select.ShouldContain("Helpdesk");
        select.ShouldContain("Billing");
        withoutProducts.ShouldContain("An organisation subscribed");
        withoutProducts.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task TheSendAgainQuestionNamesTheEventAndIsOnlyAskedForAFailedOne()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?resend={FailedSix}", Token));

        dialog.ShouldContain("role=\"dialog\"");
        dialog.ShouldContain("Send this event again?");
        dialog.ShouldContain("\"An organisation subscribed\" for <strong>helpdesk</strong>");
        dialog.ShouldContain("Nothing else changes.");
        foreach (var other in new[] { Pending, Delivered, Guid.NewGuid() })
        {
            (await client.GetStringAsync($"{PagePath}?resend={other}", Token)).ShouldNotContain("role=\"dialog\"");
        }
    }

    [Fact]
    public async Task SendingAgainAsksTheApiAsTheAdminThenSaysItIsQueued()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostResendAsync(client, FailedSix);

        var call = api.WebhookResendCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/webhooks/{FailedSix}/resend");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        Where(posted).ShouldBe($"{PagePath}?notice=resent&m={FailedSix}");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The event is queued to be sent again");
    }

    [Fact]
    public async Task TheApisOwnReasonsAreExplainedInPlainWords()
    {
        var notFailed = Api();
        notFailed.WebhookResendResponse = () => Problem(HttpStatusCode.Conflict, "webhook_not_failed");
        var gone = Api();
        gone.WebhookResendResponse = () => Problem(HttpStatusCode.NotFound, "webhook_not_found");
        var refused = Api();
        refused.WebhookResendResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.WebhookResendResponse = () => throw new HttpRequestException("connection refused");
        var cases = new (FakeAuthApi Api, string Expect)[]
        {
            (notFailed, "That event isn't failed any more"),
            (gone, "That event isn't here any more"),
            (refused, "You don't have permission to do that"),
            (down, "Auth Service isn't responding right now. Nothing was changed."),
        };

        foreach (var (api, expect) in cases)
        {
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var posted = await PostResendAsync(client, FailedSix);
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminCannotSendAnythingEvenByPostingDirectly()
    {
        var admin = Api();
        await using var adminFactory = new WebAppFactory(admin);
        using var adminClient = await SignedInAsync(adminFactory);
        var form = await ResendFormAsync(adminClient, FailedSix);

        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        // The anti-forgery token is per browser, so ask the non-admin's own site for one through a page that has a form.
        var page = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var posted = await client.PostAsync($"{PagePath}/{FailedSix}/resend", new FormUrlEncodedContent([new("__RequestVerificationToken", token)]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.WebhookResendCalls.ShouldBeEmpty();
        form.ShouldNotBeNull();
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsSent()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await client.PostAsync($"{PagePath}/{FailedSix}/resend", new FormUrlEncodedContent([]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.WebhookResendCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task LoadMoreAsksForFiftyMoreUpToTheMostTheApiAllows()
    {
        var api = Api();
        api.AdminWebhooksJson = api.AdminWebhooksJson.Replace("\"next\":null", """
            "next":{"before":"2026-10-10T08:00:00Z","beforeId":"60000000-0000-4000-8000-000000000009"}
            """.Trim());
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var first = await client.GetStringAsync(PagePath, Token);
        var last = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?take=200&status=Failed", Token));

        first.ShouldContain($"href=\"{PagePath}?take=100\"");
        last.ShouldNotContain("Load more");
        last.ShouldContain("This is the most that can be shown at once.");
    }

    [Fact]
    public async Task EmptyAndNoMatchSayDifferentThings()
    {
        var api = Api();
        api.AdminWebhooksJson = """{"items":[],"next":null}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("No events yet");
        var filtered = await client.GetStringAsync($"{PagePath}?status=Failed", Token);
        filtered.ShouldContain("No matching events");
        filtered.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task AListTheApiCannotReadIsACalmError()
    {
        var api = Api();
        api.AdminResponse = path => path.Contains("limit=50") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t load the webhook events");
        html.ShouldContain("Try again");
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoEvents()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync(PagePath, Token);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        html.ShouldNotContain("An organisation subscribed");
        html.ShouldContain("You don't have access to this page");
        api.AdminCalls.ShouldHaveSingleItem();
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>Just the rows of the list: the filters above it name statuses and products too.</summary>
    private static string Rows(string html) => Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value;

    private static async Task<List<KeyValuePair<string, string>>> ResendFormAsync(HttpClient client, Guid id)
    {
        var page = await client.GetStringAsync($"{PagePath}?resend={id}", Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("/resend\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PostResendAsync(HttpClient client, Guid id) =>
        await client.PostAsync($"{PagePath}/{id}/resend", new FormUrlEncodedContent(await ResendFormAsync(client, id)), Token);

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        ProductsJson = $$"""[{"id":"{{Billing}}","key":"billing","name":"Billing"},{"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk"}]""",
        AdminWebhooksJson = $$"""
            {"items":[
              {"id":"{{FailedSix}}","applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","eventType":"ApplicationSubscribed","status":"Failed","attempts":6,"lastError":"HTTP 503","createdAt":"2026-11-01T09:00:00Z","nextAttemptAt":"2026-11-01T20:00:00Z","deadlineAt":"2026-11-02T09:00:00Z","deliveredAt":null},
              {"id":"{{Pending}}","applicationId":"{{Billing}}","applicationKey":"billing","eventType":"ApplicationUnsubscribed","status":"Pending","attempts":1,"lastError":"HTTP 500","createdAt":"2026-11-02T12:00:00Z","nextAttemptAt":"2026-11-02T12:05:00Z","deadlineAt":"2026-11-03T12:00:00Z","deliveredAt":null},
              {"id":"{{Delivered}}","applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","eventType":"ApplicationSubscribed","status":"Delivered","attempts":1,"lastError":null,"createdAt":"2026-10-30T10:00:00Z","nextAttemptAt":"2026-10-30T10:00:00Z","deadlineAt":"2026-10-31T10:00:00Z","deliveredAt":"2026-10-30T10:01:00Z"},
              {"id":"{{FailedOne}}","applicationId":"{{Billing}}","applicationKey":"billing","eventType":"ApplicationSubscribed","status":"Failed","attempts":1,"lastError":null,"createdAt":"2026-10-20T10:00:00Z","nextAttemptAt":"2026-10-20T10:00:00Z","deadlineAt":"2026-10-21T10:00:00Z","deliveredAt":null}
            ],"next":null}
            """,
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
