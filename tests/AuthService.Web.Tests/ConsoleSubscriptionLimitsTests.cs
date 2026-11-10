using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>ENT-06 (AUTH-283) The tenant console's Subscriptions page shows each product's plan and limits, marking customised ones, with nothing to edit.</summary>
public sealed class ConsoleSubscriptionLimitsTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Marketing = Guid.Parse("20000000-0000-4000-8000-000000000005");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static string PagePath => $"/console/{Acme}/subscriptions";

    [Fact]
    public async Task ENT06_EachProductShowsItsPlanAndLimitsAndMarksCustomisedOnes()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("Growth plan");
        html.ShouldContain("Reachable contacts: 75,000 contacts");
        html.ShouldContain("Customised for Acme Ltd");
        html.ShouldContain("Asset storage: Unlimited");
        html.ShouldContain("AI journeys: On");
        html.ShouldContain("Contact them to change a plan", Case.Insensitive);
        html.ShouldNotContain("Goodwill");
        api.EntitlementCalls.ShouldContain(call => call.PathAndQuery == $"/api/auth/tenants/me/subscriptions/{Marketing}/entitlements");
        api.EntitlementCalls.ShouldAllBe(call => call.Method == "GET", "the page edits nothing");
    }

    [Fact]
    public async Task ENT06_AProductWhoseLimitsCannotBeReadStillShowsItsSeats()
    {
        var api = Api();
        api.EntitlementResponse = (_, path, _) => path.Contains(Marketing.ToString(), StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("3 of 5 seats used");
        html.ShouldContain("None set");
        html.ShouldNotContain("Growth plan");
    }

    private static FakeAuthApi Api()
    {
        var api = new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = "[]",
            SubscriptionsJson = $$"""
                [
                  {"applicationId":"{{Marketing}}","applicationKey":"marketing","applicationName":"Marketing","status":"Active","maxSeats":5,"seatsUsed":3},
                  {"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":null,"seatsUsed":1}
                ]
                """,
        };
        api.EntitlementJson[$"GET /api/auth/tenants/me/subscriptions/{Marketing}/entitlements"] = $$"""
            {"applicationId":"{{Marketing}}","applicationKey":"marketing","applicationName":"Marketing","status":"Active","planKey":"growth","planName":"Growth","version":3,
             "limits":[
               {"key":"contacts.reachable","label":"Reachable contacts","description":null,"unit":"contacts","value":75000,"overridden":true},
               {"key":"journeys.ai","label":"AI journeys","description":null,"unit":"flag","value":1,"overridden":false},
               {"key":"storage.assets","label":"Asset storage","description":null,"unit":"MB","value":null,"overridden":false}
             ]}
            """;
        return api;
    }

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }
}
