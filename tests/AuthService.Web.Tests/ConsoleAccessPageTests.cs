using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's product access (UI02_T7): who can open each product, with seats. No real API is involved.</summary>
public sealed class ConsoleAccessPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Anna = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid Priya = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid Lena = Guid.Parse("c3333333-3333-4333-8333-333333333333");
    private static readonly Guid Sam = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Analytics = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static readonly Guid Inventory = Guid.Parse("20000000-0000-4000-8000-000000000004");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/access";

    [Fact]
    public async Task EachProductShowsItsStatusItsSeatsAndWhoHasAccess()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("<title>Product access · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Product access</h1>""");
        html.ShouldContain("Helpdesk");
        html.ShouldContain("2 of 10 seats used");
        html.ShouldContain("3 of 3 seats used");
        html.ShouldContain("No seats left");
        html.ShouldContain("4 of 5 seats used");
        html.ShouldContain("Almost full");
        html.ShouldContain("1 seat used · no seat limit");
        html.ShouldContain("""role="progressbar" """.TrimEnd());
        html.ShouldContain("Priya Shah");
        html.ShouldContain("Disabled · doesn't use a seat");
        html.ShouldContain("Nobody has access yet.");
        html.ShouldContain("Suspended");
        html.ShouldContain("This subscription is suspended, so nobody can be given access until it is active again.");
    }

    [Fact]
    public async Task OnlyPeopleWithoutAccessWhoAreNotDisabledCanBeGivenIt()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        var helpdeskForm = Regex.Match(html, $"<form[^>]*access/{Helpdesk}/grant[^>]*>.*?</form>", RegexOptions.Singleline).Value;
        helpdeskForm.ShouldContain("Anna Kowalski");
        helpdeskForm.ShouldContain("sam@acme.com (invited)");
        helpdeskForm.ShouldNotContain("Priya Shah", customMessage: "she already has Helpdesk");
        helpdeskForm.ShouldNotContain("Lena Fischer", customMessage: "a disabled person cannot be given access");
    }

    [Fact]
    public async Task AFullProductCannotBeGivenToAnyoneAndSaysWhy()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("All 3 seats are in use.");
        html.ShouldNotContain($"access/{Billing}/grant");
    }

    [Fact]
    public async Task GivingAccessAsksTheApiAsTheAdminAndSaysWhenItTakesEffect()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync(PagePath, Token);

        var posted = await PostGrantAsync(client, page, Helpdesk, Anna);

        Where(posted).ShouldBe($"{PagePath}?notice=granted&m={Anna}&a={Helpdesk}");
        var call = api.AccessCalls.ShouldHaveSingleItem();
        call.Method.ShouldBe("PUT");
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/tenants/me/members/{Anna}/applications/{Helpdesk}");
        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain("Anna Kowalski can open Helpdesk");
        html.ShouldContain("within about 15 minutes");
    }

    [Fact]
    public async Task GivingAccessWithoutChoosingAPersonChangesNothing()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync(PagePath, Token);

        var posted = await PostGrantAsync(client, page, Helpdesk, null);

        Where(posted).ShouldBe($"{PagePath}?error=choose_member&a={Helpdesk}");
        api.AccessCalls.ShouldBeEmpty();
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Choose a person first");
    }

    [Fact]
    public async Task RemovingAccessAsksFirstAndSaysWhatItDoes()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?revoke={Priya}&app={Helpdesk}", Token);

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Remove Priya Shah&#x27;s access to Helpdesk?");
        html.ShouldContain("their seat is freed");
        html.ShouldContain("within about 15 minutes");
        html.ShouldContain("btn btn-danger");
        api.AccessCalls.ShouldBeEmpty("asking changes nothing");
    }

    [Fact]
    public async Task ConfirmingRemovesAccessAsTheAdminAndSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{PagePath}?revoke={Priya}&app={Helpdesk}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/access/{Helpdesk}/{Priya}/revoke");

        Where(posted).ShouldBe($"{PagePath}?notice=revoked&m={Priya}&a={Helpdesk}");
        var call = api.AccessCalls.ShouldHaveSingleItem();
        call.Method.ShouldBe("DELETE");
        call.Path.ShouldBe($"/api/auth/tenants/me/members/{Priya}/applications/{Helpdesk}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Priya Shah can&#x27;t open Helpdesk any more");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "no_seat_available", "no_seat_available", "All seats are in use. Remove someone's access to free a seat")]
    [InlineData(HttpStatusCode.Conflict, "membership_disabled", "membership_disabled", "A disabled person can't be given access. Enable them in Members first.")]
    [InlineData(HttpStatusCode.BadRequest, "application_not_subscribed", "application_not_subscribed", "no active subscription to this product")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "not_found", "Someone may have removed them.")]
    [InlineData(HttpStatusCode.Forbidden, "other", "forbidden", "'Product access · Full access' permission")]
    public async Task EachRefusalIsExplainedInPlainWords(HttpStatusCode status, string code, string expectedCode, string expected)
    {
        var api = Api();
        api.AccessResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync(PagePath, Token);

        var posted = await PostGrantAsync(client, page, Helpdesk, Anna);

        Where(posted).ShouldBe($"{PagePath}?error={expectedCode}&m={Anna}&a={Helpdesk}");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain(expected);
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedLeavesEverythingAsItWasAndSaysSo()
    {
        var api = Api();
        api.AccessResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync(PagePath, Token);

        var posted = await PostGrantAsync(client, page, Helpdesk, Anna);
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));

        html.ShouldContain("We couldn't make that change");
        html.ShouldContain("Nothing was changed.");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task WithoutPermissionToChangeAccessEveryChangeIsDisabledWithTheReason()
    {
        var api = Api();
        api.ConsolePermissions = ["access.manage:ReadOnly", "subscriptions.read:ReadOnly", "members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("Priya Shah");
        html.ShouldContain("permission to change who can open a product.");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Give access</button>").ShouldBeTrue();
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Remove access</button>").ShouldBeTrue();
        html.ShouldNotContain("/grant\"");
        html.ShouldNotContain("?revoke=");
    }

    [Fact]
    public async Task WithoutPermissionToSeeSubscriptionsThePageSaysWhatItNeeds()
    {
        var api = Api();
        api.ConsolePermissions = ["access.manage:Full", "members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("You can&#x27;t see product access");
        html.ShouldNotContain("product-card");
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenChangesNothing()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath, Token);

        var posted = await client.PostAsync($"/console/{Acme}/access/{Helpdesk}/grant", new FormUrlEncodedContent(new Dictionary<string, string> { ["membershipId"] = Anna.ToString() }), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.AccessCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheNavigationOffersProductAccessOnlyToSomeoneWhoMayReadIt()
    {
        var allowed = Api();
        var denied = Api();
        denied.ConsolePermissions = ["members.read:ReadOnly"];
        await using var allowedFactory = new WebAppFactory(allowed);
        await using var deniedFactory = new WebAppFactory(denied);
        using var allowedClient = await SignedInAsync(allowedFactory);
        using var deniedClient = await SignedInAsync(deniedFactory);

        var forAllowed = await allowedClient.GetStringAsync($"/console/{Acme}/members", Token);
        var forDenied = await deniedClient.GetStringAsync($"/console/{Acme}/members", Token);

        forAllowed.ShouldContain($"""href="/console/{Acme}/access" """.TrimEnd());
        forDenied.ShouldNotContain($"""href="/console/{Acme}/access" """.TrimEnd());
    }

    private static async Task<HttpResponseMessage> PostGrantAsync(HttpClient client, string page, Guid product, Guid? person)
    {
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value)
            .First(candidate => candidate.Contains($"access/{product}/grant"));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        fields.Add(new("membershipId", person?.ToString() ?? ""));
        return await client.PostAsync($"/console/{Acme}/access/{product}/grant", new FormUrlEncodedContent(fields), Token);
    }

    private static FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        SubscriptionsJson = $$"""
            [
              {"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":10,"seatsUsed":2},
              {"applicationId":"{{Billing}}","applicationKey":"billing","applicationName":"Billing","status":"Active","maxSeats":3,"seatsUsed":3},
              {"applicationId":"{{Inventory}}","applicationKey":"inventory","applicationName":"Inventory","status":"Active","maxSeats":5,"seatsUsed":4},
              {"applicationId":"{{Analytics}}","applicationKey":"analytics","applicationName":"Analytics","status":"Suspended","maxSeats":null,"seatsUsed":0},
              {"applicationId":"20000000-0000-4000-8000-000000000005","applicationKey":"wiki","applicationName":"Wiki","status":"Active","maxSeats":null,"seatsUsed":1}
            ]
            """,
        MembersJson = $$"""
            [
              {"membershipId":"{{Priya}}","email":"priya@acme.com","displayName":"Priya Shah","status":"Active","roles":[],"applications":[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk"}],"createdAt":"2026-10-01T10:00:00Z"},
              {"membershipId":"{{Anna}}","email":"anna@acme.com","displayName":"Anna Kowalski","status":"Active","roles":[],"applications":[{"applicationId":"{{Billing}}","applicationKey":"billing"}],"createdAt":"2026-09-01T10:00:00Z"},
              {"membershipId":"{{Sam}}","email":"sam@acme.com","displayName":null,"status":"Invited","roles":[],"applications":[],"createdAt":"2026-11-01T10:00:00Z"},
              {"membershipId":"{{Lena}}","email":"lena@acme.com","displayName":"Lena Fischer","status":"Disabled","roles":[],"applications":[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk"}],"createdAt":"2026-08-01T10:00:00Z"}
            ]
            """,
    };

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = NewClient(factory);
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
