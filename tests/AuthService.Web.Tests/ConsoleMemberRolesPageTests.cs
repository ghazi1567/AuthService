using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's role assignment (UI02_T6): replacing the roles a member holds. No real API is involved.</summary>
public sealed class ConsoleMemberRolesPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Anna = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid Priya = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid OwnerRole = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid ViewerRole = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath(Guid member) => $"/console/{Acme}/members/{member}/roles";

    [Fact]
    public async Task TheMembersListOffersARolesActionAndNamesThePermissionWhenItIsMissing()
    {
        var allowed = Api(callerIsOwner: true);
        var denied = Api(callerIsOwner: true);
        denied.ConsolePermissions = ["members.read:ReadOnly"];
        await using var allowedFactory = new WebAppFactory(allowed);
        await using var deniedFactory = new WebAppFactory(denied);
        using var allowedClient = await SignedInAsync(allowedFactory);
        using var deniedClient = await SignedInAsync(deniedFactory);

        var forAllowed = await allowedClient.GetStringAsync($"/console/{Acme}/members", Token);
        var forDenied = await deniedClient.GetStringAsync($"/console/{Acme}/members", Token);

        forAllowed.ShouldContain($"""href="/console/{Acme}/members/{Priya}/roles">Roles</a>""");
        forDenied.ShouldNotContain($"/members/{Priya}/roles\"");
        forDenied.ShouldContain("permission to change someone");
    }

    [Fact]
    public async Task ThePageShowsTheCurrentRolesTheLimitsAndTheDelay()
    {
        var api = Api(callerIsOwner: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath(Priya), Token);

        html.ShouldContain("Roles for Priya Shah");
        html.ShouldContain("Choose between 1 and 10 roles.");
        html.ShouldContain("within about 15 minutes");
        Regex.IsMatch(html, $"""<input[^>]*value="{AdminRole}"[^>]*checked""").ShouldBeTrue("her current role is ticked");
        Regex.IsMatch(html, $"""<input[^>]*value="{ViewerRole}"[^>]*checked""").ShouldBeFalse();
        html.ShouldContain("data-unsaved-guard");
    }

    [Fact]
    public async Task SavingReplacesTheRolesAsTheAdminAndLeadsBackToTheMembers()
    {
        var api = Api(callerIsOwner: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await SaveAsync(client, Priya, [AdminRole, ViewerRole]);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"/console/{Acme}/members?notice=roles&m={Priya}");
        var call = api.AssignRolesCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/tenants/me/members/{Priya}/roles");
        call.Body.ShouldContain(AdminRole.ToString());
        call.Body.ShouldContain(ViewerRole.ToString());
        var list = await client.GetStringAsync(Where(posted), Token);
        list.ShouldContain("roles are updated");
        list.ShouldContain("within about 15 minutes");
    }

    [Fact]
    public async Task NoRolesAtAllIsRefusedBeforeTheApiIsAskedAndTheAdminIsPointedToDisabling()
    {
        var api = Api(callerIsOwner: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await (await SaveAsync(client, Priya, [])).Content.ReadAsStringAsync(Token);

        html.ShouldContain("Choose between 1 and 10 roles");
        html.ShouldContain("To remove their access, disable them instead.");
        api.AssignRolesCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheOwnerRoleIsOnlyForAnOwnerToGiveOrRemove()
    {
        var notOwner = Api(callerIsOwner: false);
        await using var factory = new WebAppFactory(notOwner);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath(Priya), Token);

        Regex.IsMatch(html, $"""<input[^>]*value="{OwnerRole}"[^>]*disabled""").ShouldBeTrue();
        html.ShouldContain("Only an Owner can give or remove the Owner role.");
    }

    [Fact]
    public async Task ARoleTheAdminCannotChangeIsSentAlongUnchangedSoItIsNotRemoved()
    {
        var notOwner = Api(callerIsOwner: false);
        // Priya is an Owner here; Anna, who is editing, is only an Admin.
        notOwner.MembersJson = notOwner.MembersJson.Replace($"\"id\":\"{AdminRole}\",\"name\":\"Admin\"}}],\"applications\":[],\"createdAt\":\"2026-10-01", $"\"id\":\"{OwnerRole}\",\"name\":\"Owner\"}}],\"applications\":[],\"createdAt\":\"2026-10-01");
        await using var factory = new WebAppFactory(notOwner);
        using var client = await SignedInAsync(factory);

        var page = await client.GetStringAsync(PagePath(Priya), Token);
        var posted = await SaveAsync(client, Priya, [ViewerRole]);

        // The Owner role is locked, ticked, and carried as a hidden field.
        Regex.IsMatch(page, $"""<input[^>]*type="hidden"[^>]*value="{OwnerRole}"|<input[^>]*value="{OwnerRole}"[^>]*type="hidden""").ShouldBeTrue();
        notOwner.AssignRolesCalls.ShouldHaveSingleItem().Body.ShouldContain(OwnerRole.ToString());
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "roles_required", "A member needs a role")]
    [InlineData(HttpStatusCode.Forbidden, "owner_role_restricted", "Only an Owner can give or remove the Owner role.")]
    [InlineData(HttpStatusCode.Conflict, "last_owner", "Acme Ltd needs at least one Owner. Make someone else an Owner first.")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "isn't here any more")]
    [InlineData(HttpStatusCode.Forbidden, "other", "'Roles · Full access' permission to change someone's roles.")]
    public async Task EachRefusalIsExplainedInPlainWordsAndTheChoicesAreKept(HttpStatusCode status, string code, string expected)
    {
        var api = Api(callerIsOwner: true);
        api.AssignRolesResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await SaveAsync(client, Priya, [ViewerRole]);
        var html = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync(Token));

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain(expected);
        Regex.IsMatch(html, $"""<input[^>]*value="{ViewerRole}"[^>]*checked""").ShouldBeTrue("what was chosen stays chosen");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedSaysNothingWasChanged()
    {
        var api = Api(callerIsOwner: true);
        api.AssignRolesResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await (await SaveAsync(client, Priya, [ViewerRole])).Content.ReadAsStringAsync(Token));

        html.ShouldContain("Auth Service isn't responding right now. Nothing was changed.");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task SomeoneWithoutPermissionToAssignRolesIsToldWhichOneIsNeeded()
    {
        var api = Api(callerIsOwner: true);
        api.ConsolePermissions = ["members.read:ReadOnly", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath(Priya), Token);

        html.ShouldContain("You can&#x27;t change roles");
        html.ShouldContain("You need the 'Roles · Full access' permission");
        html.ShouldNotContain("name=\"roleId\"");
    }

    [Fact]
    public async Task AMemberWhoIsNotInTheOrganisationSaysSo()
    {
        var api = Api(callerIsOwner: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath(Guid.NewGuid()), Token);

        html.ShouldContain("That member isn't here");
        html.ShouldNotContain("name=\"roleId\"");
    }

    [Fact]
    public async Task ASaveWithoutTheAntiForgeryTokenChangesNothing()
    {
        var api = Api(callerIsOwner: true);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath(Priya), Token);

        var posted = await client.PostAsync(PagePath(Priya), new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "assign", ["roleId"] = ViewerRole.ToString() }), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.AssignRolesCalls.ShouldBeEmpty();
    }

    /// <summary>Opens the page and posts it as a browser would: its hidden fields, then each ticked role.</summary>
    private static async Task<HttpResponseMessage> SaveAsync(HttpClient client, Guid member, Guid[] tickedRoles)
    {
        var page = await client.GetStringAsync(PagePath(member), Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("name=\"_handler\""));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        // A disabled box is not sent by a browser; ticked ones are. Hidden roleId fields above already carry locked roles.
        foreach (var role in tickedRoles)
        {
            if (!fields.Any(field => field.Key == "roleId" && field.Value == role.ToString()))
            {
                fields.Add(new("roleId", role.ToString()));
            }
        }

        return await client.PostAsync(PagePath(member), new FormUrlEncodedContent(fields), Token);
    }

    private static FakeAuthApi Api(bool callerIsOwner) => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = $$"""
            [
              {"membershipId":"{{Anna}}","email":"anna@acme.com","displayName":"Anna Kowalski","status":"Active","roles":[{"id":"{{(callerIsOwner ? OwnerRole : AdminRole)}}","name":"{{(callerIsOwner ? "Owner" : "Admin")}}"}],"applications":[],"createdAt":"2026-09-01T10:00:00Z"},
              {"membershipId":"{{Priya}}","email":"priya@acme.com","displayName":"Priya Shah","status":"Active","roles":[{"id":"{{AdminRole}}","name":"Admin"}],"applications":[],"createdAt":"2026-10-01T10:00:00Z"}
            ]
            """,
        RolesJson = $$"""
            [
              {"id":"{{OwnerRole}}","name":"Owner","isBuiltIn":true,"memberCount":1},
              {"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false,"memberCount":2},
              {"id":"{{ViewerRole}}","name":"Viewer","isBuiltIn":false,"memberCount":5}
            ]
            """,
    };

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
