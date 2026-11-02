using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's products (UI04_T2): the list, and replacing a product's client secret with the new one shown once. No real API is involved.</summary>
public sealed class PlatformProductsPageTests
{
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string PagePath = "/platform/products";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ProductsAreListedWithTheirKeyAndAReplaceSecretAction()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("<title>Products · Auth Service</title>");
        html.ShouldContain("Billing");
        html.ShouldContain("Helpdesk");
        html.ShouldContain("20000000");
        html.ShouldContain($"href=\"{PagePath}?rotate={Helpdesk}\"");
        html.ShouldContain($"href=\"{PagePath}?rotate={Billing}\"");
        html.ShouldContain("Showing 2 products");
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task NoProductsShowsAnEmptyState()
    {
        var api = Api();
        api.ProductsJson = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("No products yet");
    }

    [Fact]
    public async Task TheReplaceQuestionSaysWhatStopsWorkingAndSuggestsADay()
    {
        await using var factory = new WebAppFactory(Api(), time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?rotate={Helpdesk}", Token));

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Replace the client secret for 'Helpdesk'?");
        html.ShouldContain("Until the old secret stops, either works");
        html.ShouldContain("Choose \"Not at all\" if the old secret may have leaked");
        var select = Regex.Match(html, "<select[^>]*name=\"graceHours\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Not at all");
        select.ShouldContain("30 days");
        Regex.IsMatch(select, "<option[^>]*value=\"24\"[^>]*selected|<option[^>]*selected[^>]*value=\"24\"").ShouldBeTrue("24 hours is preselected");
        (await client.GetStringAsync($"{PagePath}?rotate={Guid.NewGuid()}", Token)).ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task ReplacingSendsTheChosenGraceAsTheAdminAndShowsTheNewSecretOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, "72");

        var call = api.SecretRotateCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/secret/rotate");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"graceHours\":72");

        var revealUrl = Where(posted);
        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}&app={Helpdesk}&client=helpdesk-client&oldEnds=");
        revealUrl.ShouldNotContain(api.ProductSecret, customMessage: "the secret is never in an address");
        posted.Headers.ToString().ShouldNotContain(api.ProductSecret);

        var first = WebUtility.HtmlDecode(await client.GetStringAsync(revealUrl, Token));
        first.ShouldContain("New client secret");
        first.ShouldContain($"value=\"{api.ProductSecret}\"");
        first.ShouldContain("You won't see this again");
        first.ShouldContain("Client id: <code class=\"mono\">helpdesk-client</code>");
        first.ShouldContain("The old secret keeps working for a while");
        first.ShouldContain("It stops working at 5 Nov 2026, 12:00 UTC, in 3 days.");
        first.ShouldContain("data-copy-from=\"raw-secret\"");

        var reloaded = await client.GetStringAsync(revealUrl, Token);
        reloaded.ShouldNotContain(api.ProductSecret);
        reloaded.ShouldNotContain("New client secret");
    }

    [Fact]
    public async Task ChoosingNotAtAllSaysTheOldSecretStoppedAtOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, "0");

        Where(posted).ShouldContain("oldEnds=now");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The old secret stopped working at once");
        html.ShouldContain("the product can't prove who it is until it has the new secret");
    }

    [Fact]
    public async Task AnotherSessionCannotTakeTheNewSecret()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var admin = await SignedInAsync(factory);
        using var other = await SignedInAsync(factory);

        var posted = await PostRotateAsync(admin, Helpdesk, "24");

        (await other.GetStringAsync(Where(posted), Token)).ShouldNotContain(api.ProductSecret);
        (await admin.GetStringAsync(Where(posted), Token)).ShouldContain(api.ProductSecret);
    }

    [Fact]
    public async Task NeitherTheSecretNorTheTokenIsEverLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now), logs: logs);
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, "24");
        await client.GetStringAsync(Where(posted), Token);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(api.ProductSecret);
        everything.ShouldNotContain(api.AccessTokensIssued[0]);
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

        var posted = await PostRotateAsync(client, Helpdesk, grace);

        api.SecretRotateCalls.ShouldBeEmpty();
        Where(posted).ShouldBe($"{PagePath}?error=bad_grace&app={Helpdesk}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Choose how long the old secret keeps working");
    }

    [Fact]
    public async Task TheApisOwnReasonsAreExplainedInPlainWordsAndNothingLeaks()
    {
        var gone = Api();
        gone.SecretRotateResponse = _ => Problem(HttpStatusCode.NotFound, "application_not_found");
        var refused = Api();
        refused.SecretRotateResponse = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.SecretRotateResponse = _ => throw new HttpRequestException("connection refused");
        var cases = new (FakeAuthApi Api, string Expect)[]
        {
            (gone, "That product isn't here any more"),
            (refused, "You don't have permission to do that"),
            (down, "Auth Service isn't responding right now. Nothing was changed."),
        };

        foreach (var (api, expect) in cases)
        {
            await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
            using var client = await SignedInAsync(factory);

            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(await PostRotateAsync(client, Helpdesk, "24")), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
            html.ShouldNotContain(api.ProductSecret);
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoProductsAndCannotReplaceAnythingEvenByPostingDirectly()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.GetAsync(PagePath, Token);
        var posted = await client.PostAsync($"{PagePath}/{Helpdesk}/rotate-secret", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("graceHours", "24")]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("Helpdesk");
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.SecretRotateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsReplaced()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await client.PostAsync($"{PagePath}/{Helpdesk}/rotate-secret", new FormUrlEncodedContent([new("graceHours", "24")]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.SecretRotateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AListTheApiCannotReadIsACalmError()
    {
        var api = Api();
        api.ProductsResponse = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("We couldn&#x27;t load the products");
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static async Task<HttpResponseMessage> PostRotateAsync(HttpClient client, Guid product, string grace)
    {
        var page = await client.GetStringAsync($"{PagePath}?rotate={product}", Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("/rotate-secret\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        body.Add(new("graceHours", grace));
        return await client.PostAsync($"{PagePath}/{product}/rotate-secret", new FormUrlEncodedContent(body), Token);
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        ProductsJson = $$"""[{"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk"},{"id":"{{Billing}}","key":"billing","name":"Billing"}]""",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
