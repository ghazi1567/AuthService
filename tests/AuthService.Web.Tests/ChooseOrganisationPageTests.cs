using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The organisation picker (UI05_T1): the organisations a person belongs to, and creating another. No real API is involved.</summary>
public sealed class ChooseOrganisationPageTests
{
    private const string Acme = "7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11";
    private const string Globex = "3f2a1c44-8b1e-4c55-a0c3-1d4e5f607182";
    private const string Initech = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SomeoneNotSignedInIsSentToSignInAndBackHere()
    {
        await using var factory = new WebAppFactory(new FakeAuthApi());
        using var client = NewClient(factory);

        var response = await client.GetAsync("/choose-organisation", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe("/login?returnUrl=%2Fchoose-organisation");
    }

    [Fact]
    public async Task SomeoneInSeveralOrganisationsPicksOneFromAListAsThemselves()
    {
        var api = new FakeAuthApi { MembershipsJson = Memberships(("Globex", Globex, "Active"), ("Acme Ltd", Acme, "Active")) };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var response = await client.GetAsync("/choose-organisation", Token);
        var html = await response.Content.ReadAsStringAsync(Token);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<title>Choose an organisation · Auth Service</title>");
        html.ShouldContain("""<h1 class="picker-title">Choose an organisation</h1>""");
        html.ShouldContain($"""href="/choose-product?tenantId={Acme}" """.TrimEnd());
        html.ShouldContain($"""href="/choose-product?tenantId={Globex}" """.TrimEnd());
        html.IndexOf("Acme Ltd", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("Globex", StringComparison.Ordinal), "listed by name");
        html.ShouldContain("Create another organisation");
        html.ShouldContain("anna@acme.com");
        html.ShouldContain("Sign out");
        api.MeAuthorizations.ShouldHaveSingleItem().ShouldBe($"Bearer {api.AccessTokensIssued[0]}", "the list is read as the signed-in person");
    }

    [Fact]
    public async Task SomeoneInOneOrganisationIsNotAskedButCanStillSeeTheList()
    {
        var api = new FakeAuthApi { MembershipsJson = Memberships(("Acme Ltd", Acme, "Active")) };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var straightOn = await client.GetAsync("/choose-organisation", Token);
        var list = await client.GetAsync("/choose-organisation?change=1", Token);

        straightOn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(straightOn).ShouldBe($"/choose-product?tenantId={Acme}");
        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await list.Content.ReadAsStringAsync(Token)).ShouldContain("Acme Ltd");
    }

    [Fact]
    public async Task OrganisationsThatCannotBeEnteredAreShownWithWhyAndAreNotLinks()
    {
        var api = new FakeAuthApi
        {
            MembershipsJson = Memberships(("Acme Ltd", Acme, "Active"), ("Globex", Globex, "Invited"), ("Initech", Initech, "Disabled")),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var html = await client.GetStringAsync("/choose-organisation", Token);

        html.ShouldContain("Open the link in your invitation email to join.");
        html.ShouldContain("Your access is turned off. Ask an admin.");
        html.ShouldContain("Invited");
        html.ShouldContain("Disabled");
        html.ShouldNotContain($"/choose-product?tenantId={Globex}");
        html.ShouldNotContain($"/choose-product?tenantId={Initech}");
        html.ShouldContain($"/choose-product?tenantId={Acme}");
    }

    [Fact]
    public async Task SomeoneInNoOrganisationIsToldSoAndOffersToCreateOne()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var html = await client.GetStringAsync("/choose-organisation", Token);

        html.ShouldContain("You&#x27;re not part of an organisation");
        html.ShouldContain("Create one to get started, or ask an admin to invite you.");
        html.ShouldContain("Create an organisation");
        Regex.IsMatch(html, "<details[^>]*\\bopen\\b").ShouldBeTrue("the form is open when there is nothing else to do");
        html.ShouldContain("""<label for="org-name">Organisation name</label>""");
    }

    [Fact]
    public async Task WhenTheOrganisationsCannotBeLoadedThereIsACalmErrorWithAWayToTryAgain()
    {
        var api = new FakeAuthApi { MeResponse = () => throw new HttpRequestException("connection refused") };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var html = await client.GetStringAsync("/choose-organisation", Token);

        html.ShouldContain("We couldn&#x27;t load your organisations");
        html.ShouldContain("Your data is safe.");
        html.ShouldContain("""href="/choose-organisation?change=1">Try again</a>""");
        Regex.IsMatch(html, "Reference <code>[a-z0-9]{8}</code>").ShouldBeTrue("a short reference to quote");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task ASessionTheApiNoLongerAcceptsSendsThePersonBackToSignIn()
    {
        var api = new FakeAuthApi { MeResponse = () => new HttpResponseMessage(HttpStatusCode.Unauthorized) };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var response = await client.GetAsync("/choose-organisation", Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(response).ShouldBe("/login?returnUrl=%2Fchoose-organisation&reason=expired");
    }

    [Fact]
    public async Task CreatingAnOrganisationSendsTheNameAsTheSignedInPersonAndGoesOn()
    {
        var api = new FakeAuthApi
        {
            CreateTenantResponse = () => Json(HttpStatusCode.Created, $$"""{"tenantId":"{{Initech}}","tenantName":"Initech","membershipId":"1b8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d12"}"""),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var posted = await WebForms.PostFormAsync(client, "/choose-organisation", "create", new Dictionary<string, string> { ["org-name"] = " Initech " });

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"/choose-product?tenantId={Initech}");
        var call = api.CreateTenantCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldBe("""{"tenantName":"Initech"}""");
    }

    [Fact]
    public async Task AnEmptyNameIsRefusedBeforeTheApiIsAsked()
    {
        var api = new FakeAuthApi();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var posted = await WebForms.PostFormAsync(client, "/choose-organisation", "create", new Dictionary<string, string> { ["org-name"] = "" });
        var html = await posted.Content.ReadAsStringAsync(Token);

        html.ShouldContain("Enter a name for the organisation.");
        api.CreateTenantCalls.ShouldBeEmpty();
        Regex.IsMatch(html, "<details[^>]*\\bopen\\b").ShouldBeTrue("the form stays open to show its error");
    }

    [Fact]
    public async Task TheApisReasonForARefusedNameShowsBesideTheField()
    {
        var api = new FakeAuthApi
        {
            CreateTenantResponse = () => Json(HttpStatusCode.BadRequest, """{"status":400,"title":"x","errors":{"TenantName":["Tenant name must be at most 200 characters."]}}"""),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory, api);

        var posted = await WebForms.PostFormAsync(client, "/choose-organisation", "create", new Dictionary<string, string> { ["org-name"] = "Initech" });

        (await posted.Content.ReadAsStringAsync(Token)).ShouldContain("Tenant name must be at most 200 characters.");
    }

    [Fact]
    public async Task ADisabledAccountOrAnUnreachableApiGetsItsOwnMessageWhenCreating()
    {
        var disabled = new FakeAuthApi { CreateTenantResponse = () => Json(HttpStatusCode.Forbidden, """{"status":403,"title":"x","detail":"x","code":"account_disabled"}""") };
        var down = new FakeAuthApi { CreateTenantResponse = () => throw new HttpRequestException("connection refused") };

        await using var disabledFactory = new WebAppFactory(disabled);
        using var disabledClient = await SignedInAsync(disabledFactory, disabled);
        var disabledHtml = await (await WebForms.PostFormAsync(disabledClient, "/choose-organisation", "create", new Dictionary<string, string> { ["org-name"] = "Initech" })).Content.ReadAsStringAsync(Token);

        await using var downFactory = new WebAppFactory(down);
        using var downClient = await SignedInAsync(downFactory, down);
        var downHtml = await (await WebForms.PostFormAsync(downClient, "/choose-organisation", "create", new Dictionary<string, string> { ["org-name"] = "Initech" })).Content.ReadAsStringAsync(Token);

        disabledHtml.ShouldContain("This account is turned off");
        downHtml.ShouldContain("We couldn&#x27;t create the organisation");
        downHtml.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task SigningInLeadsStraightToTheOrganisationPicker()
    {
        var api = new FakeAuthApi { MembershipsJson = Memberships(("Acme Ltd", Acme, "Active"), ("Globex", Globex, "Active")) };
        await using var factory = new WebAppFactory(api);
        using var client = NewClient(factory);

        var signedIn = await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password");
        Where(signedIn).ShouldBe("/");

        var home = await client.GetAsync("/", Token);
        Where(home).ShouldBe("/choose-organisation");
    }

    private static HttpClient NewClient(WebAppFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory, FakeAuthApi api)
    {
        var client = NewClient(factory);
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.AccessTokensIssued.ShouldNotBeEmpty();
        return client;
    }

    private static string Memberships(params (string Name, string Id, string Status)[] items) =>
        "[" + string.Join(',', items.Select(item => $$"""{"tenantId":"{{item.Id}}","tenantName":"{{item.Name}}","status":"{{item.Status}}"}""")) + "]";

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, status is HttpStatusCode.OK or HttpStatusCode.Created ? "application/json" : "application/problem+json") };

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
