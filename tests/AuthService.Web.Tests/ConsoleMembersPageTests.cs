using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's members list (UI02_T1): who is in the organisation, and enabling and disabling them. No real API is involved.</summary>
public sealed class ConsoleMembersPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Anna = Guid.Parse("a1111111-1111-4111-8111-111111111111");
    private static readonly Guid Priya = Guid.Parse("b2222222-2222-4222-8222-222222222222");
    private static readonly Guid Lena = Guid.Parse("c3333333-3333-4333-8333-333333333333");
    private static readonly Guid Sam = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string MembersPath => $"/console/{Acme}/members";

    [Fact]
    public async Task SomeoneNotSignedInIsSentToSignInAndBackHere()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = NewClient(factory);

        var response = await client.GetAsync(MembersPath, Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe($"/login?returnUrl=%2Fconsole%2F{Acme}%2Fmembers");
    }

    [Fact]
    public async Task AnAdminSeesTheMembersWithStatusRolesAndProductsAsThemselvesNotWithTheSignInToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("<title>Members · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Members</h1>""");
        html.ShouldContain("Anna Kowalski");
        html.ShouldContain("priya@acme.com");
        html.IndexOf("Anna Kowalski", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("Priya Shah", StringComparison.Ordinal), "listed by name");
        html.ShouldContain("Active");
        html.ShouldContain("Invited");
        html.ShouldContain("Disabled");
        html.ShouldContain("chip chip-owner");
        html.ShouldContain("helpdesk, billing");
        html.ShouldContain("""data-label="Status" """.TrimEnd(), customMessage: "cells carry their labels so the table becomes cards on small screens");
        html.ShouldContain($"""href="/console/{Acme}/invitations">""");
        html.ShouldContain("Showing 4 of 4 members");

        // The exchange was made as the signed-in person, for the Auth Service product in this organisation.
        var exchange = api.ExchangeCalls.ShouldHaveSingleItem();
        exchange.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        exchange.Body.ShouldBe($$"""{"tenantId":"{{Acme}}","applicationId":"00000000-0000-7000-8000-00000000a07a"}""");
        api.MembersAuthorizations.ShouldHaveSingleItem().ShouldBe($"Bearer {api.ProductTokensIssued[0]}", "the list is read with the console token");
        html.ShouldNotContain(api.ProductTokensIssued[0]);
    }

    [Fact]
    public async Task TheShellNamesTheOrganisationAndOnlyOffersWhatExistsAndMayBeRead()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("Admin console");
        html.ShouldContain("""<nav aria-label="Main" """.TrimEnd());
        html.ShouldContain($"""href="/console/{Acme}/members" """.TrimEnd());
        html.ShouldContain("""aria-current="page" """.TrimEnd());
        html.ShouldContain("Acme Ltd");
        html.ShouldContain("Switch organisation");
        html.ShouldContain("Skip to content");
        html.ShouldNotContain($"/console/{Acme}/subscriptions", customMessage: "pages that are not built yet are not offered");
    }

    [Fact]
    public async Task TheConsoleTokenIsKeptSoEachPageDoesNotTradeForANewOne()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(MembersPath, Token);
        await client.GetStringAsync(MembersPath, Token);

        api.ExchangeCalls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SomeoneWhoIsNotAnActiveMemberIsToldTheyHaveNoAccess()
    {
        var api = Api();
        api.MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Invited"}]""";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("You don't have access");
        html.ShouldContain("""href="/choose-organisation?change=1">Choose an organisation</a>""");
        api.ExchangeCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnExchangeTheApiRefusesIsTheSameNoAccess()
    {
        var api = Api();
        api.ExchangeResponse = () => Problem(HttpStatusCode.Forbidden, "exchange_denied");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("You don't have access");
        api.MembersAuthorizations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReadOnlyAdminSeesTheListButEveryChangeIsDisabledWithTheReason()
    {
        var api = Api();
        api.ConsolePermissions = ["members.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("Priya Shah");
        html.ShouldContain("permission to invite people.\"");
        html.ShouldContain("permission to manage members.\"");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Disable</button>").ShouldBeTrue();
        html.ShouldNotContain("?disable=", customMessage: "there is nothing to click that the API would refuse");
        Regex.IsMatch(html, "<a[^>]*btn-primary[^>]*>.*?Invite people", RegexOptions.Singleline).ShouldBeFalse("Invite people is not a link when it is not allowed");
    }

    [Fact]
    public async Task SomeoneWithoutPermissionToReadMembersIsToldWhichPermissionTheyNeed()
    {
        var api = Api();
        api.ConsolePermissions = ["roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("You can&#x27;t see members");
        html.ShouldContain("You need the 'Members · Read only' permission.");
        api.MembersAuthorizations.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnOrganisationWithNoOtherMembersShowsAnEmptyState()
    {
        var api = Api();
        api.MembersJson = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("It&#x27;s just you so far");
        html.ShouldContain("Invite your team");
    }

    [Fact]
    public async Task SearchingAndFilteringNarrowTheListAndNoMatchesCanBeCleared()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var byText = await client.GetStringAsync($"{MembersPath}?q=priya", Token);
        var byStatus = await client.GetStringAsync($"{MembersPath}?status=Disabled", Token);
        var none = await client.GetStringAsync($"{MembersPath}?q=nobody", Token);

        byText.ShouldContain("Priya Shah");
        byText.ShouldNotContain("Lena Fischer");
        byText.ShouldContain("Showing 1 of 4 members");
        byStatus.ShouldContain("Lena Fischer");
        byStatus.ShouldNotContain("Priya Shah");
        none.ShouldContain("No members match");
        none.ShouldContain($"""href="{MembersPath}">Clear filters</a>""");
    }

    [Fact]
    public async Task DisablingAsksFirstAndSaysWhatItDoes()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token);

        html.ShouldContain("""role="dialog" """.TrimEnd());
        html.ShouldContain("Disable Priya Shah?");
        html.ShouldContain("until you enable them again");
        html.ShouldContain("their seats are freed");
        html.ShouldContain("""value="Disabled" """.TrimEnd());
        html.ShouldContain("""btn btn-danger""");
        html.ShouldContain($"""href="{MembersPath}" autofocus>Cancel</a>""");
        api.StatusCalls.ShouldBeEmpty("asking changes nothing");
    }

    [Fact]
    public async Task ConfirmingDisableChangesTheMemberAsTheAdminAndOffersAnUndo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/members/{Priya}/status");

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"{MembersPath}?notice=disabled&m={Priya}");
        var call = api.StatusCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/tenants/me/members/{Priya}");
        call.Body.ShouldBe("""{"status":"Disabled"}""");

        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain("Priya Shah is disabled");
        html.ShouldContain("sign in until you enable them again.");
        html.ShouldContain(">Undo</button>");
    }

    [Fact]
    public async Task EnablingADisabledMemberNeedsNoQuestion()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(MembersPath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/members/{Lena}/status");

        Where(posted).ShouldBe($"{MembersPath}?notice=enabled&m={Lena}");
        api.StatusCalls.ShouldHaveSingleItem().Body.ShouldBe("""{"status":"Active"}""");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Lena Fischer is enabled");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "last_owner", "last_owner", "Acme Ltd needs at least one Owner. Make someone else an Owner first.")]
    [InlineData(HttpStatusCode.Forbidden, "owner_role_restricted", "owner_role_restricted", "Only an Owner can disable an Owner.")]
    [InlineData(HttpStatusCode.Conflict, "membership_invited", "membership_invited", "They become active when they accept their invitation")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "not_found", "Someone may have removed them.")]
    [InlineData(HttpStatusCode.Forbidden, "something_else", "forbidden", "permission to manage members.")]
    public async Task EachRefusalFromTheApiIsExplainedInPlainWords(HttpStatusCode status, string code, string expectedCode, string expectedText)
    {
        var api = Api();
        api.StatusResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/members/{Priya}/status");

        Where(posted).ShouldBe($"{MembersPath}?error={expectedCode}&m={Priya}");
        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain(expectedText);
        html.ShouldNotContain("exchange_denied");
    }

    [Fact]
    public async Task ASeatShortageNamesTheProductsThatAreFull()
    {
        var api = Api();
        api.StatusResponse = () => Problem(HttpStatusCode.Conflict, "no_seat_available", "The member cannot be enabled because there is no free seat in: helpdesk, billing.");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(MembersPath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/members/{Lena}/status");
        var html = await client.GetStringAsync(Where(posted), Token);

        html.ShouldContain("Lena Fischer can&#x27;t be enabled yet");
        html.ShouldContain("There is no free seat in helpdesk, billing. Free a seat or raise the limit first.");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedForAChangeLeavesEverythingAsItWasAndSaysSo()
    {
        var api = Api();
        api.StatusResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/members/{Priya}/status");
        var html = await client.GetStringAsync(Where(posted), Token);

        html.ShouldContain("We couldn&#x27;t make that change");
        html.ShouldContain("Nothing was changed.");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ATokenTheApiTurnsDownIsReplacedOnTheNextPage()
    {
        var api = Api();
        api.StatusResponse = () => new HttpResponseMessage(HttpStatusCode.Unauthorized);
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/members/{Priya}/status");
        await client.GetStringAsync(MembersPath, Token);

        Where(posted).ShouldBe(MembersPath);
        api.ExchangeCalls.Count.ShouldBe(2, "a fresh console token was traded for");
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenChangesNothing()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(MembersPath, Token);

        var posted = await client.PostAsync($"/console/{Acme}/members/{Priya}/status", new FormUrlEncodedContent(new Dictionary<string, string> { ["status"] = "Disabled" }), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.StatusCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AMemberListThatCannotBeLoadedShowsACalmErrorWithAReference()
    {
        var api = Api();
        api.MembersResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(MembersPath, Token);

        html.ShouldContain("We couldn&#x27;t load the members");
        html.ShouldContain("Your data is safe.");
        Regex.IsMatch(html, "Reference <code>[a-z0-9]{8}</code>").ShouldBeTrue();
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task TheConsoleTokenIsNeverInThePageOrLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, logs: logs);
        using var client = await SignedInAsync(factory);

        var list = await client.GetStringAsync(MembersPath, Token);
        await WebForms.PostPlainFormAsync(client, await client.GetStringAsync($"{MembersPath}?disable={Priya}", Token), $"/members/{Priya}/status");

        list.ShouldNotContain(api.ProductTokensIssued[0]);
        string.Join('\n', logs.Lines).ShouldNotContain(api.ProductTokensIssued[0]);
    }

    private FakeAuthApi Api() => new()
    {
        MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = $$"""
            [
              {"membershipId":"{{Priya}}","email":"priya@acme.com","displayName":"Priya Shah","status":"Active","roles":[{"id":"{{Guid.NewGuid()}}","name":"Admin"}],"applications":[],"createdAt":"2026-10-01T10:00:00Z"},
              {"membershipId":"{{Anna}}","email":"anna@acme.com","displayName":"Anna Kowalski","status":"Active","roles":[{"id":"{{Guid.NewGuid()}}","name":"Owner"}],"applications":[{"applicationId":"{{Guid.NewGuid()}}","applicationKey":"helpdesk"},{"applicationId":"{{Guid.NewGuid()}}","applicationKey":"billing"}],"createdAt":"2026-09-01T10:00:00Z"},
              {"membershipId":"{{Sam}}","email":"sam@acme.com","displayName":null,"status":"Invited","roles":[],"applications":[],"createdAt":"2026-11-01T10:00:00Z"},
              {"membershipId":"{{Lena}}","email":"lena@acme.com","displayName":"Lena Fischer","status":"Disabled","roles":[],"applications":[],"createdAt":"2026-08-01T10:00:00Z"}
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

    private static HttpResponseMessage Problem(HttpStatusCode status, string code, string detail = "x") => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"{{detail}}","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
