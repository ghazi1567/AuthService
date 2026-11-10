using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's organisations (AUTH-268): the list, suspending and reactivating with a reason. No real API is involved.</summary>
public sealed class PlatformOrganisationsPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Globex = Guid.Parse("8e9f7ca3-3e1c-4a54-8f1b-1e7b1a1a5e22");
    private const string PagePath = "/platform/organisations";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OrganisationsAreListedWithStatusPeopleAndTheRightAction()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Organisations · Auth Service</title>");
        html.ShouldContain("Acme Ltd");
        html.ShouldContain("Globex");
        html.ShouldContain("badge badge-success");
        html.ShouldContain("badge badge-error");
        html.ShouldContain("Suspended");
        html.ShouldContain("1 Oct 2026");
        html.ShouldContain("Showing 2 organisations");
        html.ShouldContain($"href=\"{PagePath}?org={Acme}&to=Suspended\"");
        html.ShouldContain($"href=\"{PagePath}?org={Globex}&to=Active\"");
        html.ShouldContain(">Suspend</a>");
        html.ShouldContain(">Reactivate</a>");
        html.ShouldContain("href=\"/platform/organisations\"", customMessage: "the console's navigation has it");
        Regex.IsMatch(html, "aria-current=\"page\"[^>]*>(<svg.*?</svg>)?Organisations", RegexOptions.Singleline).ShouldBeTrue("and it is the current page");
    }

    [Fact]
    public async Task TheListIsReadWithThePersonsOwnSignInToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        var call = api.AdminCalls.Last(call => call.PathAndQuery.StartsWith("/api/auth/admin/tenants", StringComparison.Ordinal));
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
    }

    [Fact]
    public async Task SearchingByNameNarrowsTheListAndKeepsTheWords()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var found = await client.GetStringAsync($"{PagePath}?q=glo", Token);
        var none = await client.GetStringAsync($"{PagePath}?q=zzz", Token);

        found.ShouldContain("Globex");
        found.ShouldNotContain("Acme Ltd");
        found.ShouldContain("value=\"glo\"");
        found.ShouldContain("Clear");
        none.ShouldContain("No matching organisations");
    }

    [Fact]
    public async Task SuspendingAsksFirstNamingTheOrganisationAndWhatItStops()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?org={Acme}&to=Suspended", Token));

        dialog.ShouldContain("role=\"dialog\"");
        dialog.ShouldContain("Suspend Acme Ltd?");
        dialog.ShouldContain("even if they're signed in now");
        dialog.ShouldContain("Nothing is deleted");
        dialog.ShouldContain($"action=\"/platform/organisations/{Acme}/status\"");
        dialog.ShouldContain("name=\"status\" value=\"Suspended\"");
        var reasons = Regex.Match(dialog, "<select id=\"reason\".*?</select>", RegexOptions.Singleline).Value;
        reasons.ShouldContain("Non-payment");
        reasons.ShouldContain("value=\"abuse\"");
        reasons.ShouldNotContain("resolved");
        dialog.ShouldContain("btn btn-danger");
        api.OrganisationStatusCalls.ShouldBeEmpty("asking changes nothing");
    }

    [Fact]
    public async Task ReactivatingAsksForItsOwnReasonsWithoutAWarning()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?org={Globex}&to=Active", Token));

        dialog.ShouldContain("Reactivate Globex?");
        dialog.ShouldContain("Everything is as it was.");
        var reasons = Regex.Match(dialog, "<select id=\"reason\".*?</select>", RegexOptions.Singleline).Value;
        reasons.ShouldContain("value=\"resolved\"");
        reasons.ShouldNotContain("non_payment");
        dialog.ShouldNotContain("btn btn-danger");
    }

    [Theory]
    [InlineData("org=00000000-0000-4000-8000-000000000999&to=Suspended", "an organisation that is not in the list")]
    [InlineData("org=7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11&to=Active", "suspending's opposite for one that is already active")]
    [InlineData("org=8e9f7ca3-3e1c-4a54-8f1b-1e7b1a1a5e22&to=Suspended", "suspending one that is already suspended")]
    [InlineData("org=7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11&to=Deleted", "a status that does not exist")]
    [InlineData("org=7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11", "no status")]
    public async Task NoQuestionIsAskedWhenThereIsNothingToChange(string query, string because)
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync($"{PagePath}?{query}", Token)).ShouldNotContain("role=\"dialog\"", customMessage: because);
    }

    [Fact]
    public async Task ConfirmingSuspendsAsTheAdminWithTheChosenReasonAndSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAsync(client, Acme, "Suspended", "non_payment");

        var call = api.OrganisationStatusCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/tenants/{Acme}/status");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"status\":\"Suspended\"");
        call.Body.ShouldContain("\"reason\":\"non_payment\"");
        Where(posted).ShouldBe($"{PagePath}?notice=suspended&m={Acme}");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("Organisation suspended");
        html.ShouldContain("Acme Ltd is suspended. Its admins are refused straight away");
    }

    [Fact]
    public async Task ConfirmingReactivatesAndSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAsync(client, Globex, "Active", "resolved");

        api.OrganisationStatusCalls.ShouldHaveSingleItem().Body.ShouldContain("\"status\":\"Active\"");
        Where(posted).ShouldBe($"{PagePath}?notice=reactivated&m={Globex}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Globex is active again.");
    }

    [Fact]
    public async Task WithoutAReasonNothingIsSentAndTheQuestionComesBack()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAsync(client, Acme, "Suspended", "");

        api.OrganisationStatusCalls.ShouldBeEmpty();
        Where(posted).ShouldBe($"{PagePath}?org={Acme}&to=Suspended&error=choose_reason");
        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain("Choose a reason");
        html.ShouldContain("role=\"dialog\"");
    }

    [Fact]
    public async Task AReasonThatIsNotOnTheListIsNeverSentToTheApi()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAsync(client, Acme, "Suspended", "my own words");

        api.OrganisationStatusCalls.ShouldBeEmpty();
        Where(posted).ShouldContain("error=choose_reason");
    }

    [Fact]
    public async Task AStatusThatIsNotActiveOrSuspendedIsRefused()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAsync(client, Globex, "Deleted", "other");

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.OrganisationStatusCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisOwnReasonsAreExplainedInPlainWords()
    {
        var gone = Api();
        gone.OrganisationStatusResponse = _ => Problem(HttpStatusCode.NotFound, "tenant_not_found");
        var refused = Api();
        refused.OrganisationStatusResponse = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.OrganisationStatusResponse = _ => throw new HttpRequestException("connection refused");
        var cases = new (FakeAuthApi Api, string Expect)[]
        {
            (gone, "That organisation isn't here any more"),
            (refused, "Only platform admins can suspend or reactivate an organisation."),
            (down, "Auth Service isn't responding right now. Nothing was changed."),
        };

        foreach (var (api, expect) in cases)
        {
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var posted = await PostAsync(client, Acme, "Suspended", "abuse");
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminCannotChangeAnythingEvenByPostingDirectly()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        // The anti-forgery token is per browser, so ask the non-admin's own site for one through a page that has a form.
        var page = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var posted = await client.PostAsync($"{PagePath}/{Acme}/status",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("status", "Suspended"), new("reason", "abuse")]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.OrganisationStatusCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingChanges()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await client.PostAsync($"{PagePath}/{Acme}/status", new FormUrlEncodedContent([new("status", "Suspended"), new("reason", "abuse")]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.OrganisationStatusCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoOrganisationsAndAListTheApiCannotReadAreSaidCalmly()
    {
        var empty = Api();
        empty.AdminOrganisationsJson = "[]";
        var broken = Api();
        broken.AdminResponse = path => path.StartsWith("/api/auth/admin/tenants", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await using var emptyFactory = new WebAppFactory(empty);
        await using var brokenFactory = new WebAppFactory(broken);
        using var emptyClient = await SignedInAsync(emptyFactory);
        using var brokenClient = await SignedInAsync(brokenFactory);

        (await emptyClient.GetStringAsync(PagePath, Token)).ShouldContain("No organisations yet");
        var html = await brokenClient.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t load the organisations");
        html.ShouldContain("Try again");
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoOrganisations()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync(PagePath, Token);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        html.ShouldNotContain("Globex");
        html.ShouldContain("You don't have access to this page");
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>Opens the question for the organisation, as the page does, and posts it with the given reason.</summary>
    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string status, string reason)
    {
        var to = status == "Suspended" ? "Suspended" : "Active";
        var page = await client.GetStringAsync($"{PagePath}?org={id}&to={to}", Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("/status\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        // The form's own hidden status is what a browser would send; a test that wants another one replaces it.
        body.RemoveAll(pair => pair.Key == "status");
        body.Add(new("status", status));
        body.Add(new("reason", reason));
        return await client.PostAsync($"{PagePath}/{id}/status", new FormUrlEncodedContent(body), Token);
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        AdminOrganisationsJson = $$"""
            [{"id":"{{Acme}}","name":"Acme Ltd","status":"Active","memberCount":7,"createdAt":"2026-10-01T10:00:00Z"},
             {"id":"{{Globex}}","name":"Globex","status":"Suspended","memberCount":1,"createdAt":"2026-10-15T10:00:00Z"}]
            """,
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
