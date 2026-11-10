using System.Net;

namespace AuthService.Web.Tests;

/// <summary>
/// ENT-06 (AUTH-283) An organisation's products, its plan and limits for one product (change the plan, set or clear an override with a reason),
/// and the plan history. No real API is involved.
/// </summary>
public sealed class PlatformOrganisationEntitlementsPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string OrganisationPath = $"/platform/organisations/{Acme}";
    private static readonly string PagePath = $"{OrganisationPath}/products/{Marketing}";
    private static readonly string ApiPath = $"/api/auth/admin/tenants/{Acme}/subscriptions/{Marketing}";

    [Fact]
    public async Task ENT06_AnOrganisationsProductsAreListedWithTheirPlansAndReachedFromTheOrganisationList()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var list = await client.GetStringAsync("/platform/organisations", Token);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(OrganisationPath, Token));

        list.ShouldContain($"href=\"{OrganisationPath}\"");
        html.ShouldContain("<title>Acme Ltd · Auth Service</title>");
        html.ShouldContain("Marketing");
        html.ShouldContain("Growth");
        html.ShouldContain("No plan", customMessage: "a product with no plan says so");
        html.ShouldContain("Up to 5");
        html.ShouldContain($"href=\"{PagePath}\"");
        html.ShouldContain("Showing 2 products");
    }

    [Fact]
    public async Task ENT06_TheLimitsSayWhereEachComesFromAndAnOverrideShowsItsReason()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Marketing for Acme Ltd");
        html.ShouldContain("75,000 contacts");
        html.ShouldContain("Override");
        html.ShouldContain("\"Goodwill for the outage\" · set by a platform admin on 9 Nov 2026");
        html.ShouldContain("Plan");
        html.ShouldContain("Default");
        html.ShouldContain("Version 4.");
        html.ShouldContain("<option value=\"growth\" selected");
        html.ShouldNotContain("<option value=\"legacy\"", customMessage: "an inactive plan the organisation isn't on can't be chosen");
        html.ShouldContain($"href=\"{PagePath}?clear=contacts.reachable\"");
        html.ShouldNotContain($"href=\"{PagePath}?clear=seats\"");
    }

    [Fact]
    public async Task ENT06_ChangingThePlanSendsItAndNoPlanTakesItOff()
    {
        foreach (var (choice, body) in new[] { ("starter", """{"planKey":"starter"}"""), ("", """{"planKey":null}""") })
        {
            var api = Api();
            await using var factory = new WebAppFactory(api);
            using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

            var posted = await PortalForms.PostAsync(client, PagePath, "plan", new Dictionary<string, string?> { ["planKey"] = choice });

            var call = api.EntitlementCalls.Single(call => call.Method == "PUT");
            call.PathAndQuery.ShouldBe($"{ApiPath}/plan");
            call.Body.ShouldBe(body);
            PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=plan");
        }
    }

    [Fact]
    public async Task ENT06_AnInactivePlanIsExplainedWhenTheApiRefusesIt()
    {
        var api = Api();
        api.EntitlementResponse = (method, _, _) => method == "PUT" ? PlatformLimitsPageTests.Problem(HttpStatusCode.Conflict, "plan_inactive") : null;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, PagePath, "plan", new Dictionary<string, string?> { ["planKey"] = "starter" });

        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)).ShouldContain("That plan is inactive, so it can't be given to more organisations.");
    }

    [Fact]
    public async Task ENT06_AnOverrideNeedsAReasonAndAWholeNumberBeforeAnythingIsSent()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, $"{PagePath}?override=seats", "override", new Dictionary<string, string?> { ["value"] = "lots", ["reason"] = " " });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("Write the value as a whole number of 0 or more, or tick Unlimited.");
        html.ShouldContain("Write why Acme Ltd gets a different number");
        api.EntitlementCalls.ShouldNotContain(call => call.Method == "PUT");
    }

    [Fact]
    public async Task ENT06_SettingAnOverrideSendsTheValueAndReason()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?override=seats", Token));
        dialog.ShouldContain("Override Users for Acme Ltd");
        dialog.ShouldContain("Only platform admins see it.");

        var posted = await PortalForms.PostAsync(client, $"{PagePath}?override=seats", "override", new Dictionary<string, string?>
        {
            ["value"] = "12", ["reason"] = "Extra seats for the launch",
        });

        var call = api.EntitlementCalls.Single(call => call.Method == "PUT");
        call.PathAndQuery.ShouldBe($"{ApiPath}/overrides/seats");
        call.Body.ShouldBe("""{"value":12,"reason":"Extra seats for the launch"}""");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=override");
    }

    [Fact]
    public async Task ENT06_ChangingAnOverrideStartsFromItAndUnlimitedSendsNoNumber()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?override=contacts.reachable", Token));
        dialog.ShouldContain("value=\"75000\"");
        dialog.ShouldContain("Goodwill for the outage</textarea>");

        await PortalForms.PostAsync(client, $"{PagePath}?override=contacts.reachable", "override", new Dictionary<string, string?> { ["value"] = "", ["unlimited"] = "true" });

        api.EntitlementCalls.Single(call => call.Method == "PUT").Body.ShouldBe("""{"value":null,"reason":"Goodwill for the outage"}""");
    }

    [Fact]
    public async Task ENT06_ClearingAnOverrideAsksFirstThenClearsIt()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?clear=contacts.reachable", Token));
        dialog.ShouldContain("Clear the override on Reachable contacts?");
        dialog.ShouldContain("Acme Ltd goes back to the Growth plan's value");

        var posted = await PortalForms.PostAsync(client, $"{PagePath}?clear=contacts.reachable", "clear");

        api.EntitlementCalls.Single(call => call.Method == "DELETE").PathAndQuery.ShouldBe($"{ApiPath}/overrides/contacts.reachable");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=cleared");
    }

    [Fact]
    public async Task ENT06_AProductTheOrganisationDoesNotUseIsSaidSo()
    {
        var api = Api();
        api.EntitlementResponse = (method, path, _) => method == "GET" && path.EndsWith("/entitlements", StringComparison.Ordinal)
            ? PlatformLimitsPageTests.Problem(HttpStatusCode.NotFound, "subscription_not_found")
            : null;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Acme Ltd doesn't use this product");
        html.ShouldContain("Back to Acme Ltd");
    }

    [Fact]
    public async Task ENT06_TheHistoryNamesProductsAndOrganisationsAndPassesTheFiltersOn()
    {
        var api = Api();
        api.EntitlementJson["GET /api/auth/admin/entitlement-history"] = $$"""
            {"items":[
              {"id":"80000000-0000-4000-8000-000000000002","occurredAt":"2026-11-10T09:30:00Z","applicationId":"{{Marketing}}","tenantId":"{{Acme}}","planKey":null,"change":"override_set","limitKey":"contacts.reachable","oldValue":50000,"newValue":75000,"reason":"Goodwill for the outage","actorType":"User","actorId":"90000000-0000-4000-8000-000000000001"},
              {"id":"80000000-0000-4000-8000-000000000001","occurredAt":"2026-11-09T08:00:00Z","applicationId":"{{Marketing}}","tenantId":null,"planKey":"growth","change":"plan_created","limitKey":null,"oldValue":null,"newValue":{"name":"Growth"},"reason":null,"actorType":"User","actorId":"90000000-0000-4000-8000-000000000001"}
            ],"next":{"before":"2026-11-09T08:00:00Z","beforeId":"80000000-0000-4000-8000-000000000001"} }
            """;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/platform/entitlement-history?applicationId={Marketing}&tenantId={Acme}", Token));

        html.ShouldContain("<title>Plan history · Auth Service</title>");
        html.ShouldContain("Override on 'contacts.reachable' set");
        html.ShouldContain("Marketing · Acme Ltd");
        html.ShouldContain("50,000 → 75,000");
        html.ShouldContain("Goodwill for the outage");
        html.ShouldContain("Plan 'growth' created");
        html.ShouldContain("— → see the plan");
        html.ShouldContain("A platform admin");
        html.ShouldContain("Load more");
        html.ShouldContain("href=\"/platform/entitlement-history\" aria-current=\"page\"");
        api.EntitlementCalls.ShouldContain(call => call.PathAndQuery == $"/api/auth/admin/entitlement-history?limit=50&applicationId={Marketing}&tenantId={Acme}");
    }

    [Fact]
    public async Task ENT06_AnEmptyHistoryAndAFilterThatMatchesNothingAreTold()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var all = await client.GetStringAsync("/platform/entitlement-history", Token);
        var filtered = await client.GetStringAsync("/platform/entitlement-history?planKey=scale", Token);

        all.ShouldContain("No changes yet");
        filtered.ShouldContain("Nothing matches these filters");
        filtered.ShouldContain("Clear filters");
    }

    [Fact]
    public async Task ENT06_SomeoneWhoIsNotAPlatformAdminSeesNoneOfTheseAndNothingIsAsked()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        foreach (var path in new[] { OrganisationPath, PagePath, "/platform/entitlement-history" })
        {
            (await client.GetAsync(path, Token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        }

        api.EntitlementCalls.ShouldBeEmpty();
    }

    private static FakeAuthApi Api()
    {
        var api = PlatformLimitsPageTests.Api();
        api.AdminOrganisationsJson = $$"""[{"id":"{{Acme}}","name":"Acme Ltd","status":"Active","memberCount":4,"createdAt":"2026-10-01T10:00:00Z"}]""";
        api.EntitlementJson[$"GET /api/auth/admin/tenants/{Acme}/subscriptions"] = $$"""
            [
              {"applicationId":"20000000-0000-4000-8000-000000000003","applicationKey":"analytics","applicationName":"Analytics","status":"Active","planKey":null,"planName":null,"maxSeats":null,"entitlementsVersion":1,"createdAt":"2026-11-01T10:00:00Z"},
              {"applicationId":"{{Marketing}}","applicationKey":"marketing","applicationName":"Marketing","status":"Active","planKey":"growth","planName":"Growth","maxSeats":5,"entitlementsVersion":4,"createdAt":"2026-11-01T10:00:00Z"}
            ]
            """;
        api.EntitlementJson[$"GET {ApiPath}/entitlements"] = $$"""
            {"tenantId":"{{Acme}}","applicationId":"{{Marketing}}","planKey":"growth","planName":"Growth","version":4,"maxSeats":5,
             "limits":[
               {"key":"contacts.reachable","label":"Reachable contacts","unit":"contacts","value":75000,"source":"Override"},
               {"key":"journeys.ai","label":"AI journeys","unit":"flag","value":0,"source":"Default"},
               {"key":"seats","label":"Users","unit":"users","value":5,"source":"Plan"}
             ],
             "overrides":[{"limitKey":"contacts.reachable","value":75000,"reason":"Goodwill for the outage","setByType":"User","setById":"90000000-0000-4000-8000-000000000001","setAt":"2026-11-09T10:00:00Z"}]}
            """;
        api.EntitlementJson[$"GET /api/auth/admin/applications/{Marketing}/plans"] = """
            [
              {"key":"growth","name":"Growth","description":null,"isActive":true,"canDelete":false,"values":{},"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"legacy","name":"Legacy","description":null,"isActive":false,"canDelete":false,"values":{},"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"starter","name":"Starter","description":null,"isActive":true,"canDelete":true,"values":{},"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"}
            ]
            """;
        return api;
    }
}
