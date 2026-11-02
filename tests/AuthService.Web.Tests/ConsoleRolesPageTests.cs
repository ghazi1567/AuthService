using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's roles list (UI02_T4): what roles exist, who holds them, and deleting one nobody holds. No real API is involved.</summary>
public sealed class ConsoleRolesPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid OwnerRole = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid ViewerRole = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid Auth = Guid.Parse("00000000-0000-7000-8000-00000000a07a");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/roles";

    [Fact]
    public async Task RolesAreListedWithMembersAndAPermissionSummaryAndTheOwnerRoleIsLocked()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("<title>Roles · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Roles</h1>""");
        html.ShouldContain("Owner");
        html.ShouldContain("Built in");
        html.ShouldContain("11 permissions in 1 product");
        html.ShouldContain("3 permissions in 2 products");
        html.ShouldContain("No permissions");
        html.ShouldContain("Showing 4 roles");
        html.IndexOf("person-name\">Owner", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("person-name\">Admin", StringComparison.Ordinal), "the built-in role comes first");
        html.ShouldContain("The built-in Owner role can&#x27;t be edited or deleted.");
        html.ShouldContain($"""href="/console/{Acme}/roles/new">""");
        html.ShouldContain($"""href="/console/{Acme}/roles/{AdminRole}">Edit</a>""");
        api.RoleDeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARoleNobodyHoldsAsksBeforeItIsDeletedAndSaysWhatItDoes()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?delete={Guid.Parse("10000000-0000-4000-8000-000000000004")}", Token);

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Delete the role &#x27;Spare&#x27;?");
        html.ShouldContain("Nobody holds this role, so no one will lose access.");
        html.ShouldContain("This can't be undone.");
        html.ShouldContain("btn btn-danger");
    }

    [Fact]
    public async Task ARoleStillHeldCannotBeDeletedAndTheDialogSaysWhatToDoNext()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?delete={ViewerRole}", Token);

        html.ShouldContain("Viewer can&#x27;t be deleted yet");
        html.ShouldContain("5 people still hold this role.");
        html.ShouldContain("Give them a different role first");
        html.ShouldContain($"""href="/console/{Acme}/members">Go to members</a>""");
        html.ShouldNotContain("Delete role</button>");
    }

    [Fact]
    public async Task ConfirmingDeleteDeletesAsTheAdminAndSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var spare = Guid.Parse("10000000-0000-4000-8000-000000000004");
        var dialog = await client.GetStringAsync($"{PagePath}?delete={spare}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/roles/{spare}/delete");

        Where(posted).ShouldBe($"{PagePath}?notice=deleted");
        var call = api.RoleDeleteCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/roles/{spare}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Role deleted");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "role_in_use", "role_in_use", "is still in use")]
    [InlineData(HttpStatusCode.Forbidden, "owner_role_immutable", "owner_role_immutable", "The built-in Owner role can never be edited, downgraded or deleted.")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "not_found", "Someone may have deleted it.")]
    [InlineData(HttpStatusCode.Forbidden, "other", "forbidden", "permission to change roles.")]
    public async Task EachRefusalFromTheApiIsExplainedInPlainWords(HttpStatusCode status, string code, string expectedCode, string expected)
    {
        var api = Api();
        api.RoleDeleteResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var spare = Guid.Parse("10000000-0000-4000-8000-000000000004");
        var dialog = await client.GetStringAsync($"{PagePath}?delete={spare}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/roles/{spare}/delete");

        Where(posted).ShouldBe($"{PagePath}?error={expectedCode}&m={spare}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain(expected);
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedForADeleteLeavesTheRoleAndSaysSo()
    {
        var api = Api();
        api.RoleDeleteResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var spare = Guid.Parse("10000000-0000-4000-8000-000000000004");
        var dialog = await client.GetStringAsync($"{PagePath}?delete={spare}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/roles/{spare}/delete");
        var html = await client.GetStringAsync(Where(posted), Token);

        html.ShouldContain("We couldn&#x27;t make that change");
        html.ShouldContain("Nothing was changed.");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ADeleteWithoutTheAntiForgeryTokenChangesNothing()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath, Token);

        var posted = await client.PostAsync(
            $"/console/{Acme}/roles/{AdminRole}/delete", new FormUrlEncodedContent(new Dictionary<string, string>()), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.RoleDeleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutPermissionToManageRolesEveryChangeIsDisabledWithTheReason()
    {
        var api = Api();
        api.ConsolePermissions = ["roles.read:ReadOnly", "members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("Admin");
        html.ShouldContain("permission to create roles.\"");
        html.ShouldContain("permission to change roles.\"");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Delete</button>").ShouldBeTrue();
        html.ShouldNotContain("?delete=");
        html.ShouldNotContain("roles/new");
    }

    [Fact]
    public async Task WithoutPermissionToReadRolesThePageSaysWhichPermissionIsNeeded()
    {
        var api = Api();
        api.ConsolePermissions = ["members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("You can&#x27;t see roles");
        html.ShouldContain("You need the 'Roles · Read only' permission.");
    }

    [Fact]
    public async Task ARoleListThatCannotBeLoadedShowsACalmError()
    {
        var api = Api();
        api.RolesJson = "not json";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t load the roles");
        html.ShouldContain("Your data is safe.");
    }

    [Fact]
    public async Task TheNavigationOffersRolesOnlyToSomeoneWhoMayReadThem()
    {
        var allowed = Api();
        var denied = Api();
        denied.ConsolePermissions = ["members.read:ReadOnly"];
        await using var allowedFactory = new WebAppFactory(allowed);
        await using var deniedFactory = new WebAppFactory(denied);
        using var allowedClient = await SignedInAsync(allowedFactory);
        using var deniedClient = await SignedInAsync(deniedFactory);

        var forAllowed = await allowedClient.GetStringAsync($"/console/{Acme}/members", Token);
        var forDenied = await deniedClient.GetStringAsync($"/console/{Acme}/members", Token);

        forAllowed.ShouldContain($"""href="/console/{Acme}/roles" """.TrimEnd());
        forDenied.ShouldNotContain($"""href="/console/{Acme}/roles" """.TrimEnd());
    }

    private static FakeAuthApi Api()
    {
        string Grants(int count) => string.Join(',', Enumerable.Range(0, count).Select(index =>
            $$"""{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"perm.{{index}}","level":"Full"}"""));
        return new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = "[]",
            RolesJson = $$"""
                [
                  {"id":"{{ViewerRole}}","name":"Viewer","isBuiltIn":false,"memberCount":5,"permissions":[{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"members.read","level":"ReadOnly"},{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"roles.read","level":"ReadOnly"},{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","permission":"tickets.read","level":"ReadOnly"}],"createdAt":"2026-09-01T10:00:00Z"},
                  {"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false,"memberCount":2,"permissions":[{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"members.read","level":"Full"},{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"roles.read","level":"Full"},{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","permission":"tickets.read","level":"Full"}],"createdAt":"2026-09-01T10:00:00Z"},
                  {"id":"10000000-0000-4000-8000-000000000004","name":"Spare","isBuiltIn":false,"memberCount":0,"permissions":[{"applicationId":"{{Auth}}","applicationKey":"auth-service","permission":"members.read","level":"None"}],"createdAt":"2026-09-01T10:00:00Z"},
                  {"id":"{{OwnerRole}}","name":"Owner","isBuiltIn":true,"memberCount":1,"permissions":[{{Grants(11)}}],"createdAt":"2026-08-01T10:00:00Z"}
                ]
                """,
        };
    }

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
