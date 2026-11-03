using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The product picker (UI05_T2): the products a person can open in an organisation, why the others can't be, and the admin entry for admins. No real API is involved.</summary>
public sealed class ChooseProductPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Analytics = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static readonly Guid Inventory = Guid.Parse("20000000-0000-4000-8000-000000000004");
    private static readonly Guid Reports = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/choose-product?tenantId={Acme}";

    private static readonly string[] AdminPermissions =
    [
        "members.read:Full", "members.manage:Full", "members.invite:Full", "roles.read:Full", "roles.manage:Full", "roles.assign:Full",
        "access.manage:Full", "apikeys.read:Full", "apikeys.manage:Full", "subscriptions.read:Full", "audit.read:Full",
    ];

    [Fact]
    public async Task ChoosingAnOrganisationFromThePickerLeadsToARealPage()
    {
        await using var factory = new WebAppFactory(Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"), Product(Billing, "billing", "Billing", "NoAccess")));
        using var client = await SignedInAsync(factory);

        var picker = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var response = await client.GetAsync(PagePath, Token);

        picker.ShouldContain($"href=\"/choose-product?tenantId={Acme}\"");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EachProductIsOpenableOrGreyedOutWithItsOwnReason()
    {
        var api = Member(
            Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"),
            Product(Billing, "billing", "Billing", "NoAccess"),
            Product(Analytics, "analytics", "Analytics", "NotSubscribed"),
            Product(Inventory, "inventory", "Inventory", "SubscriptionSuspended"),
            Product(Reports, "reports", "Reports", "SubscriptionCancelled"));
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}&stay=1", Token));

        html.ShouldContain("<title>Acme Ltd · Auth Service</title>");
        html.ShouldContain("What would you like to do?");
        html.ShouldContain("You have a seat.");
        html.ShouldContain("You don't have a seat. Ask an admin at Acme Ltd for access.");
        html.ShouldContain("Acme Ltd hasn't subscribed to Analytics.");
        html.ShouldContain("The subscription to Inventory is suspended, so it can't be opened.");
        html.ShouldContain("The subscription to Reports has ended.");
        Regex.Matches(html, "launch-card is-unavailable").Count.ShouldBe(4);
        Regex.Matches(html, "action=\"/choose-product/open\"").Count.ShouldBe(1, "only the available product can be opened");
        html.ShouldContain($"name=\"applicationId\" value=\"{Helpdesk}\"");
        html.ShouldNotContain("Nothing to open here yet");
    }

    [Fact]
    public async Task AvailableButWithNoAddressIsShownAsNotReadyAndNeverOffered()
    {
        await using var factory = new WebAppFactory(Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", null), Product(Billing, "billing", "Billing", "NoAccess")));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("It isn't ready to open yet. Ask the platform administrator to set where it opens.");
        html.ShouldNotContain("action=\"/choose-product/open\"");
        html.ShouldContain("Nothing to open here yet");
    }

    [Fact]
    public async Task ExactlyOneOpenableProductContinuesByItselfUnlessThePersonAsksToChoose()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"), Product(Billing, "billing", "Billing", "NoAccess"));
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var auto = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));
        var chosen = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}&stay=1", Token));

        auto.ShouldContain("Opening Acme Ltd · Helpdesk…");
        auto.ShouldContain("It's the only product you can open here");
        auto.ShouldContain("action=\"/choose-product/open\" data-autosubmit");
        auto.ShouldContain($"name=\"applicationId\" value=\"{Helpdesk}\"");
        auto.ShouldContain($"href=\"{PagePath}&stay=1\"");
        chosen.ShouldNotContain("Opening Acme Ltd");
        chosen.ShouldContain("What would you like to do?");
    }

    [Fact]
    public async Task TwoOpenableProductsAreBothOfferedAndNeitherContinuesByItself()
    {
        await using var factory = new WebAppFactory(Member(
            Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"),
            Product(Billing, "billing", "Billing", "Available", "https://billing.example.com/in")));
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldNotContain("data-autosubmit");
        Regex.Matches(html, "action=\"/choose-product/open\"").Count.ShouldBe(2);
    }

    [Fact]
    public async Task AnAdminSeesTheConsoleAndWhatCanBeConnectedAndNeverContinuesByItself()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"));
        api.ConsolePermissions = AdminPermissions;
        api.ProductsJson = $$"""[{"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk"},{"id":"{{Billing}}","key":"billing","name":"Billing"}]""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldNotContain("data-autosubmit", customMessage: "an admin has the console to choose too");
        html.ShouldContain($"href=\"/console/{Acme}/members\"");
        html.ShouldContain("Admin console");
        html.ShouldContain("Connect a product");
        html.ShouldContain("A product connects your organisation itself. Ask the team behind the product to connect Acme Ltd.");
        html.ShouldContain("Billing");
        html.ShouldNotContain(Billing.ToString(), customMessage: "only names are shown, no ids or client details");
    }

    [Fact]
    public async Task TheConsoleLinkGoesToTheFirstPartThePersonMayRead()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NoAccess"));
        api.ConsolePermissions = ["apikeys.read:ReadOnly", "audit.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"/console/{Acme}/api-keys\"");
        html.ShouldNotContain($"/console/{Acme}/members");
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminSeesNoConsoleAndNoConnectList()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NoAccess"));
        api.ConsolePermissions = [];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Nothing to open here yet");
        html.ShouldContain("Your organisation's admin decides which products you can use.");
        html.ShouldNotContain("Admin console");
        html.ShouldNotContain("Connect a product");
    }

    [Fact]
    public async Task AnAdminWhoseProductListCannotBeReadStillSeesTheRestOfThePage()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NoAccess"));
        api.ConsolePermissions = AdminPermissions;
        api.ProductsResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("We couldn't load the list of products");
        html.ShouldContain("Admin console");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ASuspendedOrganisationIsExplainedToItsMembers()
    {
        var api = Member();
        api.MyApplicationsResponse = _ => Problem(HttpStatusCode.Forbidden, "tenant_suspended");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Acme Ltd is suspended");
        html.ShouldContain("Nothing can be opened in Acme Ltd right now");
        html.ShouldContain("Ask the platform administrator to switch it back on.");
        html.ShouldNotContain("What would you like to do?");
    }

    [Fact]
    public async Task AnOrganisationThePersonIsNotAnActiveMemberOfIsRefusedWithoutAskingForProducts()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"));
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/choose-product?tenantId={Guid.NewGuid()}", Token));

        html.ShouldContain("You can't open this organisation");
        html.ShouldNotContain("Helpdesk");
        api.MyApplicationsCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AProductListThatCannotBeReadIsACalmError()
    {
        var api = Member();
        api.MyApplicationsResponse = _ => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t open this organisation");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task TheListIsAskedForWithThePersonsOwnSignInTokenAndTheOrganisation()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NoAccess"));
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        var call = api.MyApplicationsCalls.ShouldHaveSingleItem();
        call.Query.ShouldBe($"?tenantId={Acme}");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
    }

    [Theory]
    [InlineData("not_available", "can't be opened right now")]
    [InlineData("tenant_suspended", "Acme Ltd is suspended")]
    [InlineData("rate_limited", "Try again in 42 seconds.")]
    [InlineData("expired", "Choose the product again.")]
    [InlineData("unavailable", "Nothing was changed. Try again in a moment.")]
    public async Task WhatWentWrongOpeningAProductIsSaidAndThePageDoesNotTryAgainByItself(string code, string expected)
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "Available", "https://helpdesk.example.com/in"));
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}&stay=1&error={code}&app={Helpdesk}&wait=42", Token));

        html.ShouldContain(expected);
        html.ShouldNotContain("data-autosubmit", customMessage: "a failure must not loop");
        var withoutStay = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}&error={code}&app={Helpdesk}", Token));
        withoutStay.ShouldNotContain("data-autosubmit", customMessage: "an error stops the automatic continue even without stay");
    }

    [Fact]
    public async Task SomeoneSignedOutIsSentToSignIn()
    {
        await using var factory = new WebAppFactory(Member());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(PagePath, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldContain("/login");
    }

    [Fact]
    public async Task WithoutAnOrganisationThePersonIsSentBackToChooseOne()
    {
        await using var factory = new WebAppFactory(Member());
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync("/choose-product", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldEndWith("/choose-organisation?change=1");
    }

    [Fact]
    public async Task AnAdminWithNothingToOpenIsPointedAtConnectingAProductNotAtThemselves()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NotSubscribed"));
        api.ConsolePermissions = AdminPermissions;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Connect a product below, then give people access to it in the admin console.");
        html.ShouldNotContain("Your organisation's admin decides");
    }

    [Fact]
    public async Task AnOrganisationNameEndingInSDoesNotGetAnAwkwardPossessive()
    {
        var api = Member(Product(Helpdesk, "helpdesk", "Helpdesk", "NotSubscribed"));
        api.MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Okafor Works","status":"Active"}]""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}&stay=1", Token));

        html.ShouldContain("Okafor Works hasn't subscribed to Helpdesk.");
        html.ShouldNotContain("Works's");
    }

    [Fact]
    public void TheOpenButtonIsAFullTouchTargetOnAPhone()
    {
        // The stylesheet lives in the repository; find it from this source file, wherever the build wrote its output.
        var root = new FileInfo(SourceFile()).Directory;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "AuthService.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull();
        var css = File.ReadAllText(Path.Combine(root.FullName, "src", "AuthService.Web", "wwwroot", "app.css"));

        Regex.IsMatch(css, @"@media \(max-width: 639px\)\s*\{\s*\.launch-card \.btn\s*\{\s*min-height: 44px;").ShouldBeTrue();
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static string Product(Guid id, string key, string name, string state, string? launchUrl = null) =>
        $$"""{"applicationId":"{{id}}","key":"{{key}}","name":"{{name}}","state":"{{state}}","launchUrl":{{(launchUrl is null ? "null" : $"\"{launchUrl}\"")}}}""";

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>An organisation, and the products the person can open in it.</summary>
    private static FakeAuthApi Member(params string[] products) => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        ConsolePermissions = [],
        MyApplicationsJson = "[" + string.Join(",", products) + "]",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
