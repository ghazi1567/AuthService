using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>
/// UI02_T8: across the whole tenant console, an admin who may only read is offered nothing that writes, a forced link or request does
/// nothing, and the API's refusal reads as a plain message. The server stays the authority; this is what the person is shown.
/// </summary>
public sealed class PermissionAwareConsoleTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Priya = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid Lena = Guid.Parse("c3333333-3333-4333-8333-333333333333");
    private static readonly Guid Sam = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] ReadOnlyPermissions =
        ["members.read:ReadOnly", "roles.read:ReadOnly", "access.manage:ReadOnly", "subscriptions.read:ReadOnly", "apikeys.read:ReadOnly", "audit.read:ReadOnly"];

    public static TheoryData<string> ConsolePages =>
    [
        $"/console/{Acme}/members",
        $"/console/{Acme}/invitations",
        $"/console/{Acme}/roles",
        $"/console/{Acme}/access",
    ];

    [Theory]
    [MemberData(nameof(ConsolePages))]
    public async Task AReadOnlyAdminIsOfferedNothingThatWrites(string path)
    {
        var api = Api(ReadOnlyPermissions);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(path, Token);

        // Every form that posts goes nowhere but sign-out, and no link leads to something that changes anything.
        foreach (Match form in Regex.Matches(html, "<form[^>]*method=\"post\"[^>]*>"))
        {
            form.Value.ShouldContain("action=\"/logout\"", customMessage: $"no write form on {path}: {form.Value}");
        }

        foreach (var write in new[] { "?disable=", "?delete=", "?revoke=", "?invite=1", "/roles/new", "/resend\"", "/status\"", "/grant\"", "/revoke\"", "/delete\"" })
        {
            html.ShouldNotContain(write, customMessage: $"{path} offers {write} to someone who may only read");
        }

        Regex.IsMatch(html, $"""<a[^>]*href="/console/{Acme}/members/[^"]+/roles"[^>]*>""").ShouldBeFalse("no link to change someone's roles");
        Regex.IsMatch(html, $$"""<a[^>]*href="/console/{{Acme}}/roles/[0-9a-f-]{36}"[^>]*>""").ShouldBeFalse("no link to edit a role");
    }

    [Fact]
    public async Task AnAdminWithOnlyMembersReadSeesTheListButNoInviteResendOrDisable()
    {
        var api = Api(["members.read:ReadOnly"]);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var members = await client.GetStringAsync($"/console/{Acme}/members", Token);
        var invitations = await client.GetStringAsync($"/console/{Acme}/invitations", Token);

        members.ShouldContain("Priya Shah");
        invitations.ShouldContain("sam@acme.com");
        Regex.IsMatch(members, "<button[^>]*disabled[^>]*>Disable</button>").ShouldBeTrue();
        Regex.IsMatch(members, "<button[^>]*disabled[^>]*>.*?Invite people</button>", RegexOptions.Singleline).ShouldBeTrue();
        Regex.IsMatch(invitations, "<button[^>]*disabled[^>]*>Resend</button>").ShouldBeTrue();
        members.ShouldContain("permission to manage members.");
        members.ShouldContain("permission to invite people.");
    }

    [Theory]
    [InlineData("members?disable=b2222222-2222-4222-8222-222222222222")]
    [InlineData("roles?delete=10000000-0000-4000-8000-000000000002")]
    [InlineData("access?revoke=b2222222-2222-4222-8222-222222222222&app=20000000-0000-4000-8000-000000000001")]
    [InlineData("invitations?invite=1")]
    public async Task AForcedConfirmationOrDialogIsNotShownToSomeoneWhoCannotAct(string relative)
    {
        var api = Api(ReadOnlyPermissions);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"/console/{Acme}/{relative}", Token);

        html.ShouldNotContain("role=\"dialog\"");
        html.ShouldNotContain("btn btn-danger");
    }

    [Theory]
    [InlineData("roles/new")]
    [InlineData("roles/10000000-0000-4000-8000-000000000002")]
    [InlineData("members/b2222222-2222-4222-8222-222222222222/roles")]
    public async Task AForcedEditorPageSaysWhichPermissionIsNeeded(string relative)
    {
        var api = Api(ReadOnlyPermissions);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"/console/{Acme}/{relative}", Token);

        html.ShouldContain("You can&#x27;t change roles");
        html.ShouldContain("Full access");
        html.ShouldNotContain("name=\"roleId\"");
        html.ShouldNotContain("name=\"name\"");
        html.ShouldNotContain("Save roles");
    }

    [Fact]
    public async Task AForcedRequestThatTheApiRefusesReadsAsAPlainMessageOnEveryPage()
    {
        var api = Api(ReadOnlyPermissions);
        api.StatusResponse = () => Refusal();
        api.ResendResponse = () => Refusal();
        api.RoleDeleteResponse = () => Refusal();
        api.AccessResponse = () => Refusal();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var token = await AntiforgeryFieldsAsync(client);

        var status = await client.PostAsync($"/console/{Acme}/members/{Priya}/status", Form(token, ("status", "Disabled")), Token);
        var resend = await client.PostAsync($"/console/{Acme}/invitations/{Sam}/resend", Form(token), Token);
        var delete = await client.PostAsync($"/console/{Acme}/roles/{AdminRole}/delete", Form(token), Token);
        var grant = await client.PostAsync($"/console/{Acme}/access/{Helpdesk}/grant", Form(token, ("membershipId", Priya.ToString())), Token);
        var revoke = await client.PostAsync($"/console/{Acme}/access/{Helpdesk}/{Priya}/revoke", Form(token), Token);

        foreach (var response in new[] { status, resend, delete, grant, revoke })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
            Where(response).ShouldContain("error=forbidden");
            var page = await client.GetStringAsync(Where(response), Token);
            page.ShouldContain("You don&#x27;t have permission to do that");
            page.ShouldContain("permission");
            page.ShouldNotContain("owner_role_restricted", customMessage: "no raw codes reach the page");
        }
    }

    [Theory]
    [InlineData("members")]
    [InlineData("invitations")]
    [InlineData("roles")]
    [InlineData("access")]
    public async Task AListTheApiRefusesToShowReadsAsAPermissionMessageNotAnError(string page)
    {
        var api = Api(["members.read:Full", "roles.read:Full", "access.manage:Full", "subscriptions.read:Full"]);
        api.MembersResponse = () => Refusal();
        api.PendingResponse = () => Refusal();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var json = page == "roles" ? "/api/auth/roles" : null;

        // The roles list has its own endpoint; make it refuse too.
        var html = page == "roles"
            ? await RolesRefusedAsync()
            : await client.GetStringAsync($"/console/{Acme}/{page}", Token);

        html.ShouldNotContain("Your data is safe.", customMessage: "a refusal is not a broken page");
        html.ShouldContain("You can&#x27;t see");

        async Task<string> RolesRefusedAsync()
        {
            api.RolesJson = "[]";
            var refused = new FakeAuthApi { MembershipsJson = api.MembershipsJson, ConsolePermissions = api.ConsolePermissions };
            await using var other = new WebAppFactory(new RefusingRolesApi(refused));
            using var otherClient = await SignedInAsync(other);
            return await otherClient.GetStringAsync($"/console/{Acme}/roles", Token);
        }
    }

    private static FakeAuthApi Api(string[] permissions) => new()
    {
        ConsolePermissions = permissions,
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = $$"""
            [
              {"membershipId":"{{Priya}}","email":"priya@acme.com","displayName":"Priya Shah","status":"Active","roles":[{"id":"{{AdminRole}}","name":"Admin"}],"applications":[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk"}],"createdAt":"2026-10-01T10:00:00Z"},
              {"membershipId":"{{Lena}}","email":"lena@acme.com","displayName":"Lena Fischer","status":"Disabled","roles":[],"applications":[],"createdAt":"2026-08-01T10:00:00Z"}
            ]
            """,
        RolesJson = $$"""[{"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false,"memberCount":1,"permissions":[],"createdAt":"2026-09-01T10:00:00Z"}]""",
        SubscriptionsJson = $$"""[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":5,"seatsUsed":1}]""",
        PendingJson = $$"""[{"membershipId":"{{Sam}}","email":"sam@acme.com","roles":[{"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false}],"applicationIds":[],"invitedBy":"Priya Shah","invitedAt":"2026-11-01T10:00:00Z","lastSentAt":"2026-11-01T10:00:00Z","expiresAt":"2026-11-09T10:00:00Z","isExpired":false}]""",
    };

    /// <summary>The anti-forgery fields of the sign-out form every console page has, so a forced post carries a valid token.</summary>
    private static async Task<List<KeyValuePair<string, string>>> AntiforgeryFieldsAsync(HttpClient client)
    {
        var page = await client.GetStringAsync($"/console/{Acme}/members", Token);
        var logout = Regex.Match(page, "<form[^>]*action=\"/logout\"[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline);
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(logout.Groups["body"].Value, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        return fields;
    }

    private static FormUrlEncodedContent Form(List<KeyValuePair<string, string>> token, params (string Key, string Value)[] more) =>
        new([.. token, .. more.Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value))]);

    private static HttpResponseMessage Refusal() => new(HttpStatusCode.Forbidden)
    {
        Content = new StringContent("""{"status":403,"title":"Forbidden","detail":"x","code":"forbidden"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = NewClient(factory);
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    /// <summary>Wraps the fake API and refuses the role list, which the fake has no switch for.</summary>
    private sealed class RefusingRolesApi(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.AbsolutePath == "/api/auth/roles" ? Task.FromResult(Refusal()) : base.SendAsync(request, cancellationToken);
    }
}
