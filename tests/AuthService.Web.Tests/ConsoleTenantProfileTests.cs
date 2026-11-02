using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The organisation in the console header (UI03_T5): its name and status always shown, and a suspended one made obvious. No real API is involved.</summary>
public sealed class ConsoleTenantProfileTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string MembersPath => $"/console/{Acme}/members";

    [Theory]
    [InlineData("Active", "Active", "badge-success")]
    [InlineData("Trial", "Trial", "badge-info")]
    [InlineData("Suspended", "Suspended", "badge-warning")]
    public async Task TheHeaderShowsTheOrganisationsNameAndStatusInWordsAndAnIcon(string status, string label, string badgeClass)
    {
        var api = Api();
        api.TenantStatus = status;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        var header = Regex.Match(html, "<header class=\"console-header\">.*?</header>", RegexOptions.Singleline).Value;
        header.ShouldContain("Acme Ltd");
        header.ShouldContain($"badge {badgeClass}");
        header.ShouldContain(label);
        header.ShouldContain("<svg", customMessage: "the state is an icon and a word, never colour alone");
    }

    [Fact]
    public async Task TheProfileIsReadWithTheConsoleTokenAndNothingElseDecidesTheTenant()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(MembersPath, Token);

        api.TenantProfileAuthorizations.ShouldHaveSingleItem().ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
    }

    [Fact]
    public async Task TheNameComesFromTheProfile()
    {
        var api = Api();
        api.TenantName = "Acme Holdings";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var header = Regex.Match(await client.GetStringAsync(MembersPath, Token), "<header class=\"console-header\">.*?</header>", RegexOptions.Singleline).Value;

        header.ShouldContain("Acme Holdings");
    }

    [Fact]
    public async Task ASuspendedOrganisationHasABannerOnEveryPageSayingWhatIsBlocked()
    {
        var api = Api();
        api.TenantStatus = "Suspended";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        foreach (var page in new[] { "members", "roles", "api-keys", "subscriptions", "audit" })
        {
            var html = WebUtility.HtmlDecode(await client.GetStringAsync($"/console/{Acme}/{page}", Token));

            html.ShouldContain("Acme Ltd is suspended", customMessage: page);
            html.ShouldContain("People can't open its products, its API keys have stopped working", customMessage: page);
            html.ShouldContain("Contact the platform administrator.", customMessage: page);
            Regex.IsMatch(html, "<div[^>]*role=\"alert\"[^>]*suspended-banner|<div[^>]*suspended-banner[^>]*role=\"alert\"").ShouldBeTrue(page);
        }
    }

    [Theory]
    [InlineData("Active")]
    [InlineData("Trial")]
    public async Task AWorkingOrganisationHasNoBanner(string status)
    {
        var api = Api();
        api.TenantStatus = status;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(MembersPath, Token)).ShouldNotContain("is suspended");
    }

    [Fact]
    public async Task IfTheProfileCannotBeReadThePageStillWorksWithTheNameAndAnUnknownStatus()
    {
        var failing = Api();
        failing.TenantProfileResponse = () => throw new HttpRequestException("connection refused");
        var refused = Api();
        refused.TenantProfileResponse = () => new HttpResponseMessage(HttpStatusCode.NotFound);
        await using var failingFactory = new WebAppFactory(failing);
        await using var refusedFactory = new WebAppFactory(refused);
        using var failingClient = await SignedInAsync(failingFactory);
        using var refusedClient = await SignedInAsync(refusedFactory);

        foreach (var client in new[] { failingClient, refusedClient })
        {
            var html = await client.GetStringAsync(MembersPath, Token);

            html.ShouldContain("Acme Ltd");
            html.ShouldContain("Status unknown");
            html.ShouldNotContain("is suspended");
            html.ShouldNotContain("connection refused");
            html.ShouldContain("""<h1 class="page-title">Members</h1>""");
        }
    }

    [Fact]
    public async Task TheStatusIsAlsoInTheMenuSoItIsThereOnASmallScreen()
    {
        var api = Api();
        api.TenantStatus = "Suspended";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var menu = Regex.Match(await client.GetStringAsync(MembersPath, Token), "<div class=\"menu-panel\">.*?</div>", RegexOptions.Singleline).Value;

        menu.ShouldContain("Organisation status");
        menu.ShouldContain("Suspended");
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
    };

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
