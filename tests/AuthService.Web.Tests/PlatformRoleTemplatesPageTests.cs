using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's roles a product ships with (ONB-04): what they are, editing the whole set, and copying them to organisations. No real API is involved.</summary>
public sealed class PlatformRoleTemplatesPageTests
{
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Missing = Guid.Parse("20000000-0000-4000-8000-0000000000ff");
    private static string PagePath => $"/platform/products/{Helpdesk}/roles";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const string TwoRoles = """
        {"roles":[
          {"id":"40000000-0000-4000-8000-000000000001","name":"Analyst","description":null,"permissions":[{"key":"campaign.view","level":"ReadOnly"}]},
          {"id":"40000000-0000-4000-8000-000000000002","name":"Marketer","description":"Builds and sends campaigns","permissions":[{"key":"campaign.send","level":"CreateRead"},{"key":"campaign.view","level":"ReadOnly"},{"key":"user.manage","level":"Full"}]}
        ]}
        """;

    [Fact]
    public async Task TheRolesAreListedWithWhatTheyAreForAndWhatTheyCanDoInPlainWords()
    {
        await using var factory = new WebAppFactory(Api(TwoRoles));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Helpdesk roles · Auth Service</title>");
        html.ShouldContain("Analyst");
        html.ShouldContain("Marketer");
        html.ShouldContain("Builds and sends campaigns");
        html.ShouldContain("Not described");
        html.ShouldContain("<code class=\"mono\">campaign.send</code>");
        html.ShouldContain("View and create");
        html.ShouldContain("View only");
        html.ShouldContain("Full access");
        html.ShouldContain("Showing 2 roles");
        html.ShouldContain("Copies, not links");
        html.ShouldContain($"{PagePath}?apply=1");
        html.ShouldContain($"{PagePath}?edit=1");
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task WithNoRolesTheEmptyStateOffersToEditAndThereIsNothingToCopy()
    {
        await using var factory = new WebAppFactory(Api("""{"roles":[]}"""));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);
        var apply = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?apply=1", Token));

        html.ShouldContain("No roles yet");
        html.ShouldNotContain($"{PagePath}?apply=1");
        apply.ShouldNotContain("Copy the role");
        apply.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task AnUnknownProductSaysSoAndALoadFailureOffersTryAgain()
    {
        await using var factory = new WebAppFactory(Api("""{"roles":[]}"""));
        using var client = await SignedInAsync(factory);
        var missing = WebUtility.HtmlDecode(await client.GetStringAsync($"/platform/products/{Missing}/roles", Token));
        missing.ShouldContain("That product isn't here");

        var failing = Api("""{"roles":[]}""");
        failing.RolesReadResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await using var failingFactory = new WebAppFactory(failing);
        using var failingClient = await SignedInAsync(failingFactory);

        WebUtility.HtmlDecode(await failingClient.GetStringAsync(PagePath, Token)).ShouldContain("We couldn't load the roles");
    }

    [Fact]
    public async Task EveryProductOnTheListLinksToItsRoles()
    {
        await using var factory = new WebAppFactory(Api(TwoRoles));
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync("/platform/products", Token)).ShouldContain($"href=\"/platform/products/{Helpdesk}/roles\"");
    }

    [Fact]
    public async Task TheEditDialogStartsFromTheRolesThereAreAsJson()
    {
        await using var factory = new WebAppFactory(Api(TwoRoles));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?edit=1", Token));

        html.ShouldContain("Edit the roles of Helpdesk");
        html.ShouldContain("\"name\": \"Marketer\"");
        html.ShouldContain("\"key\": \"campaign.send\"");
        html.ShouldContain("\"level\": \"CreateRead\"");
        html.ShouldContain("data-load-into=\"roles-json\"");
    }

    [Fact]
    public async Task SavingSendsTheWholeSetAsTheAdminAndSaysHowManyThereAre()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostFormAsync(client, "?edit=1", "edit", new()
        {
            ["json"] = """{"permissions":[],"roles":[{"name":" Agent ","description":"Answers customers","permissions":[{"key":"inbox.reply","level":"CreateRead"}]},{"name":"Viewer"}]}""",
        });

        var call = api.SetRolesCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/role-templates");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"name\":\"Agent\"");
        call.Body.ShouldContain("\"description\":\"Answers customers\"");
        call.Body.ShouldContain("\"key\":\"inbox.reply\"");
        call.Body.ShouldContain("\"level\":\"CreateRead\"");
        call.Body.ShouldContain("\"name\":\"Viewer\"");
        Where(posted).ShouldBe($"{PagePath}?notice=saved&roles=2");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        page.ShouldContain("The roles are saved");
        page.ShouldContain("2 roles now.");
    }

    [Fact]
    public async Task AnEmptyListIsSentAsRemovingEveryRole()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostFormAsync(client, "?edit=1", "edit", new() { ["json"] = """{"roles": []}""" });

        api.SetRolesCalls.ShouldHaveSingleItem().Body.ShouldBe("""{"roles":[]}""");
        Where(posted).ShouldBe($"{PagePath}?notice=saved&roles=0");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain("There are no roles now.");
    }

    [Fact]
    public async Task TextThatCannotBeReadNeverReachesTheApiAndWhatWasTypedIsKept()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var notJson = await PostFormAsync(client, "?edit=1", "edit", new() { ["json"] = "Marketer: campaign.view" });
        var noList = await PostFormAsync(client, "?edit=1", "edit", new() { ["json"] = """{"permissions":[]}""" });
        var badEntries = await PostFormAsync(client, "?edit=1", "edit", new() { ["json"] = """{"roles":[{"description":"no name"},{"name":"X","permissions":[{"key":"a.b"}]}]}""" });

        var first = WebUtility.HtmlDecode(await notJson.Content.ReadAsStringAsync(Token));
        first.ShouldContain("Paste JSON with a \"roles\" list");
        first.ShouldContain("Marketer: campaign.view");
        WebUtility.HtmlDecode(await noList.Content.ReadAsStringAsync(Token)).ShouldContain("Paste JSON with a \"roles\" list");
        var third = WebUtility.HtmlDecode(await badEntries.Content.ReadAsStringAsync(Token));
        third.ShouldContain("Fix 2 problems");
        third.ShouldContain("Role 1: write a name.");
        third.ShouldContain("Role 2, permission 1: write a key and a level.");
        api.SetRolesCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisReasonsForRefusingTheRolesAreExplainedAndWhatWasTypedIsKept()
    {
        var cases = new (Func<string, HttpResponseMessage?> Respond, string Expect)[]
        {
            (_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status":400,"title":"x","errors":{"Roles[0].Permissions[0].Key":["Unknown permission 'campaign.sned' for this product. Register it in the product's permissions first."]}}""", Encoding.UTF8, "application/problem+json"),
            }, "Unknown permission 'campaign.sned'"),
            (_ => Problem(HttpStatusCode.NotFound, "application_not_found"), "That product isn't here any more."),
            (_ => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to change roles."),
            (_ => throw new HttpRequestException("connection refused"), "Nothing was changed."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api(TwoRoles);
            api.SetRolesResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var response = await PostFormAsync(client, "?edit=1", "edit", new() { ["json"] = """{"roles":[{"name":"Typed here"}]}""" });
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldContain("Typed here");
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task CopyingAsksFirstSayingNothingIsOverwrittenThenTellsWhatItDid()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?apply=1", Token));
        dialog.ShouldContain("Copy the 2 roles to organisations?");
        dialog.ShouldContain("left exactly as it is");
        dialog.ShouldContain("Nothing is overwritten or removed");

        var posted = await PostFormAsync(client, "?apply=1", "apply", []);

        var call = api.ApplyRolesCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/role-templates/apply");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        Where(posted).ShouldBe($"{PagePath}?notice=applied&orgs=3&created=4&skipped=2");
        var page = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        page.ShouldContain("The roles are copied");
        page.ShouldContain("4 roles were created in 3 organisations, and 2 were left alone because the organisation already had a role with that name.");
    }

    [Fact]
    public async Task CopyingWhenNothingWasNeededSaysSo()
    {
        var api = Api(TwoRoles);
        api.ApplyRolesResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"organisations":1,"rolesCreated":0,"rolesSkipped":2}""", Encoding.UTF8, "application/json"),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostFormAsync(client, "?apply=1", "apply", []);

        var page = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        page.ShouldContain("Nothing needed copying");
        page.ShouldContain("0 roles were created in 1 organisation, and 2 were left alone");
    }

    [Fact]
    public async Task TheApisReasonsForRefusingACopyAreExplained()
    {
        var cases = new (Func<HttpResponseMessage?> Respond, string Expect)[]
        {
            (() => Problem(HttpStatusCode.Conflict, "role_name_conflict"), "at the same moment. Nothing was changed. Try again."),
            (() => Problem(HttpStatusCode.NotFound, "application_not_found"), "That product isn't here any more."),
            (() => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to change roles."),
            (() => throw new HttpRequestException("connection refused"), "Nothing was changed."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api(TwoRoles);
            api.ApplyRolesResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var response = await PostFormAsync(client, "?apply=1", "apply", []);
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldContain("role=\"dialog\"");
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task RegisteringWithRolesInTheJsonSendsThemAndTheSecretPanelCountsThem()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostProductFormAsync(client, new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "",
            ["permissions"] = """{"permissions":[{"key":"campaign.view","label":"View"}],"roles":[{"name":"Marketer","permissions":[{"key":"campaign.view","level":"ReadOnly"}]},{"name":"Agent"}]}""",
            ["activate"] = "on",
        });

        var body = api.RegisterProductCalls.ShouldHaveSingleItem().Body;
        body.ShouldContain("\"key\":\"campaign.view\"");
        body.ShouldContain("\"roles\":[");
        body.ShouldContain("\"name\":\"Marketer\"");
        body.ShouldContain("\"name\":\"Agent\"");
        var panel = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        panel.ShouldContain("Roles: 2 roles that every organisation gets when it subscribes");
    }

    [Fact]
    public async Task RegisteringWithoutRolesSendsNoneAndShowsNoRolesLine()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostProductFormAsync(client, new() { ["name"] = "Marketing", ["key"] = "marketing", ["audience"] = "", ["permissions"] = "campaign.view | View" });

        api.RegisterProductCalls.ShouldHaveSingleItem().Body.ShouldContain("\"roles\":null");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldNotContain("Roles:");
    }

    [Fact]
    public async Task ABrokenRoleInTheRegisterJsonNeverReachesTheApi()
    {
        var api = Api(TwoRoles);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostProductFormAsync(client, new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "",
            ["permissions"] = """{"permissions":[{"key":"campaign.view","label":"View"}],"roles":[{"description":"no name"}]}""",
        });

        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)).ShouldContain("Role 1: write a name.");
        api.RegisterProductCalls.ShouldBeEmpty();
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static FakeAuthApi Api(string rolesJson) => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        RoleTemplatesJson = rolesJson,
        AdminProductsJson = $$"""
            [
              {"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","isActive":true,"permissionCount":3,"launchUrl":"https://helpdesk.example.com/in","createdAt":"2026-10-03T10:00:00Z"}
            ]
            """,
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string query, string handler, Dictionary<string, string> fields) =>
        PostToAsync(client, PagePath + query, handler, fields);

    private static Task<HttpResponseMessage> PostProductFormAsync(HttpClient client, Dictionary<string, string> fields) =>
        PostToAsync(client, "/platform/products?register=1", "register", fields);

    /// <summary>Opens a dialog that posts to the page itself (a Blazor form), fills it in, and posts it as a browser would.</summary>
    private static async Task<HttpResponseMessage> PostToAsync(HttpClient client, string address, string handler, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(address, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value)
            .First(candidate => candidate.Contains("name=\"_handler\"") && candidate.Contains($"value=\"{handler}\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        body.RemoveAll(field => fields.ContainsKey(field.Key));
        body.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync(address, new FormUrlEncodedContent(body), Token);
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
