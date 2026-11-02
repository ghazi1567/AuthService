using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>Rotating and revoking API keys (UI03_T2): the choices, the new key shown once, how long the old one lives, and every way the API can say no. No real API is involved.</summary>
public sealed class ConsoleApiKeysRotateRevokeTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Active = Guid.Parse("90000000-0000-4000-8000-000000000001");
    private static readonly Guid Expiring = Guid.Parse("90000000-0000-4000-8000-000000000002");
    private static readonly Guid Expired = Guid.Parse("90000000-0000-4000-8000-000000000003");
    private static readonly Guid Revoked = Guid.Parse("90000000-0000-4000-8000-000000000004");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/api-keys";

    [Fact]
    public async Task OnlyKeysThatCanStillBeActedOnGetRowActions()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"{PagePath}?rotate={Active}\"");
        html.ShouldContain($"href=\"{PagePath}?revoke={Active}\"");
        html.ShouldContain($"href=\"{PagePath}?revoke={Expiring}\"");
        html.ShouldNotContain($"?rotate={Expiring}", customMessage: "a key already being rotated out is not rotated again");
        foreach (var done in new[] { Expired, Revoked })
        {
            html.ShouldNotContain($"?rotate={done}");
            html.ShouldNotContain($"?revoke={done}");
        }
    }

    [Fact]
    public async Task AKeyBeingRotatedOutSaysWhenItStopsAndHowLongIsLeft()
    {
        await using var factory = new WebAppFactory(Api(), time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Stops 5 Nov, 10:00 UTC (in 2 days)");
    }

    [Fact]
    public async Task TheRotateDialogAsksHowLongTheOldKeyKeepsWorkingAndSuggestsADay()
    {
        await using var factory = new WebAppFactory(Api(), time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?rotate={Active}", Token);

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Rotate &#x27;CI deploy key&#x27;?");
        html.ShouldContain("How long should the old key keep working?");
        var select = Regex.Match(html, "<select[^>]*name=\"graceHours\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Not at all");
        select.ShouldContain("30 days");
        Regex.IsMatch(select, "<option[^>]*value=\"24\"[^>]*selected|<option[^>]*selected[^>]*value=\"24\"").ShouldBeTrue("24 hours is preselected");
        html.ShouldContain("Rotate key");
    }

    [Fact]
    public async Task RotatingSendsTheChosenGraceAsTheAdminAndShowsTheNewKeyOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostDialogAsync(client, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "72" });

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var call = api.RotateKeyCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/apikeys/{Active}/rotate");
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Body.ShouldContain("\"graceHours\":72");

        var revealUrl = Where(posted);
        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}&rotated=1&oldEnds=");
        revealUrl.ShouldNotContain(api.RotatedRawKey, customMessage: "the secret is never in an address");
        posted.Headers.ToString().ShouldNotContain(api.RotatedRawKey);

        var first = WebUtility.HtmlDecode(await client.GetStringAsync(revealUrl, Token));
        first.ShouldContain("Your rotated API key");
        first.ShouldContain($"value=\"{api.RotatedRawKey}\"");
        first.ShouldContain("You won't see this again");
        first.ShouldContain("The old key keeps working for a while");
        first.ShouldContain("It stops working at 5 Nov 2026, 12:00 UTC, in 3 days.");

        var reloaded = await client.GetStringAsync(revealUrl, Token);
        reloaded.ShouldNotContain(api.RotatedRawKey);
        reloaded.ShouldNotContain("Your rotated API key");
    }

    [Fact]
    public async Task ChoosingNotAtAllSaysTheOldKeyStoppedAtOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostDialogAsync(client, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "0" });

        api.RotateKeyCalls.ShouldHaveSingleItem().Body.ShouldContain("\"graceHours\":0");
        Where(posted).ShouldContain("oldEnds=now");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The old key stopped working at once");
        html.ShouldContain(api.RotatedRawKey);
    }

    [Fact]
    public async Task AnotherSessionCannotTakeTheRotatedKey()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var admin = await SignedInAsync(factory);
        using var other = await SignedInAsync(factory);

        var posted = await PostDialogAsync(admin, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "24" });

        (await other.GetStringAsync(Where(posted), Token)).ShouldNotContain(api.RotatedRawKey);
        (await admin.GetStringAsync(Where(posted), Token)).ShouldContain(api.RotatedRawKey);
    }

    [Fact]
    public async Task NeitherKeyIsEverLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now), logs: logs);
        using var client = await SignedInAsync(factory);

        var posted = await PostDialogAsync(client, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "24" });
        await client.GetStringAsync(Where(posted), Token);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(api.RotatedRawKey);
        everything.ShouldNotContain(api.ProductTokensIssued[0]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("721")]
    [InlineData("soon")]
    [InlineData("")]
    public async Task AGraceOutsideTheOfferedRangeNeverReachesTheApi(string grace)
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostDialogAsync(client, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = grace });

        api.RotateKeyCalls.ShouldBeEmpty();
        Where(posted).ShouldBe($"{PagePath}?error=bad_grace&m={Active}");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("Choose how long the old key keeps working");
    }

    [Fact]
    public async Task RevokingAsksFirstThenDeletesTheKeyAsTheAdmin()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?revoke={Active}", Token));
        dialog.ShouldContain("Revoke 'CI deploy key'?");
        dialog.ShouldContain("Any software using this key will stop working immediately. This can't be undone.");
        dialog.ShouldContain("Revoke key");
        api.RevokeKeyCalls.ShouldBeEmpty();

        var posted = await PostDialogAsync(client, $"?revoke={Active}", "/revoke", []);

        var call = api.RevokeKeyCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/apikeys/{Active}");
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        Where(posted).ShouldBe($"{PagePath}?notice=revoked&m={Active}");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("CI deploy key is revoked");
        html.ShouldContain("Software using it has stopped working.");
    }

    [Fact]
    public async Task AKeyBeingRotatedOutCanStillBeRevokedEarly()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync($"{PagePath}?revoke={Expiring}", Token)).ShouldContain("role=\"dialog\"");
        await PostDialogAsync(client, $"?revoke={Expiring}", "/revoke", []);

        api.RevokeKeyCalls.ShouldHaveSingleItem().Path.ShouldBe($"/api/auth/apikeys/{Expiring}");
    }

    [Fact]
    public async Task DialogsForKeysThatAreDoneShowNothing()
    {
        await using var factory = new WebAppFactory(Api(), time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        foreach (var url in new[] { $"?rotate={Expiring}", $"?rotate={Expired}", $"?rotate={Revoked}", $"?revoke={Expired}", $"?revoke={Revoked}", $"?revoke={Guid.NewGuid()}" })
        {
            (await client.GetStringAsync(PagePath + url, Token)).ShouldNotContain("role=\"dialog\"", customMessage: url);
        }
    }

    [Fact]
    public async Task TheApisOwnReasonsAreExplainedInPlainWords()
    {
        var notActive = Api();
        notActive.RotateKeyResponse = _ => Problem(HttpStatusCode.Conflict, "api_key_not_active");
        var gone = Api();
        gone.RevokeKeyResponse = () => Problem(HttpStatusCode.NotFound, "not_found");
        var refused = Api();
        refused.RevokeKeyResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.RotateKeyResponse = _ => throw new HttpRequestException("connection refused");
        await using var notActiveFactory = new WebAppFactory(notActive, time: new FakeClock(Now));
        await using var goneFactory = new WebAppFactory(gone, time: new FakeClock(Now));
        await using var refusedFactory = new WebAppFactory(refused, time: new FakeClock(Now));
        await using var downFactory = new WebAppFactory(down, time: new FakeClock(Now));
        using var notActiveClient = await SignedInAsync(notActiveFactory);
        using var goneClient = await SignedInAsync(goneFactory);
        using var refusedClient = await SignedInAsync(refusedFactory);
        using var downClient = await SignedInAsync(downFactory);

        var notActiveHtml = await PageAfterAsync(notActiveClient, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "24" });
        var goneHtml = await PageAfterAsync(goneClient, $"?revoke={Active}", "/revoke", []);
        var refusedHtml = await PageAfterAsync(refusedClient, $"?revoke={Active}", "/revoke", []);
        var downHtml = await PageAfterAsync(downClient, $"?rotate={Active}", "/rotate", new() { ["graceHours"] = "24" });

        notActiveHtml.ShouldContain("CI deploy key can't be rotated");
        notActiveHtml.ShouldContain("Create a new key instead.");
        goneHtml.ShouldContain("That key isn't here any more");
        refusedHtml.ShouldContain("You don't have permission to do that");
        downHtml.ShouldContain("Auth Service isn't responding right now. Nothing was changed.");
        downHtml.ShouldNotContain("connection refused");
        downHtml.ShouldNotContain(down.RotatedRawKey);
    }

    [Fact]
    public async Task WithoutPermissionToManageKeysTheRowsOfferNothingAndForcedDialogsShowNothing()
    {
        var api = Api();
        api.ConsolePermissions = ["apikeys.read:ReadOnly", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldNotContain("?rotate=");
        html.ShouldNotContain("?revoke=");
        html.ShouldContain("to rotate or revoke keys.");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>\\s*Revoke\\s*</button>").ShouldBeTrue();
        (await client.GetStringAsync($"{PagePath}?rotate={Active}", Token)).ShouldNotContain("role=\"dialog\"");
        (await client.GetStringAsync($"{PagePath}?revoke={Active}", Token)).ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsSent()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var rotate = await client.PostAsync($"{PagePath}/{Active}/rotate",new FormUrlEncodedContent([new("graceHours", "24")]), Token);
        var revoke = await client.PostAsync($"{PagePath}/{Active}/revoke", new FormUrlEncodedContent([]), Token);

        rotate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        revoke.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.RotateKeyCalls.ShouldBeEmpty();
        api.RevokeKeyCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWhileSignedOutSendsThePersonToSignIn()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = NewClient(factory);

        var posted = await client.PostAsync($"{PagePath}/{Active}/revoke", new FormUrlEncodedContent([]), Token);

        posted.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        api.RevokeKeyCalls.ShouldBeEmpty();
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>Opens a dialog, posts its form as a browser would, and returns the page the person lands on after the redirect.</summary>
    private static async Task<string> PageAfterAsync(HttpClient client, string query, string action, Dictionary<string, string> fields)
    {
        var posted = await PostDialogAsync(client, query, action, fields);
        return WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
    }

    private static async Task<HttpResponseMessage> PostDialogAsync(HttpClient client, string query, string action, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(PagePath + query, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains(action + "\""));
        var target = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]+)\"").Groups[1].Value);
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        body.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync(target, new FormUrlEncodedContent(body), Token);
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        ApiKeysJson = $$"""
            [
              {"id":"{{Active}}","name":"CI deploy key","maskedKey":"ask_live_7f3a…c9","applicationId":null,"applicationKey":null,"permissions":[{"permission":"audit.read","level":"Full"}],"status":"Active","createdAt":"2026-10-01T10:00:00Z","expiresAt":null,"revokedAt":null},
              {"id":"{{Expiring}}","name":"Old reporting","maskedKey":"ask_live_11aa…02","applicationId":null,"applicationKey":null,"permissions":[{"permission":"audit.read","level":"ReadOnly"}],"status":"Expiring","createdAt":"2026-09-01T10:00:00Z","expiresAt":"2026-11-05T10:00:00Z","revokedAt":null},
              {"id":"{{Expired}}","name":"Lapsed","maskedKey":"ask_live_22bb…03","applicationId":null,"applicationKey":null,"permissions":[],"status":"Expired","createdAt":"2026-08-01T10:00:00Z","expiresAt":"2026-10-01T10:00:00Z","revokedAt":null},
              {"id":"{{Revoked}}","name":"Leaked","maskedKey":"ask_live_33cc…04","applicationId":null,"applicationKey":null,"permissions":[],"status":"Revoked","createdAt":"2026-07-01T10:00:00Z","expiresAt":null,"revokedAt":"2026-07-02T10:00:00Z"}
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
