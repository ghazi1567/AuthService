using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>ENT-06 (AUTH-283) The platform console's product limits: the list, adding one, editing one (never its key), and what the API refuses.</summary>
public sealed class PlatformLimitsPageTests
{
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string PagePath = $"/platform/products/{Marketing}/limits";
    private static readonly string ApiPath = $"/api/auth/admin/applications/{Marketing}/limits";

    [Fact]
    public async Task ENT06_LimitsAreListedByKeyWithLabelUnitAndDefault()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Marketing limits · Auth Service</title>");
        html.ShouldContain("Reachable contacts");
        html.ShouldContain("People you can message");
        html.ShouldContain("1,000 contacts", customMessage: "the default with its unit and thousands separator");
        html.ShouldContain("On or off");
        html.ShouldContain("Unlimited");
        html.ShouldContain("Showing 3 limits");
        html.IndexOf("contacts.reachable", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("journeys.ai", StringComparison.Ordinal));
        html.ShouldContain("A limit can't be renamed or removed");
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task ENT06_AProductWithNoLimitsHasAnEmptyState()
    {
        var api = Api();
        api.EntitlementJson[$"GET {ApiPath}"] = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("No limits yet");
        html.ShouldContain($"href=\"{PagePath}?add=1\"");
    }

    [Fact]
    public async Task ENT06_AddingALimitSendsItAsTheAdminAndSaysWhatHappened()
    {
        var api = Api();
        api.EntitlementJson[$"POST {ApiPath}"] = $$"""{"applicationId":"{{Marketing}}","created":1,"updated":0,"unchanged":0,"limits":[]}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PortalForms.PostAsync(client, $"{PagePath}?add=1", "limit", new Dictionary<string, string?>
        {
            ["key"] = " Storage.Assets ", ["label"] = "Asset storage", ["unit"] = "MB", ["defaultValue"] = "1,024",
        });

        var call = api.EntitlementCalls.Single(call => call.Method == "POST");
        call.PathAndQuery.ShouldBe(ApiPath);
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldBe("""{"limits":[{"key":"storage.assets","label":"Asset storage","description":null,"unit":"MB","defaultValue":1024}]}""");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=added&key=storage.assets");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PortalForms.Where(posted), Token));
        html.ShouldContain("The limit is added");
        html.ShouldContain("Every organisation now has 'storage.assets'");
    }

    [Fact]
    public async Task ENT06_TickingUnlimitedSendsNoNumber()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await PortalForms.PostAsync(client, $"{PagePath}?add=1", "limit", new Dictionary<string, string?>
        {
            ["key"] = "journeys.live", ["label"] = "Live journeys", ["defaultValue"] = "", ["unlimited"] = "true",
        });

        api.EntitlementCalls.Single(call => call.Method == "POST").Body.ShouldContain("\"defaultValue\":null");
    }

    [Fact]
    public async Task ENT06_EditingKeepsTheKeyAndStartsFromTheSavedValues()
    {
        var api = Api();
        api.EntitlementJson[$"POST {ApiPath}"] = $$"""{"applicationId":"{{Marketing}}","created":0,"updated":1,"unchanged":0,"limits":[]}""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?edit=contacts.reachable", Token));
        dialog.ShouldContain("Edit Reachable contacts");
        dialog.ShouldContain("A key can't be changed");
        dialog.ShouldContain("value=\"1000\"");

        var posted = await PortalForms.PostAsync(client, $"{PagePath}?edit=contacts.reachable", "limit", new Dictionary<string, string?> { ["defaultValue"] = "2000" });

        api.EntitlementCalls.Single(call => call.Method == "POST").Body
            .ShouldBe("""{"limits":[{"key":"contacts.reachable","label":"Reachable contacts","description":"People you can message","unit":"contacts","defaultValue":2000}]}""");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=changed&key=contacts.reachable");
    }

    [Fact]
    public async Task ENT06_ADefaultThatIsNotAWholeNumberNeverReachesTheApiAndWhatWasTypedIsKept()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, $"{PagePath}?add=1", "limit", new Dictionary<string, string?>
        {
            ["key"] = "journeys.live", ["label"] = "", ["defaultValue"] = "-5",
        });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("Write a label people can read.");
        html.ShouldContain("Write the default as a whole number of 0 or more");
        html.ShouldContain("value=\"journeys.live\"");
        api.EntitlementCalls.ShouldNotContain(call => call.Method == "POST");
    }

    [Fact]
    public async Task ENT06_TheApisReasonsAreExplainedInTheDialog()
    {
        var cases = new (Func<HttpResponseMessage> Respond, string Expect)[]
        {
            (() => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status":400,"title":"x","errors":{"Limits[0].Key":["Key must be lowercase letters, digits and . _ - characters."]}}""", Encoding.UTF8, "application/problem+json"),
            }, "Key must be lowercase letters"),
            (() => Problem(HttpStatusCode.Conflict, "too_many_limits"), "A product can have at most 100 limits."),
            (() => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to change products."),
            (() => throw new HttpRequestException("connection refused"), "Nothing was saved."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api();
            api.EntitlementResponse = (method, _, _) => method == "POST" ? respond() : null;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var response = await PortalForms.PostAsync(client, $"{PagePath}?add=1", "limit", new Dictionary<string, string?>
            {
                ["key"] = "a.b", ["label"] = "A", ["defaultValue"] = "1",
            });
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task ENT06_SomeoneWhoIsNotAPlatformAdminSeesNoLimits()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync(PagePath, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("contacts.reachable");
        api.EntitlementCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task ENT06_AListTheApiCannotReadIsACalmError()
    {
        var api = Api();
        api.EntitlementResponse = (_, _, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("We couldn&#x27;t load the limits");
    }

    [Fact]
    public async Task ENT06_TheProductListLinksToLimitsAndPlans()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var list = await client.GetStringAsync("/platform/products", Token);

        list.ShouldContain($"href=\"/platform/products/{Marketing}/limits\"");
        list.ShouldContain($"href=\"/platform/products/{Marketing}/plans\"");
    }

    internal static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail = "x") => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"{{detail}}","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    internal static FakeAuthApi Api()
    {
        var api = new FakeAuthApi
        {
            IsPlatformAdmin = true,
            MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = "[]",
            AdminProductsJson = $$"""
                [{"id":"{{Marketing}}","key":"marketing","name":"Marketing","isActive":true,"permissionCount":2,"launchUrl":null,"createdAt":"2026-10-02T10:00:00Z"}]
                """,
        };
        api.EntitlementJson[$"GET {ApiPath}"] = """
            [
              {"key":"journeys.ai","label":"AI journeys","description":null,"unit":"flag","defaultValue":0,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"contacts.reachable","label":"Reachable contacts","description":"People you can message","unit":"contacts","defaultValue":1000,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"storage.assets","label":"Asset storage","description":null,"unit":"MB","defaultValue":null,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"}
            ]
            """;
        return api;
    }

    internal static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
