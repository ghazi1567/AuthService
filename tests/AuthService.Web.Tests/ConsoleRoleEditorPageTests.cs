using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's role editor (UI02_T5): creating and editing a role from the permission catalog. No real API is involved.</summary>
public sealed class ConsoleRoleEditorPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid OwnerRole = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid Auth = Guid.Parse("00000000-0000-7000-8000-00000000a07a");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Retired = Guid.Parse("20000000-0000-4000-8000-000000000009");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string NewPath => $"/console/{Acme}/roles/new";

    private static string EditPath(Guid id) => $"/console/{Acme}/roles/{id}";

    [Fact]
    public async Task CreatingShowsANameAndEveryProductsPermissionsEachWithALevel()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(NewPath, Token);

        html.ShouldContain("<title>Create role · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Create role</h1>""");
        html.ShouldContain("""<label for="role-name">Role name</label>""");
        html.ShouldContain("0 of 2 permissions");
        html.ShouldContain("Auth Service");
        html.ShouldContain("Helpdesk");
        html.ShouldContain("See members");
        html.ShouldContain("Answer tickets");
        html.ShouldContain($"""name="grant:{Auth}:members.read" """.TrimEnd());
        html.ShouldContain("View only");
        html.ShouldContain("View and create");
        html.ShouldContain("Full access");
        html.ShouldContain("data-unsaved-guard");
        html.ShouldContain("Create role</button>");
    }

    [Fact]
    public async Task EditingStartsFromTheRolesCurrentName_AndLevels()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(EditPath(AdminRole), Token);

        html.ShouldContain("Edit &#x27;Admin&#x27;");
        html.ShouldContain("""value="Admin" """.TrimEnd());
        html.ShouldContain("1 of 2 permissions");
        Regex.IsMatch(html, $"""<select[^>]*name="grant:{Auth}:members.read"[^>]*>.*?<option value="Full" selected""", RegexOptions.Singleline).ShouldBeTrue("the current level is selected");
        html.ShouldContain("Save changes</button>");
    }

    [Fact]
    public async Task SavingANewRoleSendsOnlyTheGrantsAboveNoAccessAsTheAdmin()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await SaveAsync(client, NewPath, "  Support  ", new Dictionary<string, string>
        {
            [$"grant:{Auth}:members.read"] = "ReadOnly",
            [$"grant:{Helpdesk}:tickets.reply"] = "CreateRead",
        });

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"/console/{Acme}/roles?notice=saved");
        var call = api.SaveRoleCalls.ShouldHaveSingleItem();
        call.Method.ShouldBe("POST");
        call.Path.ShouldBe("/api/auth/roles");
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Body.ShouldContain("\"name\":\"Support\"");
        call.Body.ShouldContain($"{{\"applicationId\":\"{Auth}\",\"permission\":\"members.read\",\"level\":\"ReadOnly\"}}");
        call.Body.ShouldContain($"{{\"applicationId\":\"{Helpdesk}\",\"permission\":\"tickets.reply\",\"level\":\"CreateRead\"}}");
        call.Body.ShouldNotContain("roles.read", customMessage: "a permission left at no access is not sent");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Role saved");
    }

    [Fact]
    public async Task SavingAnEditReplacesTheWholeSetWithPatch()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await SaveAsync(client, EditPath(AdminRole), "Administrators", new Dictionary<string, string>
        {
            [$"grant:{Auth}:members.read"] = "Full",
        });

        Where(posted).ShouldBe($"/console/{Acme}/roles?notice=saved");
        var call = api.SaveRoleCalls.ShouldHaveSingleItem();
        call.Method.ShouldBe("PATCH");
        call.Path.ShouldBe($"/api/auth/roles/{AdminRole}");
        call.Body.ShouldContain("\"name\":\"Administrators\"");
        call.Body.ShouldContain($"\"applicationId\":\"{Auth}\"");
    }

    [Fact]
    public async Task ARoleWithoutANameIsRefusedBeforeTheApiIsAsked()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await SaveAsync(client, NewPath, "   ", new Dictionary<string, string> { [$"grant:{Auth}:members.read"] = "ReadOnly" });
        var html = await posted.Content.ReadAsStringAsync(Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Fix 1 problem to save");
        html.ShouldContain("Enter a name for the role.");
        html.ShouldContain("""aria-invalid="true" """.TrimEnd());
        Regex.IsMatch(html, $"""<select[^>]*name="grant:{Auth}:members.read"[^>]*>.*?<option value="ReadOnly" selected""", RegexOptions.Singleline).ShouldBeTrue("the choices made are kept");
        api.SaveRoleCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ANameAlreadyUsedIsExplainedBesideTheName()
    {
        var api = Api();
        api.SaveRoleResponse = _ => Problem(HttpStatusCode.Conflict, "role_name_taken");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await (await SaveAsync(client, NewPath, "Admin", [])).Content.ReadAsStringAsync(Token);

        html.ShouldContain("A role with this name already exists. Choose a different name.");
        html.ShouldContain("""value="Admin" """.TrimEnd());
    }

    [Fact]
    public async Task TheApisOwnReasonsForRefusingAreShown()
    {
        var api = Api();
        api.SaveRoleResponse = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":400,"title":"x","errors":{"Permissions":["A role can grant at most 500 permissions."]}}""", Encoding.UTF8, "application/problem+json"),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await (await SaveAsync(client, NewPath, "Support", [])).Content.ReadAsStringAsync(Token);

        html.ShouldContain("A role can grant at most 500 permissions.");
    }

    [Fact]
    public async Task APermissionOfAProductThatIsNoLongerActiveIsFlaggedAndSavingRemovesIt()
    {
        var api = Api();
        api.RolesJson = RolesJson(extraRetiredGrant: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(EditPath(AdminRole), Token);

        html.ShouldContain("Some permissions are for products that aren&#x27;t active");
        html.ShouldContain("retired isn&#x27;t active for Acme Ltd any more");
        html.ShouldContain("Saving removes them from this role.");
    }

    [Fact]
    public async Task TheOwnerRoleCannotBeEdited()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(EditPath(OwnerRole), Token);

        html.ShouldContain("The Owner role can't be changed");
        html.ShouldNotContain("name=\"grant:");
    }

    [Fact]
    public async Task ARoleThatNoLongerExistsSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(EditPath(Guid.NewGuid()), Token);

        html.ShouldContain("That role isn't here");
        html.ShouldNotContain("name=\"name\"");
    }

    [Fact]
    public async Task SomeoneWithoutPermissionToChangeRolesIsToldWhichOneIsNeeded()
    {
        var api = Api();
        api.ConsolePermissions = ["roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(NewPath, Token);

        html.ShouldContain("You can&#x27;t change roles");
        html.ShouldContain("You need the 'Roles · Full access' permission.");
        html.ShouldNotContain("name=\"name\"");
        api.SaveRoleCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedWhileSavingKeepsTheFormAndSaysNothingWasSaved()
    {
        var api = Api();
        api.SaveRoleResponse = _ => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await (await SaveAsync(client, NewPath, "Support", new Dictionary<string, string> { [$"grant:{Auth}:members.read"] = "ReadOnly" })).Content.ReadAsStringAsync(Token);

        html.ShouldContain("We couldn&#x27;t save the role");
        html.ShouldContain("Nothing was saved.");
        html.ShouldContain("""value="Support" """.TrimEnd());
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ACatalogThatCannotBeLoadedShowsACalmError()
    {
        var api = Api();
        api.CatalogJson = "not json";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(NewPath, Token);

        html.ShouldContain("We couldn&#x27;t load this page");
        html.ShouldContain("Your data is safe.");
    }

    [Fact]
    public async Task ASaveWithoutTheAntiForgeryTokenChangesNothing()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(NewPath, Token);

        var posted = await client.PostAsync(NewPath, new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "role", ["name"] = "Sneaky" }), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.SaveRoleCalls.ShouldBeEmpty();
    }

    /// <summary>Opens the editor and posts it the way a browser would: the name, and a level for every permission (No access unless chosen).</summary>
    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, string path, string name, Dictionary<string, string> levels)
    {
        var page = await client.GetStringAsync(path, Token);
        var fields = new List<KeyValuePair<string, string>>();
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("name=\"_handler\""));
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        fields.Add(new("name", name));
        foreach (Match select in Regex.Matches(form, "<select[^>]*name=\"([^\"]+)\""))
        {
            var field = select.Groups[1].Value;
            fields.Add(new(field, levels.TryGetValue(field, out var level) ? level : "None"));
        }

        return await client.PostAsync(path, new FormUrlEncodedContent(fields), Token);
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        RolesJson = RolesJson(extraRetiredGrant: false),
        CatalogJson = $$"""
            {"levels":["ReadOnly","CreateRead","Full"],"applications":[
              {"applicationId":"{{Auth}}","key":"auth-service","name":"Auth Service","isBuiltIn":true,"permissions":[
                {"key":"members.read","label":"See members","description":"Who is in the organisation"},
                {"key":"roles.read","label":"See roles","description":null}]},
              {"applicationId":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","isBuiltIn":false,"permissions":[
                {"key":"tickets.reply","label":"Answer tickets","description":null},
                {"key":"tickets.close","label":"Close tickets","description":null}]}]}
            """,
    };

    private static string RolesJson(bool extraRetiredGrant) => $$"""
        [
          {"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false,"memberCount":2,"permissions":[
            {"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"members.read","level":"Full"}
            {{(extraRetiredGrant ? $$""",{"applicationId":"{{Retired}}","applicationKey":"retired","permission":"x.read","level":"Full"}""" : "")}}
          ],"createdAt":"2026-09-01T10:00:00Z"},
          {"id":"{{OwnerRole}}","name":"Owner","isBuiltIn":true,"memberCount":1,"permissions":[],"createdAt":"2026-08-01T10:00:00Z"}
        ]
        """;

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = NewClient(factory);
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
