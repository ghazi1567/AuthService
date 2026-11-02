using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's signing keys (UI04_T5): the published keys, and an emergency rotation that cannot be set off by accident. No real API is involved.</summary>
public sealed class PlatformSigningKeysPageTests
{
    private const string PagePath = "/platform/signing-keys";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ThePublishedKeysAreListedWithTheirTypeAndUse()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Signing keys · Auth Service</title>");
        html.ShouldContain("key-old-0001");
        html.ShouldContain("key-next-0002");
        html.ShouldContain("EC P-256");
        html.ShouldContain("ES256");
        html.ShouldContain("Signing tokens");
        html.ShouldContain("2 keys are published.");
        html.ShouldContain("A new key is published a day before it starts signing");
        html.ShouldContain($"href=\"{PagePath}?rotate=1\"");
        html.ShouldNotContain("role=\"dialog\"", customMessage: "nothing asks to be confirmed until it is asked for");
    }

    [Fact]
    public async Task TheRotationQuestionStatesTheEffectPlainlyAndAsksForAWordTyped()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?rotate=1", Token));

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Rotate the signing key now?");
        html.ShouldContain("Use this only if a signing key may have been exposed.");
        html.ShouldContain("every token already issued stops working");
        html.ShouldContain("People are signed out of the products and have to sign in again, including you.");
        html.ShouldContain("It can't be undone");
        html.ShouldContain("Type ROTATE to confirm");
        html.ShouldContain("Rotate signing key");
        api.SigningKeyRotateCalls.ShouldBeEmpty("opening the question rotates nothing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("rotat")]
    [InlineData("yes")]
    [InlineData("ROTATE NOW")]
    public async Task AnythingButTheWordRotatesNothingAndTheApiIsNotAsked(string typed)
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, typed);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"{PagePath}?rotate=1&error=confirm");
        api.SigningKeyRotateCalls.ShouldBeEmpty();
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("That isn't the word. Nothing was rotated.");
        html.ShouldContain("role=\"dialog\"", customMessage: "the question stays open");
        html.ShouldContain("key-old-0001", customMessage: "the keys are unchanged");
    }

    [Theory]
    [InlineData("ROTATE")]
    [InlineData("rotate")]
    [InlineData("  Rotate  ")]
    public async Task TheWordRotatesTheKeyAsTheAdminAndTheListShowsOnlyTheNewKey(string typed)
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, typed);

        var call = api.SigningKeyRotateCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        Where(posted).ShouldBe($"{PagePath}?notice=rotated&kid=key-new-0001");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The signing key was rotated");
        html.ShouldContain("New tokens are signed with key key-new-0001.");
        html.ShouldContain("Every token issued before this stopped working");
        html.ShouldContain("1 key is published.");
        Rows(html).ShouldNotContain("key-old-0001");
        Rows(html).ShouldContain("key-new-0001");
    }

    [Fact]
    public async Task TheApisOwnAnswersAreExplainedAndNothingIsClaimedThatDidNotHappen()
    {
        var refused = Api();
        refused.SigningKeyRotateResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.SigningKeyRotateResponse = () => throw new HttpRequestException("connection refused");
        var cases = new (FakeAuthApi Api, string Expect)[]
        {
            (refused, "You don't have permission to do that"),
            (down, "Auth Service isn't responding right now. Nothing was changed."),
        };

        foreach (var (api, expect) in cases)
        {
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(await PostRotateAsync(client, "ROTATE")), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("The signing key was rotated");
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoKeysAndCannotRotateEvenByPostingDirectly()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.GetAsync(PagePath, Token);
        var posted = await client.PostAsync($"{PagePath}/rotate", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("confirm", "ROTATE")]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("key-old-0001");
        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.SigningKeyRotateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsRotated()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await client.PostAsync($"{PagePath}/rotate", new FormUrlEncodedContent([new("confirm", "ROTATE")]), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.SigningKeyRotateCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task KeysThatCannotBeReadAreACalmErrorAndNoKeysAtAllSaysSo()
    {
        var failing = Api();
        failing.JwksResponse = () => throw new HttpRequestException("connection refused");
        var none = Api();
        none.JwksJson = """{"keys":[]}""";
        await using var failingFactory = new WebAppFactory(failing);
        await using var noneFactory = new WebAppFactory(none);
        using var failingClient = await SignedInAsync(failingFactory);
        using var noneClient = await SignedInAsync(noneFactory);

        var failed = await failingClient.GetStringAsync(PagePath, Token);
        var empty = await noneClient.GetStringAsync(PagePath, Token);

        failed.ShouldContain("We couldn&#x27;t load the signing keys");
        failed.ShouldNotContain("connection refused");
        empty.ShouldContain("No keys are published");
    }

    /// <summary>Just the keys of the list.</summary>
    private static string Rows(string html) => Regex.Match(html, "<tbody>.*?</tbody>", RegexOptions.Singleline).Value;

    private static async Task<HttpResponseMessage> PostRotateAsync(HttpClient client, string typed)
    {
        var page = await client.GetStringAsync($"{PagePath}?rotate=1", Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("/rotate\""));
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        body.Add(new("confirm", typed));
        return await client.PostAsync($"{PagePath}/rotate", new FormUrlEncodedContent(body), Token);
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
