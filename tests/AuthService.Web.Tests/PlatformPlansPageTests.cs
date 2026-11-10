using System.Net;

namespace AuthService.Web.Tests;

/// <summary>
/// ENT-06 (AUTH-283) The platform console's plans: the list, activating and deactivating, deleting a plan never assigned, creating, copying and
/// editing one, and the review of how many organisations an edit changes before it is saved.
/// </summary>
public sealed class PlatformPlansPageTests
{
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string PagePath = $"/platform/products/{Marketing}/plans";
    private static readonly string ApiPath = $"/api/auth/admin/applications/{Marketing}/plans";

    [Fact]
    public async Task ENT06_PlansAreListedWithStatusAndEveryLimitsValue()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Marketing plans · Auth Service</title>");
        html.ShouldContain("Growth");
        html.ShouldContain("For teams that send every week");
        html.ShouldContain("Reachable contacts: 50,000 contacts");
        html.ShouldContain("Asset storage: Unlimited");
        html.ShouldContain("AI journeys: default (Off)", customMessage: "a limit the plan leaves out shows its default");
        html.ShouldContain("Inactive");
        html.ShouldContain("Showing 2 plans");
        html.ShouldContain($"href=\"{PagePath}/growth/edit\"");
        html.ShouldContain($"href=\"{PagePath}/new?from=growth\"");
        html.ShouldContain($"href=\"{PagePath}?delete=starter\"");
        html.ShouldNotContain($"href=\"{PagePath}?delete=growth\"", customMessage: "a plan that was ever assigned can't be deleted");
    }

    [Fact]
    public async Task ENT06_AProductWithoutLimitsIsAskedForThemFirstAndOneWithoutPlansHasAnEmptyState()
    {
        var noLimits = Api();
        noLimits.EntitlementJson[$"GET /api/auth/admin/applications/{Marketing}/limits"] = "[]";
        var noPlans = Api();
        noPlans.EntitlementJson[$"GET {ApiPath}"] = "[]";
        await using var first = new WebAppFactory(noLimits);
        using var firstClient = await PlatformLimitsPageTests.SignedInAsync(first);
        await using var second = new WebAppFactory(noPlans);
        using var secondClient = await PlatformLimitsPageTests.SignedInAsync(second);

        var withoutLimits = await firstClient.GetStringAsync(PagePath, Token);
        var withoutPlans = await secondClient.GetStringAsync(PagePath, Token);

        withoutLimits.ShouldContain("Add limits first");
        withoutLimits.ShouldNotContain($"href=\"{PagePath}/new\"");
        withoutPlans.ShouldContain("No plans yet");
        withoutPlans.ShouldContain($"href=\"{PagePath}/new\"");
    }

    [Fact]
    public async Task ENT06_DeactivatingAPlanSendsItAndSaysWhatItMeans()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var posted = await PortalForms.PostAsync(client, PagePath, "active-growth");

        var call = api.EntitlementCalls.Single(call => call.Method == "PUT");
        call.PathAndQuery.ShouldBe($"{ApiPath}/growth/active");
        call.Body.ShouldBe("""{"isActive":false}""");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=deactivated&plan=Growth");
        WebUtility.HtmlDecode(await client.GetStringAsync(PortalForms.Where(posted), Token)).ShouldContain("organisations on it keep it, and no more can get it");
    }

    [Fact]
    public async Task ENT06_DeletingAPlanAsksFirstThenDeletesIt()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?delete=starter", Token));
        dialog.ShouldContain("Delete 'Starter'?");
        dialog.ShouldContain("This can't be undone.");
        api.EntitlementCalls.ShouldNotContain(call => call.Method == "DELETE");

        var posted = await PortalForms.PostAsync(client, $"{PagePath}?delete=starter", "delete");

        api.EntitlementCalls.Single(call => call.Method == "DELETE").PathAndQuery.ShouldBe($"{ApiPath}/starter");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=deleted&plan=Starter");
    }

    [Fact]
    public async Task ENT06_APlanThatBecameAssignedMeanwhileIsExplainedNotDeleted()
    {
        var api = Api();
        api.EntitlementResponse = (method, _, _) => method == "DELETE" ? PlatformLimitsPageTests.Problem(HttpStatusCode.Conflict, "plan_in_use") : null;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, $"{PagePath}?delete=starter", "delete");
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("This plan has been given to an organisation, so it can't be deleted. Deactivate it instead.");
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task ENT06_ANewPlanSendsOnlyTheValuesSetWithUnlimitedAsNoNumber()
    {
        var api = Api();
        api.EntitlementJson[$"POST {ApiPath}"] = """{"key":"scale","name":"Scale","description":null,"isActive":true,"canDelete":true,"values":{},"createdAt":"2026-11-11T10:00:00Z","updatedAt":"2026-11-11T10:00:00Z"}""";
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var posted = await PortalForms.PostAsync(client, $"{PagePath}/new", "plan", new Dictionary<string, string?>
        {
            ["key"] = "Scale", ["name"] = "Scale", ["value.contacts.reachable"] = "250,000", ["unlimited.storage.assets"] = "true",
        });

        api.EntitlementCalls.Single(call => call.Method == "POST").Body
            .ShouldBe("""{"key":"scale","name":"Scale","description":null,"isActive":true,"values":{"contacts.reachable":250000,"storage.assets":null}}""");
        PortalForms.Where(posted).ShouldBe($"{PagePath}?notice=created&plan=Scale");
    }

    [Fact]
    public async Task ENT06_CopyingAPlanStartsFromItsValuesWithANewKeyToChoose()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}/new?from=growth", Token));

        html.ShouldContain("New Marketing plan");
        html.ShouldContain("value=\"Growth (copy)\"");
        html.ShouldContain("id=\"plan-key\" name=\"key\" class=\"input mono\" value=\"\"");
        html.ShouldContain("value=\"50000\"");
    }

    [Fact]
    public async Task ENT06_AnEditThatChangesOrganisationsIsReviewedBeforeItIsSaved()
    {
        var api = Api();
        api.EntitlementJson[$"POST {ApiPath}/growth/preview"] = """{"subscriptions":40,"changed":12}""";
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);
        var changes = new Dictionary<string, string?> { ["value.contacts.reachable"] = "60000" };

        var reviewed = await PortalForms.PostAsync(client, $"{PagePath}/growth/edit", "plan", changes);
        var html = WebUtility.HtmlDecode(await reviewed.Content.ReadAsStringAsync(Token));

        html.ShouldContain("This changes the limits of 12 organisations");
        html.ShouldContain("12 of the 40 organisations on Growth get new limits when you save");
        html.ShouldContain("Save and update 12 organisations");
        html.ShouldContain("value=\"60000\"", customMessage: "what was typed is kept");
        api.EntitlementCalls.Single(call => call.Method == "POST").Body
            .ShouldBe("""{"name":"Growth","description":"For teams that send every week","values":{"contacts.reachable":60000,"seats":5,"storage.assets":null}}""");
        api.EntitlementCalls.ShouldNotContain(call => call.Method == "PUT");

        var saved = await PortalForms.PostFromHtmlAsync(client, $"{PagePath}/growth/edit", await reviewed.Content.ReadAsStringAsync(Token), "plan",
            new Dictionary<string, string?> { ["confirm"] = "1" });

        var put = api.EntitlementCalls.Single(call => call.Method == "PUT");
        put.PathAndQuery.ShouldBe($"{ApiPath}/growth");
        put.Body.ShouldContain("\"contacts.reachable\":60000");
        PortalForms.Where(saved).ShouldBe($"{PagePath}?notice=saved&plan=Growth");
    }

    [Fact]
    public async Task ENT06_AnEditThatChangesNobodyIsSavedStraightAway()
    {
        var api = Api();
        api.EntitlementJson[$"POST {ApiPath}/growth/preview"] = """{"subscriptions":40,"changed":0}""";
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var saved = await PortalForms.PostAsync(client, $"{PagePath}/growth/edit", "plan", new Dictionary<string, string?> { ["name"] = "Growth+" });

        api.EntitlementCalls.ShouldContain(call => call.Method == "PUT");
        PortalForms.Where(saved).ShouldBe($"{PagePath}?notice=saved&plan=Growth%2B");
    }

    [Fact]
    public async Task ENT06_AValueThatIsNotAWholeNumberIsRefusedBeforeAnythingIsSent()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, $"{PagePath}/growth/edit", "plan", new Dictionary<string, string?> { ["value.seats"] = "five" });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Users: write a whole number of 0 or more, tick Unlimited, or leave it empty for the default.");
        api.EntitlementCalls.ShouldNotContain(call => call.Method != "GET");
    }

    [Fact]
    public async Task ENT06_ATakenKeyAndAValueTheProductRefusesAreExplained()
    {
        var api = Api();
        api.EntitlementResponse = (method, path, _) => method == "POST" && path == ApiPath
            ? PlatformLimitsPageTests.Problem(HttpStatusCode.Conflict, "plan_key_taken")
            : null;
        await using var factory = new WebAppFactory(api);
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var response = await PortalForms.PostAsync(client, $"{PagePath}/new", "plan", new Dictionary<string, string?> { ["key"] = "growth", ["name"] = "Growth" });

        WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token)).ShouldContain("This product already has a plan with this key. Choose another.");
    }

    [Fact]
    public async Task ENT06_AnUnknownPlanIsSaidToBeGone()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await PlatformLimitsPageTests.SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}/scale/edit", Token));

        html.ShouldContain("That plan isn't here");
        html.ShouldContain("Back to plans");
    }

    private static FakeAuthApi Api()
    {
        var api = PlatformLimitsPageTests.Api();
        api.EntitlementJson[$"GET /api/auth/admin/applications/{Marketing}/limits"] = """
            [
              {"key":"contacts.reachable","label":"Reachable contacts","description":null,"unit":"contacts","defaultValue":1000,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"journeys.ai","label":"AI journeys","description":null,"unit":"flag","defaultValue":0,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"seats","label":"Users","description":null,"unit":"users","defaultValue":1,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"storage.assets","label":"Asset storage","description":null,"unit":"MB","defaultValue":100,"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"}
            ]
            """;
        api.EntitlementJson[$"GET {ApiPath}"] = """
            [
              {"key":"growth","name":"Growth","description":"For teams that send every week","isActive":true,"canDelete":false,"values":{"contacts.reachable":50000,"seats":5,"storage.assets":null},"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"},
              {"key":"starter","name":"Starter","description":null,"isActive":false,"canDelete":true,"values":{"contacts.reachable":5000},"createdAt":"2026-11-01T10:00:00Z","updatedAt":"2026-11-01T10:00:00Z"}
            ]
            """;
        return api;
    }
}
