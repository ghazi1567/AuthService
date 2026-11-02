using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>What to do in an organisation after choosing it (UI05_T6): the admin console for admins, and the products that can be connected. No real API is involved.</summary>
public sealed class ChooseProductPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/choose-product?tenantId={Acme}";

    [Fact]
    public async Task ChoosingAnOrganisationFromThePickerLeadsToARealPage()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var picker = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var response = await client.GetAsync($"/choose-product?tenantId={Acme}", Token);

        picker.ShouldContain($"href=\"/choose-product?tenantId={Acme}\"");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnAdminSeesTheirOrganisationTheConsoleAndWhatCanBeConnected()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Acme Ltd · Auth Service</title>");
        html.ShouldContain("Acme Ltd");
        html.ShouldContain("href=\"/choose-organisation?change=1\"");
        html.ShouldContain($"href=\"/console/{Acme}/members\"");
        html.ShouldContain("Admin console");
        html.ShouldContain("Connect a product");
        html.ShouldContain("A product connects your organisation itself. Ask the team behind the product to connect Acme Ltd.");
        html.ShouldContain("Billing");
        html.ShouldContain("Helpdesk");
        html.ShouldNotContain(Billing.ToString(), customMessage: "only names are shown, no ids or client details");
        html.ShouldNotContain("helpdesk-client");
    }

    [Fact]
    public async Task TheConsoleLinkGoesToTheFirstPartThePersonMayRead()
    {
        var api = Api();
        api.ConsolePermissions = ["apikeys.read:ReadOnly", "audit.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"href=\"/console/{Acme}/api-keys\"");
        html.ShouldNotContain($"/console/{Acme}/members");
    }

    [Fact]
    public async Task AMemberWhoIsNotAnAdminSeesNeitherTheConsoleNorTheConnectList()
    {
        var api = Api();
        api.ConsolePermissions = [];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Nothing to open from here yet");
        html.ShouldContain("Your organisation's admin decides which products you can use.");
        html.ShouldNotContain("Admin console");
        html.ShouldNotContain("Connect a product");
        html.ShouldNotContain("Billing");
    }

    [Fact]
    public async Task NothingToConnectAndAListThatCannotBeReadAreEachSaidCalmly()
    {
        var none = Api();
        none.ProductsJson = "[]";
        var failing = Api();
        failing.ProductsResponse = () => throw new HttpRequestException("connection refused");
        await using var noneFactory = new WebAppFactory(none);
        await using var failingFactory = new WebAppFactory(failing);
        using var noneClient = await SignedInAsync(noneFactory);
        using var failingClient = await SignedInAsync(failingFactory);

        (await noneClient.GetStringAsync(PagePath, Token)).ShouldContain("No products are available to connect right now.");
        var failed = WebUtility.HtmlDecode(await failingClient.GetStringAsync(PagePath, Token));
        failed.ShouldContain("We couldn't load the list of products");
        failed.ShouldContain("Admin console", customMessage: "the rest of the page still works");
        failed.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task AnOrganisationThePersonCannotOpenIsExplained()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/choose-product?tenantId={Guid.NewGuid()}", Token));

        html.ShouldContain("You can't open this organisation");
        html.ShouldContain("Ask an admin of the organisation.");
        html.ShouldNotContain("Admin console");
    }

    [Fact]
    public async Task WithoutAnOrganisationThePersonIsSentBackToChooseOne()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var response = await client.GetAsync("/choose-product", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldEndWith("/choose-organisation?change=1");
    }

    [Fact]
    public async Task SomeoneSignedOutIsSentToSignIn()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync(PagePath, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldContain("/login");
    }

    [Fact]
    public async Task AnApiThatCannotAnswerIsACalmError()
    {
        var api = Api();
        api.Unreachable = true;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        api.Unreachable = true;

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t open this organisation");
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
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
