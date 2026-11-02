using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's product permissions (UI04_T3): the catalog, adding several at once, and the rule that it only grows. No real API is involved.</summary>
public sealed class PlatformPermissionsPageTests
{
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Draft = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath(Guid id) => $"/platform/products/{id}/permissions";

    [Fact]
    public async Task ThePermissionsAreListedByKeyWithLabelDescriptionAndDate()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath(Helpdesk), Token));

        html.ShouldContain("<title>Helpdesk permissions · Auth Service</title>");
        html.ShouldContain("Helpdesk permissions");
        html.ShouldContain("ticket.close");
        html.ShouldContain("Close tickets");
        html.ShouldContain("Reply to a customer");
        html.ShouldContain("None", customMessage: "a permission with no description says so");
        html.ShouldContain("1 Oct 2026");
        html.ShouldContain("Showing 2 permissions");
        html.IndexOf("ticket.close", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("ticket.reply", StringComparison.Ordinal));
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task TheOnlyGrowsRuleIsExplainedOnThePage()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath(Helpdesk), Token));

        html.ShouldContain("The list only grows");
        html.ShouldContain("A permission is never removed. A role may already grant it");
        html.ShouldContain("Adding a key that is already here changes its label and description.");
    }

    [Fact]
    public async Task AProductWithNoPermissionsHasAnEmptyStateAndAProductWithSomeCanBeMadeAvailable()
    {
        var empty = Api();
        empty.AdminPermissionsJson = "[]";
        await using var emptyFactory = new WebAppFactory(empty);
        using var emptyClient = await SignedInAsync(emptyFactory);
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var none = await emptyClient.GetStringAsync(PagePath(Draft), Token);
        var some = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath(Draft), Token));
        var alreadyAvailable = await client.GetStringAsync(PagePath(Helpdesk), Token);

        none.ShouldContain("No permissions yet");
        none.ShouldNotContain($"/platform/products/{Draft}/active");
        some.ShouldContain("Draft can be made available");
        some.ShouldContain($"action=\"/platform/products/{Draft}/active\"");
        alreadyAvailable.ShouldNotContain("can be made available");
    }

    [Fact]
    public async Task AnUnknownProductIsSaidToBeGone()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath(Guid.NewGuid()), Token));

        html.ShouldContain("That product isn't here");
        html.ShouldContain("Back to products");
    }

    [Fact]
    public async Task SeveralPermissionsAreSentAtOnceAsTheAdminAndTheNoticeSaysWhatHappened()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath(Draft)}?add=1", Token));
        dialog.ShouldContain("Add permissions to Draft");
        dialog.ShouldContain("separated by a | sign");

        var posted = await PostAddAsync(client, Draft, "ticket.reply | Answer tickets | Reply to a customer\r\n\r\n  ticket.close|Close tickets  \r\nreport.view | See reports");

        var call = api.AddPermissionsCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Draft}/permissions");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("{\"key\":\"ticket.reply\",\"label\":\"Answer tickets\",\"description\":\"Reply to a customer\"}");
        call.Body.ShouldContain("{\"key\":\"ticket.close\",\"label\":\"Close tickets\",\"description\":null}");
        call.Body.ShouldContain("{\"key\":\"report.view\",\"label\":\"See reports\",\"description\":null}");
        Where(posted).ShouldBe($"{PagePath(Draft)}?notice=added&created=3&updated=0&unchanged=0");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The permissions are saved");
        html.ShouldContain("3 added.");
    }

    [Fact]
    public async Task LinesThatAreNotAKeyAndALabelNeverReachTheApiAndWhatWasTypedIsKept()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostAddAsync(client, Draft, "ticket.reply | Answer tickets\njust a key\n | no key here");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("Line 2: write a key and a label separated by a | sign.");
        html.ShouldContain("Line 3: write a key and a label separated by a | sign.");
        html.ShouldContain("ticket.reply | Answer tickets");
        api.AddPermissionsCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnEmptyBoxIsAskedToHaveAtLeastOnePermission()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await (await PostAddAsync(client, Draft, " \n ")).Content.ReadAsStringAsync(Token));

        html.ShouldContain("Write at least one permission.");
        api.AddPermissionsCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisReasonsAreExplainedInTheDialogWithWhatWasTypedKept()
    {
        var cases = new (Func<string, HttpResponseMessage?> Respond, string Expect)[]
        {
            (_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status":400,"title":"x","errors":{"Permissions[0].Key":["Key must be 1 to 100 lowercase letters, digits and . _ - characters, such as campaign.create."]}}""", Encoding.UTF8, "application/problem+json"),
            }, "Key must be 1 to 100 lowercase letters"),
            (_ => Problem(HttpStatusCode.NotFound, "application_not_found"), "That product isn't here any more."),
            (_ => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to change products."),
            (_ => throw new HttpRequestException("connection refused"), "Nothing was saved."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api();
            api.AddPermissionsResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var html = WebUtility.HtmlDecode(await (await PostAddAsync(client, Draft, "Bad Key | A label")).Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldContain("Bad Key | A label");
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task APermissionThatWasUpdatedOrAlreadyThereIsReportedAsSuch()
    {
        var api = Api();
        api.AddPermissionsResponse = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"applicationId":"{{Draft}}","created":1,"updated":2,"unchanged":3,"permissions":[]}""", Encoding.UTF8, "application/json"),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostAddAsync(client, Draft, "a.b | A");

        Where(posted).ShouldBe($"{PagePath(Draft)}?notice=added&created=1&updated=2&unchanged=3");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain("1 added, 2 changed, 3 already there.");
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoPermissionsAndNothingIsSent()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync(PagePath(Helpdesk), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("ticket.reply");
        api.AdminCalls.ShouldHaveSingleItem("only the check was made");
    }

    [Fact]
    public async Task AListTheApiCannotReadIsACalmError()
    {
        var api = Api();
        api.AdminResponse = path => path.EndsWith("/permissions") ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath(Helpdesk), Token)).ShouldContain("We couldn&#x27;t load the permissions");
    }

    [Fact]
    public async Task ThePermissionsPageIsReachedFromTheProductListAndKeepsProductsHighlighted()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var list = await client.GetStringAsync("/platform/products", Token);
        var page = await client.GetStringAsync(PagePath(Helpdesk), Token);

        list.ShouldContain($"href=\"{PagePath(Helpdesk)}\"");
        page.ShouldContain("href=\"/platform/products\" aria-current=\"page\"");
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static async Task<HttpResponseMessage> PostAddAsync(HttpClient client, Guid product, string lines)
    {
        var page = await client.GetStringAsync($"{PagePath(product)}?add=1", Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("value=\"add\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        body.Add(new("lines", lines));
        return await client.PostAsync($"{PagePath(product)}?add=1", new FormUrlEncodedContent(body), Token);
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        AdminProductsJson = $$"""
            [
              {"id":"{{Draft}}","key":"draft","name":"Draft","isActive":false,"permissionCount":2,"launchUrl":null,"createdAt":"2026-10-02T10:00:00Z"},
              {"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","isActive":true,"permissionCount":2,"launchUrl":null,"createdAt":"2026-10-03T10:00:00Z"}
            ]
            """,
        AdminPermissionsJson = """
            [
              {"key":"ticket.close","label":"Close tickets","description":null,"createdAt":"2026-10-01T10:00:00Z","updatedAt":"2026-10-01T10:00:00Z"},
              {"key":"ticket.reply","label":"Answer tickets","description":"Reply to a customer","createdAt":"2026-10-02T10:00:00Z","updatedAt":"2026-10-02T10:00:00Z"}
            ]
            """,
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
