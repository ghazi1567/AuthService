using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's API keys (UI03_T1): the list, creating a key, and its value shown exactly once. No real API is involved.</summary>
public sealed class ConsoleApiKeysPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Auth = Guid.Parse("00000000-0000-7000-8000-00000000a07a");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/api-keys";

    [Fact]
    public async Task KeysAreListedMaskedWithTheirProductStatusAndDates()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("<title>API keys · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">API keys</h1>""");
        html.ShouldContain("CI deploy key");
        html.ShouldContain("ask_live_7f3a&#x2026;c9");
        html.ShouldContain("helpdesk");
        html.ShouldContain("Auth Service");
        html.ShouldContain("2 permissions");
        foreach (var status in new[] { "Active", "Expiring", "Expired", "Revoked" })
        {
            html.ShouldContain(status);
        }

        html.ShouldContain("1 Oct 2026");
        html.ShouldContain("Never");
        html.ShouldContain("Showing 4 keys");
        html.ShouldNotContain(api.RawKey);
    }

    [Fact]
    public async Task NoKeysShowsAnEmptyState()
    {
        var api = Api();
        api.ApiKeysJson = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("No API keys yet");
    }

    [Fact]
    public async Task TheCreateDialogOffersOnlyLevelsTheCreatorHolds()
    {
        var api = Api();
        api.ConsolePermissions = ["apikeys.read:Full", "apikeys.manage:Full", "roles.read:ReadOnly", "members.read:ReadOnly", "audit.read:Full"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?create=1", Token);

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Create an API key");
        html.ShouldContain("""<label for="key-name">Key name</label>""");
        html.ShouldContain("Auth Service itself");
        html.ShouldContain("Helpdesk");
        var members = Regex.Match(html, "<select[^>]*name=\"perm:members.read\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        members.ShouldContain("View only");
        members.ShouldNotContain("Full access", customMessage: "the creator holds members.read only at View only");
        var audit = Regex.Match(html, "<select[^>]*name=\"perm:audit.read\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        audit.ShouldContain("Full access");
        var roles = Regex.Match(html, "<select[^>]*name=\"perm:apikeys.manage\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        roles.ShouldContain("Full access");
    }

    [Fact]
    public async Task ChoosingAProductShowsThatProductsPermissions()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?create=1&app={Helpdesk}", Token);

        html.ShouldContain("name=\"perm:tickets.reply\"");
        html.ShouldNotContain("name=\"perm:members.read\"");
        html.ShouldContain($"""name="app" value="{Helpdesk}" """.TrimEnd());
    }

    [Fact]
    public async Task CreatingAKeySendsItsNameProductAndPermissionsAsTheAdmin()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostCreateAsync(client, "?create=1", "  Reporting job  ", new Dictionary<string, string> { ["perm:members.read"] = "ReadOnly", ["perm:audit.read"] = "Full" });

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var call = api.CreateKeyCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Body.ShouldContain("\"name\":\"Reporting job\"");
        call.Body.ShouldContain("\"applicationId\":null");
        call.Body.ShouldContain("{\"permission\":\"members.read\",\"level\":\"ReadOnly\"}");
        call.Body.ShouldContain("{\"permission\":\"audit.read\",\"level\":\"Full\"}");
        call.Body.ShouldNotContain("roles.read", customMessage: "a permission left at no access is not sent");
    }

    [Fact]
    public async Task ANewKeyIsShownOnceWithAWarningAndAReloadNeverShowsItAgain()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostCreateAsync(client, "?create=1", "Reporting job", new Dictionary<string, string> { ["perm:members.read"] = "ReadOnly" });
        var revealUrl = Where(posted);

        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}$");
        revealUrl.ShouldNotContain(api.RawKey, customMessage: "the secret is never in an address");
        posted.Headers.ToString().ShouldNotContain(api.RawKey);

        var first = await client.GetStringAsync(revealUrl, Token);
        first.ShouldContain("Your new API key");
        first.ShouldContain("You won&#x27;t see this again");
        first.ShouldContain($"value=\"{api.RawKey}\"");
        first.ShouldContain("readonly");
        first.ShouldContain("I&#x27;ve saved it", Case.Insensitive);
        first.ShouldContain("data-copy-from=\"raw-key\"");
        Regex.IsMatch(first, "<button[^>]*data-needs-script[^>]*hidden|<button[^>]*hidden[^>]*data-needs-script").ShouldBeTrue("the copy button only appears once the script has loaded");
        first.ShouldNotContain("aria-label=\"Close\"");

        var reloaded = await client.GetStringAsync(revealUrl, Token);
        reloaded.ShouldNotContain(api.RawKey);
        reloaded.ShouldNotContain("Your new API key");
        (await client.GetStringAsync(PagePath, Token)).ShouldNotContain(api.RawKey);
    }

    [Fact]
    public async Task ANewKeyCannotBeTakenBySomeoneElse()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var maker = await SignedInAsync(factory);
        using var other = await SignedInAsync(factory);

        var posted = await PostCreateAsync(maker, "?create=1", "Reporting job", new Dictionary<string, string> { ["perm:members.read"] = "ReadOnly" });

        var stolen = await other.GetStringAsync(Where(posted), Token);
        stolen.ShouldNotContain(api.RawKey);
        (await maker.GetStringAsync(Where(posted), Token)).ShouldContain(api.RawKey, customMessage: "the person who made it still can");
    }

    [Fact]
    public async Task TheKeyAndItsValueAreNeverLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, logs: logs);
        using var client = await SignedInAsync(factory);

        var posted = await PostCreateAsync(client, "?create=1", "Reporting job", new Dictionary<string, string> { ["perm:members.read"] = "ReadOnly" });
        await client.GetStringAsync(Where(posted), Token);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(api.RawKey);
        everything.ShouldNotContain(api.ProductTokensIssued[0]);
    }

    [Fact]
    public async Task ANameAndAtLeastOnePermissionAreNeededBeforeTheApiIsAsked()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await (await PostCreateAsync(client, "?create=1", "  ", new Dictionary<string, string>())).Content.ReadAsStringAsync(Token);

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("Enter a name for the key.");
        html.ShouldContain("Choose at least one permission for the key.");
        api.CreateKeyCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisOwnReasonsAndAnUnreachableApiAreExplainedAndTheChoicesKept()
    {
        var refused = Api();
        refused.CreateKeyResponse = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":400,"title":"x","errors":{"Name":["Name must be at most 100 characters."]}}""", Encoding.UTF8, "application/problem+json"),
        };
        var down = Api();
        down.CreateKeyResponse = _ => throw new HttpRequestException("connection refused");
        var forbidden = Api();
        forbidden.CreateKeyResponse = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        await using var refusedFactory = new WebAppFactory(refused);
        await using var downFactory = new WebAppFactory(down);
        await using var forbiddenFactory = new WebAppFactory(forbidden);
        using var refusedClient = await SignedInAsync(refusedFactory);
        using var downClient = await SignedInAsync(downFactory);
        using var forbiddenClient = await SignedInAsync(forbiddenFactory);
        var levels = new Dictionary<string, string> { ["perm:members.read"] = "ReadOnly" };

        var refusedHtml = await (await PostCreateAsync(refusedClient, "?create=1", "Reporting job", levels)).Content.ReadAsStringAsync(Token);
        var downHtml = WebUtility.HtmlDecode(await (await PostCreateAsync(downClient, "?create=1", "Reporting job", levels)).Content.ReadAsStringAsync(Token));
        var forbiddenHtml = WebUtility.HtmlDecode(await (await PostCreateAsync(forbiddenClient, "?create=1", "Reporting job", levels)).Content.ReadAsStringAsync(Token));

        refusedHtml.ShouldContain("Name must be at most 100 characters.");
        downHtml.ShouldContain("Auth Service isn't responding right now. Nothing was created.");
        downHtml.ShouldNotContain("connection refused");
        forbiddenHtml.ShouldContain("You don't have permission to create keys");
        refusedHtml.ShouldContain("value=\"Reporting job\"");
    }

    [Fact]
    public async Task WithoutPermissionToManageKeysCreatingIsDisabledAndAForcedDialogShowsNothing()
    {
        var api = Api();
        api.ConsolePermissions = ["apikeys.read:ReadOnly", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);
        var forced = await client.GetStringAsync($"{PagePath}?create=1", Token);

        html.ShouldContain("CI deploy key");
        html.ShouldContain("permission to create keys.\"");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>.*?Create API key</button>", RegexOptions.Singleline).ShouldBeTrue();
        html.ShouldNotContain("?create=1");
        forced.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task WithoutPermissionToReadKeysThePageSaysWhichPermissionIsNeeded()
    {
        var api = Api();
        api.ConsolePermissions = ["members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("You can&#x27;t see API keys");
        html.ShouldContain("'API keys · Read only' permission");
    }

    [Fact]
    public async Task AListTheApiRefusesReadsAsAPermissionMessageAndAFailureAsACalmError()
    {
        var refusing = Api();
        refusing.ApiKeysResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var failing = Api();
        failing.ApiKeysResponse = () => throw new HttpRequestException("connection refused");
        await using var refusingFactory = new WebAppFactory(refusing);
        await using var failingFactory = new WebAppFactory(failing);
        using var refusingClient = await SignedInAsync(refusingFactory);
        using var failingClient = await SignedInAsync(failingFactory);

        (await refusingClient.GetStringAsync(PagePath, Token)).ShouldContain("You can&#x27;t see API keys");
        var failed = await failingClient.GetStringAsync(PagePath, Token);
        failed.ShouldContain("We couldn&#x27;t load the API keys");
        failed.ShouldNotContain("connection refused");
    }

    private static async Task<HttpResponseMessage> PostCreateAsync(HttpClient client, string query, string name, Dictionary<string, string> levels)
    {
        var page = await client.GetStringAsync(PagePath + query, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("name=\"_handler\""));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        fields.Add(new("name", name));
        foreach (Match select in Regex.Matches(form, "<select[^>]*name=\"(perm:[^\"]+)\""))
        {
            var field = select.Groups[1].Value;
            fields.Add(new(field, levels.TryGetValue(field, out var level) ? level : "None"));
        }

        return await client.PostAsync(PagePath + query, new FormUrlEncodedContent(fields), Token);
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        CatalogJson = $$"""
            {"levels":["ReadOnly","CreateRead","Full"],"applications":[
              {"applicationId":"{{Auth}}","key":"auth-service","name":"Auth Service","isBuiltIn":true,"permissions":[
                {"key":"members.read","label":"See members","description":null},
                {"key":"audit.read","label":"See the audit log","description":null},
                {"key":"roles.read","label":"See roles","description":null},
                {"key":"apikeys.manage","label":"Manage API keys","description":null}]},
              {"applicationId":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","isBuiltIn":false,"permissions":[
                {"key":"tickets.reply","label":"Answer tickets","description":null}]}]}
            """,
        ApiKeysJson = $$"""
            [
              {"id":"90000000-0000-4000-8000-000000000001","name":"CI deploy key","maskedKey":"ask_live_7f3a…c9","applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","permissions":[{"permission":"tickets.reply","level":"Full"},{"permission":"tickets.close","level":"ReadOnly"}],"status":"Active","createdAt":"2026-10-01T10:00:00Z","expiresAt":null,"revokedAt":null},
              {"id":"90000000-0000-4000-8000-000000000002","name":"Old reporting","maskedKey":"ask_live_11aa…02","applicationId":null,"applicationKey":null,"permissions":[{"permission":"audit.read","level":"ReadOnly"}],"status":"Expiring","createdAt":"2026-09-01T10:00:00Z","expiresAt":"2026-11-05T10:00:00Z","revokedAt":null},
              {"id":"90000000-0000-4000-8000-000000000003","name":"Lapsed","maskedKey":"ask_live_22bb…03","applicationId":null,"applicationKey":null,"permissions":[],"status":"Expired","createdAt":"2026-08-01T10:00:00Z","expiresAt":"2026-10-01T10:00:00Z","revokedAt":null},
              {"id":"90000000-0000-4000-8000-000000000004","name":"Leaked","maskedKey":"ask_live_33cc…04","applicationId":null,"applicationKey":null,"permissions":[],"status":"Revoked","createdAt":"2026-07-01T10:00:00Z","expiresAt":null,"revokedAt":"2026-07-02T10:00:00Z"}
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

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
