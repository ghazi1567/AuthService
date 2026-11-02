using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's subscriptions (UI03_T3): one row per product with its status explained and its seats shown. No real API is involved.</summary>
public sealed class ConsoleSubscriptionsPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Analytics = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static readonly Guid Inventory = Guid.Parse("20000000-0000-4000-8000-000000000004");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/subscriptions";

    [Fact]
    public async Task EachProductShowsItsStatusAndSeatsExactlyAsTheApiSaysThem()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Subscriptions · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Subscriptions</h1>""");
        html.ShouldContain("3 of 5 seats used");
        html.ShouldContain("9 of 10 seats used");
        html.ShouldContain("1 seat used · no seat limit");
        html.ShouldContain("2 seats used · no seat limit");
        html.ShouldContain("Showing 4 subscriptions");
        Regex.Matches(html, "role=\"progressbar\"").Count.ShouldBe(2, "only limited products have a meter");
        html.ShouldContain("aria-valuenow=\"3\"");
        html.ShouldContain("aria-valuemax=\"5\"");
        html.ShouldContain("width: 60%");
        html.ShouldContain("width: 90%");
    }

    [Fact]
    public async Task AlmostFullAndFullAreFlaggedInWordsNotJustColour()
    {
        var api = Api();
        api.SubscriptionsJson = $$"""
            [
              {"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":10,"seatsUsed":8},
              {"applicationId":"{{Billing}}","applicationKey":"billing","applicationName":"Billing","status":"Active","maxSeats":5,"seatsUsed":5},
              {"applicationId":"{{Analytics}}","applicationKey":"analytics","applicationName":"Analytics","status":"Active","maxSeats":10,"seatsUsed":2}
            ]
            """;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        Regex.Matches(html, "Almost full").Count.ShouldBe(1);
        Regex.Matches(html, "No seats left").Count.ShouldBe(1);
        html.ShouldContain("class=\"meter is-near\"");
        html.ShouldContain("class=\"meter is-full\"");
    }

    [Fact]
    public async Task ASuspendedOrCancelledProductIsExplainedAndTheTopOfThePageSaysSo()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Suspended");
        html.ShouldContain("Paused. Nobody can open it, and no one can be given access, until it is active again.");
        html.ShouldContain("Cancelled");
        html.ShouldContain("Ended. Nobody can open it any more.");
        html.ShouldContain("2 products aren't active");
        html.ShouldContain("People you give access to can open it.");
    }

    [Fact]
    public async Task OneInactiveProductReadsInTheSingular()
    {
        var api = Api();
        api.SubscriptionsJson = $$"""[{"applicationId":"{{Billing}}","applicationKey":"billing","applicationName":"Billing","status":"Suspended","maxSeats":5,"seatsUsed":1}]""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("1 product isn&#x27;t active");
    }

    [Fact]
    public async Task ActiveProductsComeFirstThenTheRestEachByName()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        var order = new[] { "Analytics", "Helpdesk", "Billing", "Inventory" }.Select(name => html.IndexOf($"person-name\">{name}<", StringComparison.Ordinal)).ToArray();
        order.ShouldAllBe(index => index > 0);
        order.ShouldBe(order.OrderBy(index => index).ToArray());
    }

    [Fact]
    public async Task ManageAccessIsOfferedOnlyForActiveProductsAndOnlyToSomeoneWhoMayManageIt()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var limited = Api();
        limited.ConsolePermissions = ["subscriptions.read:ReadOnly", "members.read:ReadOnly"];
        await using var limitedFactory = new WebAppFactory(limited);
        using var limitedClient = await SignedInAsync(limitedFactory);

        var html = await client.GetStringAsync(PagePath, Token);
        var withoutRights = await limitedClient.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"/console/{Acme}/access#product-{Helpdesk}\"");
        html.ShouldContain($"href=\"/console/{Acme}/access#product-{Analytics}\"");
        html.ShouldNotContain($"#product-{Billing}");
        html.ShouldNotContain($"#product-{Inventory}");
        withoutRights.ShouldContain("Helpdesk");
        withoutRights.ShouldNotContain("Manage access");
    }

    [Fact]
    public async Task NoProductsShowsAnEmptyState()
    {
        var api = Api();
        api.SubscriptionsJson = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("No products yet");
        html.ShouldContain("Products are connected by the platform administrator.");
        html.ShouldNotContain("isn&#x27;t active");
    }

    [Fact]
    public async Task WithoutPermissionToReadSubscriptionsThePageSaysWhichPermissionIsNeeded()
    {
        var api = Api();
        api.ConsolePermissions = ["members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("You can&#x27;t see subscriptions");
        html.ShouldContain("'Subscriptions · Read only' permission");
        html.ShouldNotContain("Helpdesk");
    }

    [Fact]
    public async Task AListTheApiRefusesReadsAsAPermissionMessageAndAFailureAsACalmError()
    {
        var refusing = Api();
        refusing.SubscriptionsResponse = () => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var failing = Api();
        failing.SubscriptionsResponse = () => throw new HttpRequestException("connection refused");
        await using var refusingFactory = new WebAppFactory(refusing);
        await using var failingFactory = new WebAppFactory(failing);
        using var refusingClient = await SignedInAsync(refusingFactory);
        using var failingClient = await SignedInAsync(failingFactory);

        (await refusingClient.GetStringAsync(PagePath, Token)).ShouldContain("You can&#x27;t see subscriptions");
        var failed = await failingClient.GetStringAsync(PagePath, Token);
        failed.ShouldContain("We couldn&#x27;t load the subscriptions");
        failed.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ThePageIsInTheNavigationForAnyoneWhoMayReadIt()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"/console/{Acme}/subscriptions\"");
        html.ShouldContain("aria-current=\"page\"");
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        SubscriptionsJson = $$"""
            [
              {"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":5,"seatsUsed":3},
              {"applicationId":"{{Analytics}}","applicationKey":"analytics","applicationName":"Analytics","status":"Active","maxSeats":null,"seatsUsed":2},
              {"applicationId":"{{Billing}}","applicationKey":"billing","applicationName":"Billing","status":"Suspended","maxSeats":10,"seatsUsed":9},
              {"applicationId":"{{Inventory}}","applicationKey":"inventory","applicationName":"Inventory","status":"Cancelled","maxSeats":null,"seatsUsed":1}
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
