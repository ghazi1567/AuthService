using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>Opening a product (UI05_T4, decision 0007): the person's token is posted to the address the platform set, in the request body, and never appears in an address, a log or a referrer. No real API is involved.</summary>
public sealed class ProductHandOffTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private const string LaunchUrl = "https://helpdesk.example.com/signed-in?from=auth";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/choose-product?tenantId={Acme}&stay=1";

    [Fact]
    public async Task TheTokenIsPostedToTheProductsAddressInTheBodyOfAFormAndNowhereElse()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await OpenAsync(client, Helpdesk);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var token = api.ProductTokensIssued.Last();
        html.ShouldContain($"<form method=\"post\" action=\"{LaunchUrl}\" data-autosubmit>");
        html.ShouldContain($"<input type=\"hidden\" name=\"access_token\" value=\"{token}\">");
        html.ShouldContain("<input type=\"hidden\" name=\"token_type\" value=\"Bearer\">");
        html.ShouldContain("<input type=\"hidden\" name=\"expires_in\" value=\"900\">");
        html.ShouldContain("Opening Helpdesk…");
        html.ShouldContain("Continue", customMessage: "a way on when scripts are off");
        response.Headers.Location.ShouldBeNull("nothing is redirected: the token has no address to ride in");
    }

    [Fact]
    public async Task TheTokenIsInNoAddressAndNoLink()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await OpenAsync(client, Helpdesk);
        var html = await response.Content.ReadAsStringAsync(Token);

        var token = api.ProductTokensIssued.Last();
        Regex.Matches(html, "(?:href|action|src)=\"([^\"]*)\"").Select(match => match.Groups[1].Value).ShouldAllBe(value => !value.Contains(token));
        response.RequestMessage!.RequestUri!.ToString().ShouldNotContain(token);
        response.Headers.ToString().ShouldNotContain(token);
    }

    [Fact]
    public async Task TheResponseSendsNoReferrerIsNotCachedAndMayPostOnlyToTheProductsOwnOrigin()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await OpenAsync(client, Helpdesk);
        var picker = await client.GetAsync(PagePath, Token);

        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        csp.ShouldContain("form-action 'self' https://helpdesk.example.com;");
        csp.ShouldNotContain("*");
        string.Join(' ', picker.Headers.GetValues("Content-Security-Policy")).ShouldContain("form-action 'self';", customMessage: "every other page keeps posting only to itself");
    }

    [Fact]
    public async Task TheTokenIsNeverLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, logs: logs);
        using var client = await SignedInAsync(factory);

        await OpenAsync(client, Helpdesk);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(api.ProductTokensIssued.Last());
        everything.ShouldNotContain(api.AccessTokensIssued[0]);
    }

    [Fact]
    public async Task TheAddressComesFromTheApiNotFromTheRequest()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await OpenAsync(client, Helpdesk, new() { ["launchUrl"] = "https://evil.example.com/steal", ["url"] = "https://evil.example.com/steal" });
        var html = await response.Content.ReadAsStringAsync(Token);

        html.ShouldContain($"action=\"{LaunchUrl}\"");
        html.ShouldNotContain("evil.example.com");
        string.Join(' ', response.Headers.GetValues("Content-Security-Policy")).ShouldNotContain("evil.example.com");
    }

    [Fact]
    public async Task ThePersonsOwnIdentityTokenMakesTheExchangeForThatOrganisationAndProductOnly()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await OpenAsync(client, Helpdesk);

        var call = ProductExchanges(api).ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldBe($$"""{"tenantId":"{{Acme}}","applicationId":"{{Helpdesk}}"}""");
    }

    [Fact]
    public async Task AProductThePersonCannotOpenIsNeverExchangedForAndTheyAreToldWhy()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var response = await OpenAsync(client, Billing);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe($"/choose-product?tenantId={Acme}&stay=1&error=not_available&app={Billing}");
        ProductExchanges(api).ShouldBeEmpty("nothing is traded for a product the API says is not theirs");
    }

    [Fact]
    public async Task AnUnknownProductIsTreatedTheSameWay()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var response = await OpenAsync(client, Guid.NewGuid());

        Where(response).ShouldContain("error=not_available");
        ProductExchanges(api).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASuspendedOrganisationAndARefusedExchangeAreExplainedWithoutAToken()
    {
        var suspended = Api();
        var refused = Api();
        await using var suspendedFactory = new WebAppFactory(suspended);
        await using var refusedFactory = new WebAppFactory(refused);
        using var suspendedClient = await SignedInAsync(suspendedFactory);
        using var refusedClient = await SignedInAsync(refusedFactory);

        var first = await OpenAsync(suspendedClient, Helpdesk, beforePost: () => suspended.MyApplicationsResponse = _ => Problem(HttpStatusCode.Forbidden, "tenant_suspended"));
        var second = await OpenAsync(refusedClient, Helpdesk, beforePost: () => refused.ExchangeResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden));

        Where(first).ShouldBe($"/choose-product?tenantId={Acme}&stay=1&error=tenant_suspended&app={Helpdesk}");
        Where(second).ShouldBe($"/choose-product?tenantId={Acme}&stay=1&error=not_available&app={Helpdesk}");
    }

    [Fact]
    public async Task ARateLimitedExchangeSaysHowLongToWait()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        api.ExchangeResponse = () =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        };

        var response = await OpenAsync(client, Helpdesk);

        Where(response).ShouldBe($"/choose-product?tenantId={Acme}&stay=1&error=rate_limited&app={Helpdesk}&wait=42");
    }

    [Fact]
    public async Task AnApiThatCannotAnswerIsACalmFailureWithNoToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        api.ExchangeResponse = () => throw new HttpRequestException("connection refused");

        var response = await OpenAsync(client, Helpdesk);

        Where(response).ShouldContain("error=unavailable");
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsExchanged()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath, Token);

        var response = await client.PostAsync("/choose-product/open", new FormUrlEncodedContent([new("tenantId", Acme.ToString()), new("applicationId", Helpdesk.ToString())]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        ProductExchanges(api).ShouldBeEmpty();
    }

    [Fact]
    public async Task APostThatIsNotAnOrganisationAndAProductIsRefused()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);
        var fields = await FieldsAsync(client);

        var response = await client.PostAsync("/choose-product/open", new FormUrlEncodedContent([.. fields, new("tenantId", "nope"), new("applicationId", "nope")]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    /// <summary>The exchanges made for a product (the picker also trades once for the admin console, which is a different token).</summary>
    private static List<(string? Authorization, string Body)> ProductExchanges(FakeAuthApi api) =>
        [.. api.ExchangeCalls.Where(call => !call.Body.Contains("00000000-0000-7000-8000-00000000a07a"))];

    private static async Task<List<KeyValuePair<string, string>>> FieldsAsync(HttpClient client)
    {
        var page = await client.GetStringAsync(PagePath, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("action=\"/choose-product/open\""));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        fields.RemoveAll(field => field.Key is "tenantId" or "applicationId");
        return fields;
    }

    /// <summary>Posts the picker's own form (with a valid anti-forgery token) for one product, plus any extra fields a forger might add.</summary>
    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, Guid applicationId, Dictionary<string, string>? extra = null, Action? beforePost = null)
    {
        var fields = await FieldsAsync(client);
        beforePost?.Invoke();
        fields.Add(new("tenantId", Acme.ToString()));
        fields.Add(new("applicationId", applicationId.ToString()));
        fields.AddRange((extra ?? []).Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync("/choose-product/open", new FormUrlEncodedContent(fields), Token);
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", System.Text.Encoding.UTF8, "application/problem+json"),
    };

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        ConsolePermissions = [],
        MyApplicationsJson = $$"""
            [
              {"applicationId":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","state":"Available","launchUrl":"{{LaunchUrl}}"},
              {"applicationId":"{{Billing}}","key":"billing","name":"Billing","state":"NoAccess","launchUrl":null}
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
