using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The platform console's products (UI04_T1, UI04_T2): every product, register, make available or withdraw, where it opens, and replacing its client secret. No real API is involved.</summary>
public sealed class PlatformProductsPageTests
{
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Draft = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private const string PagePath = "/platform/products";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryProductIsListedWithItsStatusPermissionsAndWhereItOpens()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync(PagePath, Token));

        html.ShouldContain("<title>Products · Auth Service</title>");
        html.ShouldContain("Billing");
        html.ShouldContain("Draft");
        html.ShouldContain("Helpdesk");
        html.ShouldContain("Available");
        html.ShouldContain("Not available");
        html.ShouldContain("Organisations can subscribe and people can open it.");
        html.ShouldContain("Add at least one permission, then make it available.");
        html.ShouldContain("Nobody can open it and organisations can't subscribe. Make it available when it is ready.");
        html.ShouldContain("3 permissions");
        html.ShouldContain("0 permissions");
        html.ShouldContain("https://helpdesk.example.com/in");
        html.ShouldContain("Not set. People can't open it from the picker.");
        html.ShouldContain("Showing 3 products");
        html.ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task OnlyTheRightActionsAreOfferedForEachProduct()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain($"?withdraw={Helpdesk}");
        html.ShouldNotContain($"?withdraw={Billing}");
        html.ShouldNotContain($"?withdraw={Draft}");
        html.ShouldContain($"/platform/products/{Draft}/active");
        html.ShouldNotContain($"/platform/products/{Billing}/active", customMessage: "a product with no permissions can't be made available");
        html.ShouldNotContain($"/platform/products/{Helpdesk}/active", customMessage: "withdrawing asks first, so it is a link to a question");
        foreach (var id in new[] { Helpdesk, Billing, Draft })
        {
            html.ShouldContain($"?rotate={id}");
            html.ShouldContain($"?launch={id}");
            html.ShouldContain($"href=\"/platform/products/{id}/permissions\"");
        }
    }

    [Fact]
    public async Task NoProductsShowsAnEmptyStateWithARegisterAction()
    {
        var api = Api();
        api.AdminProductsJson = "[]";
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("No products yet");
        html.ShouldContain("Register a product");
    }

    [Fact]
    public async Task TheListIsReadWithThePersonsOwnSignInToken()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await client.GetStringAsync(PagePath, Token);

        var call = api.AdminCalls.Last(call => call.PathAndQuery == "/api/auth/admin/applications?limit=200");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
    }

    [Fact]
    public async Task RegisteringAProductSendsItsKeyNameAndAudienceAndShowsTheSecretOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?register=1", Token));
        dialog.ShouldContain("Register a product");
        dialog.ShouldContain("It can't be changed later.");

        var posted = await PostPageFormAsync(client, "?register=1", "register", new() { ["name"] = "  Inventory  ", ["key"] = "inventory", ["audience"] = "inventory-api" });

        var call = api.RegisterProductCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"key\":\"inventory\"");
        call.Body.ShouldContain("\"name\":\"Inventory\"");
        call.Body.ShouldContain("\"jwtAudience\":\"inventory-api\"");
        var revealUrl = Where(posted);
        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}&registered=1&app=[0-9a-f-]{{36}}&name=Inventory&client=new-client&aud=inventory-api&perms=0&live=0$");
        revealUrl.ShouldNotContain(api.ProductSecret, customMessage: "the secret is never in an address");

        var first = WebUtility.HtmlDecode(await client.GetStringAsync(revealUrl, Token));
        first.ShouldContain("Product registered");
        first.ShouldContain("Inventory is registered");
        first.ShouldContain("It isn't available yet. Next, give it its permissions, then make it available.");
        first.ShouldContain($"value=\"{api.ProductSecret}\"");
        first.ShouldContain("You won't see this again");
        first.ShouldContain("Client id: <code class=\"mono\">new-client</code>");
        first.ShouldContain("Add its permissions");
        Regex.IsMatch(first, "href=\"/platform/products/[0-9a-f-]{36}/permissions\"").ShouldBeTrue("the next step is one click away");

        var reloaded = await client.GetStringAsync(revealUrl, Token);
        reloaded.ShouldNotContain(api.ProductSecret);
        reloaded.ShouldNotContain("Product registered");
    }

    [Fact]
    public async Task ARegistrationWithMissingFieldsNeverReachesTheApiAndKeepsWhatWasTyped()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostPageFormAsync(client, "?register=1", "register", new() { ["name"] = "Inventory", ["key"] = "", ["audience"] = " " });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("Enter a key for the product.");
        html.ShouldContain("Enter the token audience for the product.");
        html.ShouldContain("value=\"Inventory\"");
        api.RegisterProductCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisReasonsForRefusingARegistrationAreExplainedAndWhatWasTypedIsKept()
    {
        var cases = new (Func<string, HttpResponseMessage?> Respond, string Expect)[]
        {
            (_ => Problem(HttpStatusCode.Conflict, "application_key_taken"), "A product with this key already exists."),
            (_ => Problem(HttpStatusCode.Conflict, "audience_taken"), "Another product already uses this token audience."),
            (_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status":400,"title":"x","errors":{"Key":["Key must be 1 to 63 lowercase letters, digits or hyphens."]}}""", Encoding.UTF8, "application/problem+json"),
            }, "Key must be 1 to 63 lowercase letters, digits or hyphens."),
            (_ => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to register products."),
            (_ => throw new HttpRequestException("connection refused"), "Nothing was registered."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api();
            api.RegisterProductResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var response = await PostPageFormAsync(client, "?register=1", "register", new() { ["name"] = "Inventory", ["key"] = "inventory", ["audience"] = "inventory-api" });
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldContain("value=\"inventory-api\"");
            html.ShouldNotContain("connection refused");
            html.ShouldNotContain(api.ProductSecret);
        }
    }

    [Fact]
    public async Task EverythingFilledInRegistersTheProductItsPermissionsAndItsAddressInOneCallAndSaysItIsAvailable()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?register=1", Token));
        dialog.ShouldContain("Where it opens (optional)");
        dialog.ShouldContain("Permissions (optional)");
        dialog.ShouldContain("Make it available now");
        dialog.ShouldContain("Leave it empty to use the key plus -api.");

        var posted = await PostPageFormAsync(client, "?register=1", "register", new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "",
            ["launchUrl"] = " http://localhost:5080/api/session/launch ",
            ["permissions"] = "campaign.view | View campaigns | See campaigns\ncampaign.send | Send campaigns",
            ["activate"] = "on",
        });

        var call = api.RegisterProductCalls.ShouldHaveSingleItem();
        call.Body.ShouldContain("\"jwtAudience\":\"marketing-api\"", customMessage: "an empty audience follows the key");
        call.Body.ShouldContain("\"launchUrl\":\"http://localhost:5080/api/session/launch\"");
        call.Body.ShouldContain("\"key\":\"campaign.view\"");
        call.Body.ShouldContain("\"description\":\"See campaigns\"");
        call.Body.ShouldContain("\"key\":\"campaign.send\"");
        call.Body.ShouldContain("\"activate\":true");
        var revealUrl = Where(posted);
        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}&registered=1&app=[0-9a-f-]{{36}}&name=Marketing&client=new-client&aud=marketing-api&perms=2&live=1&opens=http%3A%2F%2Flocalhost%3A5080%2Fapi%2Fsession%2Flaunch$");
        revealUrl.ShouldNotContain(api.ProductSecret);

        var panel = WebUtility.HtmlDecode(await client.GetStringAsync(revealUrl, Token));
        panel.ShouldContain("Marketing is registered and available");
        panel.ShouldContain("Token audience: <code class=\"mono\">marketing-api</code>");
        panel.ShouldContain("Client id: <code class=\"mono\">new-client</code>");
        panel.ShouldContain("Opens at: <code class=\"mono\">http://localhost:5080/api/session/launch</code>");
        panel.ShouldContain("Permissions: 2 permissions registered");
        panel.ShouldContain($"value=\"{api.ProductSecret}\"");
        panel.ShouldNotContain("Add its permissions", customMessage: "the permissions are already in");
    }

    [Fact]
    public async Task PermissionsPastedAsJsonAreReadAndSent()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        await PostPageFormAsync(client, "?register=1", "register", new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "marketing-api",
            ["permissions"] = """{"permissions":[{"key":"contact.view","label":"View contacts","description":"See contacts"},{"key":"user.manage","label":"Manage users"}],"roles":[]}""",
            ["activate"] = "on",
        });

        var body = api.RegisterProductCalls.ShouldHaveSingleItem().Body;
        body.ShouldContain("\"key\":\"contact.view\"");
        body.ShouldContain("\"label\":\"View contacts\"");
        body.ShouldContain("\"key\":\"user.manage\"");
        body.ShouldContain("\"activate\":true");
    }

    [Fact]
    public async Task WithoutTheAvailableBoxTheProductIsRegisteredButNotMadeAvailable()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostPageFormAsync(client, "?register=1", "register", new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "marketing-api",
            ["permissions"] = "campaign.view | View campaigns",
        });

        api.RegisterProductCalls.ShouldHaveSingleItem().Body.ShouldNotContain("\"activate\":true");
        var panel = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        panel.ShouldContain("Marketing is registered");
        panel.ShouldNotContain("registered and available");
        panel.ShouldContain("It isn't available yet. Make it available when it is ready.");
        panel.ShouldContain("Permissions: 1 permission registered");
    }

    [Fact]
    public async Task TheAvailableBoxWithNoPermissionsIsNotSentSoTheApiDoesNotRefuseTheRegistration()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostPageFormAsync(client, "?register=1", "register", new() { ["name"] = "Marketing", ["key"] = "marketing", ["audience"] = "", ["activate"] = "on" });

        api.RegisterProductCalls.ShouldHaveSingleItem().Body.ShouldNotContain("\"activate\":true");
        var panel = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        panel.ShouldContain("It isn't available yet. Next, give it its permissions, then make it available.");
        panel.ShouldContain("Add its permissions");
    }

    [Fact]
    public async Task APermissionListThatCannotBeReadNeverReachesTheApiAndWhatWasTypedIsKept()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostPageFormAsync(client, "?register=1", "register", new()
        {
            ["name"] = "Marketing",
            ["key"] = "marketing",
            ["audience"] = "marketing-api",
            ["permissions"] = "campaign.view | View campaigns\nthis line has no label",
        });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Fix 1 problem");
        html.ShouldContain("Line 2: write a key and a label separated by a | sign.");
        html.ShouldContain("this line has no label");
        html.ShouldContain("value=\"marketing-api\"");
        api.RegisterProductCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task EveryProductOffersARenameAndTheDialogSaysTheKeyAndAudienceStay()
    {
        await using var factory = new WebAppFactory(Api());
        using var client = await SignedInAsync(factory);

        var list = await client.GetStringAsync(PagePath, Token);
        foreach (var id in new[] { Helpdesk, Billing, Draft })
        {
            list.ShouldContain($"?rename={id}");
        }

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?rename={Helpdesk}", Token));
        dialog.ShouldContain("Rename 'Helpdesk'");
        dialog.ShouldContain("value=\"Helpdesk\"");
        dialog.ShouldContain("Its key, <code class=\"mono\">helpdesk</code>, and its token audience stay as they are");
    }

    [Fact]
    public async Task RenamingAsksTheApiAsTheAdminThenSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostPageFormAsync(client, $"?rename={Helpdesk}", "rename", new() { ["id"] = Helpdesk.ToString(), ["name"] = "  Staff Link Helpdesk  " });

        var call = api.RenameCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/name");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"name\":\"Staff Link Helpdesk\"");
        Where(posted).ShouldBe($"{PagePath}?notice=renamed&app={Helpdesk}");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain("is renamed");
    }

    [Fact]
    public async Task ABlankNameNeverReachesTheApi()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostPageFormAsync(client, $"?rename={Helpdesk}", "rename", new() { ["id"] = Helpdesk.ToString(), ["name"] = "   " });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Enter a name for the product.");
        html.ShouldContain("role=\"dialog\"");
        api.RenameCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheApisReasonsForRefusingARenameAreExplainedInTheDialog()
    {
        var cases = new (Func<string, HttpResponseMessage?> Respond, string Expect)[]
        {
            (_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"status":400,"title":"x","errors":{"Name":["Name must be at most 200 characters."]}}""", Encoding.UTF8, "application/problem+json"),
            }, "Name must be at most 200 characters."),
            (_ => Problem(HttpStatusCode.NotFound, "application_not_found"), "That product isn't here any more."),
            (_ => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to change products."),
            (_ => throw new HttpRequestException("connection refused"), "Nothing was changed."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api();
            api.RenameResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var response = await PostPageFormAsync(client, $"?rename={Helpdesk}", "rename", new() { ["id"] = Helpdesk.ToString(), ["name"] = "New name" });
            var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

            html.ShouldContain(expect);
            html.ShouldContain("value=\"New name\"");
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task MakingAProductAvailableAsksTheApiAsTheAdminThenSaysSo()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var posted = await PostActionAsync(client, $"/platform/products/{Draft}/active", new() { ["isActive"] = "true" });

        var call = api.SetActiveCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Draft}/active");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"isActive\":true");
        Where(posted).ShouldBe($"{PagePath}?notice=available&app={Draft}");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain("Draft is available");
    }

    [Fact]
    public async Task WithdrawingAsksFirstNamingWhatHappensThenWithdraws()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?withdraw={Helpdesk}", Token));
        dialog.ShouldContain("Withdraw 'Helpdesk'?");
        dialog.ShouldContain("Nobody will be able to open it, and organisations can't subscribe to it");
        dialog.ShouldContain("Existing subscriptions and seats aren't changed.");
        api.SetActiveCalls.ShouldBeEmpty();

        var posted = await PostFormInDialogAsync(client, $"?withdraw={Helpdesk}", $"/{Helpdesk}/active\"", []);

        api.SetActiveCalls.ShouldHaveSingleItem().Body.ShouldContain("\"isActive\":false");
        Where(posted).ShouldBe($"{PagePath}?notice=withdrawn&app={Helpdesk}");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token)).ShouldContain("Helpdesk is withdrawn");
        foreach (var id in new[] { Billing, Draft, Guid.NewGuid() })
        {
            (await client.GetStringAsync($"{PagePath}?withdraw={id}", Token)).ShouldNotContain("role=\"dialog\"", customMessage: "only an available product can be withdrawn");
        }
    }

    [Fact]
    public async Task TheApisReasonsForRefusingAnAvailabilityChangeAreExplained()
    {
        var cases = new (Func<string, HttpResponseMessage?> Respond, string Expect)[]
        {
            (_ => Problem(HttpStatusCode.Conflict, "no_permissions_registered"), "Draft has no permissions yet"),
            (_ => Problem(HttpStatusCode.NotFound, "application_not_found"), "That product isn't here any more"),
            (_ => new HttpResponseMessage(HttpStatusCode.Forbidden), "You don't have permission to do that"),
            (_ => throw new HttpRequestException("connection refused"), "Nothing was changed."),
        };

        foreach (var (respond, expect) in cases)
        {
            var api = Api();
            api.SetActiveResponse = respond;
            await using var factory = new WebAppFactory(api);
            using var client = await SignedInAsync(factory);

            var posted = await PostActionAsync(client, $"/platform/products/{Draft}/active", new() { ["isActive"] = "true" });
            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
        }
    }

    [Fact]
    public async Task TheLaunchAddressIsShownSavedAndCleared()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var dialog = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?launch={Helpdesk}", Token));
        dialog.ShouldContain("Where 'Helpdesk' opens");
        dialog.ShouldContain("value=\"https://helpdesk.example.com/in\"");

        var saved = await PostPageFormAsync(client, $"?launch={Helpdesk}", "launch", new() { ["id"] = Helpdesk.ToString(), ["url"] = " https://helpdesk.example.com/signed-in " });
        var cleared = await PostPageFormAsync(client, $"?launch={Billing}", "launch", new() { ["id"] = Billing.ToString(), ["url"] = "" });

        api.LaunchUrlCalls.Count.ShouldBe(2);
        api.LaunchUrlCalls[0].Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/launch-url");
        api.LaunchUrlCalls[0].Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        api.LaunchUrlCalls[0].Body.ShouldContain("\"url\":\"https://helpdesk.example.com/signed-in\"");
        api.LaunchUrlCalls[1].Body.ShouldContain("\"url\":null");
        Where(saved).ShouldBe($"{PagePath}?notice=launch_saved&app={Helpdesk}");
        Where(cleared).ShouldBe($"{PagePath}?notice=launch_cleared&app={Billing}");
        WebUtility.HtmlDecode(await client.GetStringAsync(Where(saved), Token)).ShouldContain("Helpdesk's address is saved");
    }

    [Fact]
    public async Task ARefusedLaunchAddressIsExplainedInTheDialogAndKept()
    {
        var api = Api();
        api.LaunchUrlResponse = _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"status":400,"title":"x","errors":{"Url":["Url must use https. Plain http is only accepted for localhost."]}}""", Encoding.UTF8, "application/problem+json"),
        };
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var response = await PostPageFormAsync(client, $"?launch={Helpdesk}", "launch", new() { ["id"] = Helpdesk.ToString(), ["url"] = "http://helpdesk.example.com/in" });
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Token));

        html.ShouldContain("Url must use https. Plain http is only accepted for localhost.");
        html.ShouldContain("value=\"http://helpdesk.example.com/in\"");
        html.ShouldContain("role=\"dialog\"");
    }

    [Fact]
    public async Task TheReplaceQuestionSaysWhatStopsWorkingAndSuggestsADay()
    {
        await using var factory = new WebAppFactory(Api(), time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await client.GetStringAsync($"{PagePath}?rotate={Helpdesk}", Token));

        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Replace the client secret for 'Helpdesk'?");
        html.ShouldContain("Until the old secret stops, either works");
        html.ShouldContain("Choose \"Not at all\" if the old secret may have leaked");
        var select = Regex.Match(html, "<select[^>]*name=\"graceHours\"[^>]*>.*?</select>", RegexOptions.Singleline).Value;
        select.ShouldContain("Not at all");
        select.ShouldContain("30 days");
        Regex.IsMatch(select, "<option[^>]*value=\"24\"[^>]*selected|<option[^>]*selected[^>]*value=\"24\"").ShouldBeTrue("24 hours is preselected");
        (await client.GetStringAsync($"{PagePath}?rotate={Guid.NewGuid()}", Token)).ShouldNotContain("role=\"dialog\"");
    }

    [Fact]
    public async Task ReplacingSendsTheChosenGraceAsTheAdminAndShowsTheNewSecretOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, "72");

        var call = api.SecretRotateCalls.ShouldHaveSingleItem();
        call.Path.ShouldBe($"/api/auth/admin/applications/{Helpdesk}/secret/rotate");
        call.Authorization.ShouldBe($"Bearer {api.AccessTokensIssued[0]}");
        call.Body.ShouldContain("\"graceHours\":72");

        var revealUrl = Where(posted);
        revealUrl.ShouldMatch($@"^{Regex.Escape(PagePath)}\?reveal=[0-9a-f]{{32}}&app={Helpdesk}&client=helpdesk-client&oldEnds=");
        revealUrl.ShouldNotContain(api.ProductSecret, customMessage: "the secret is never in an address");
        posted.Headers.ToString().ShouldNotContain(api.ProductSecret);

        var first = WebUtility.HtmlDecode(await client.GetStringAsync(revealUrl, Token));
        first.ShouldContain("New client secret");
        first.ShouldContain($"value=\"{api.ProductSecret}\"");
        first.ShouldContain("You won't see this again");
        first.ShouldContain("Client id: <code class=\"mono\">helpdesk-client</code>");
        first.ShouldContain("The old secret keeps working for a while");
        first.ShouldContain("It stops working at 5 Nov 2026, 12:00 UTC, in 3 days.");
        first.ShouldContain("data-copy-from=\"raw-secret\"");

        var reloaded = await client.GetStringAsync(revealUrl, Token);
        reloaded.ShouldNotContain(api.ProductSecret);
        reloaded.ShouldNotContain("New client secret");
    }

    [Fact]
    public async Task ChoosingNotAtAllSaysTheOldSecretStoppedAtOnce()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, "0");

        Where(posted).ShouldContain("oldEnds=now");
        var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(posted), Token));
        html.ShouldContain("The old secret stopped working at once");
        html.ShouldContain("the product can't prove who it is until it has the new secret");
    }

    [Fact]
    public async Task AnotherSessionCannotTakeTheNewSecret()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var admin = await SignedInAsync(factory);
        using var other = await SignedInAsync(factory);

        var posted = await PostRotateAsync(admin, Helpdesk, "24");

        (await other.GetStringAsync(Where(posted), Token)).ShouldNotContain(api.ProductSecret);
        (await admin.GetStringAsync(Where(posted), Token)).ShouldContain(api.ProductSecret);
    }

    [Fact]
    public async Task NeitherSecretNorTokenIsEverLogged()
    {
        var logs = new CapturingLogs();
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now), logs: logs);
        using var client = await SignedInAsync(factory);

        var rotated = await PostRotateAsync(client, Helpdesk, "24");
        await client.GetStringAsync(Where(rotated), Token);
        var registered = await PostPageFormAsync(client, "?register=1", "register", new() { ["name"] = "Inventory", ["key"] = "inventory", ["audience"] = "inventory-api" });
        await client.GetStringAsync(Where(registered), Token);

        var everything = string.Join('\n', logs.Lines);
        everything.ShouldNotContain(api.ProductSecret);
        everything.ShouldNotContain(api.AccessTokensIssued[0]);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("721")]
    [InlineData("soon")]
    [InlineData("")]
    public async Task AGraceOutsideTheOfferedRangeNeverReachesTheApi(string grace)
    {
        var api = Api();
        await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
        using var client = await SignedInAsync(factory);

        var posted = await PostRotateAsync(client, Helpdesk, grace);

        api.SecretRotateCalls.ShouldBeEmpty();
        Where(posted).ShouldBe($"{PagePath}?error=bad_grace&app={Helpdesk}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Choose how long the old secret keeps working");
    }

    [Fact]
    public async Task TheApisOwnReasonsForRefusingASecretReplacementAreExplainedAndNothingLeaks()
    {
        var gone = Api();
        gone.SecretRotateResponse = _ => Problem(HttpStatusCode.NotFound, "application_not_found");
        var refused = Api();
        refused.SecretRotateResponse = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        var down = Api();
        down.SecretRotateResponse = _ => throw new HttpRequestException("connection refused");
        var cases = new (FakeAuthApi Api, string Expect)[]
        {
            (gone, "That product isn't here any more"),
            (refused, "You don't have permission to do that"),
            (down, "Auth Service isn't responding right now. Nothing was changed."),
        };

        foreach (var (api, expect) in cases)
        {
            await using var factory = new WebAppFactory(api, time: new FakeClock(Now));
            using var client = await SignedInAsync(factory);

            var html = WebUtility.HtmlDecode(await client.GetStringAsync(Where(await PostRotateAsync(client, Helpdesk, "24")), Token));

            html.ShouldContain(expect);
            html.ShouldNotContain("connection refused");
            html.ShouldNotContain(api.ProductSecret);
        }
    }

    [Fact]
    public async Task SomeoneWhoIsNotAPlatformAdminSeesNoProductsAndCannotChangeAnythingEvenByPostingDirectly()
    {
        var api = Api();
        api.IsPlatformAdmin = false;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);
        var page = await client.GetStringAsync("/choose-organisation?change=1", Token);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var response = await client.GetAsync(PagePath, Token);
        var rotate = await client.PostAsync($"{PagePath}/{Helpdesk}/rotate-secret", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("graceHours", "24")]), Token);
        var active = await client.PostAsync($"{PagePath}/{Draft}/active", new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("isActive", "true")]), Token);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Token)).ShouldNotContain("Helpdesk");
        rotate.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        active.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        api.SecretRotateCalls.ShouldBeEmpty();
        api.SetActiveCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task APostWithoutTheAntiForgeryTokenIsRefusedAndNothingIsChanged()
    {
        var api = Api();
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        var rotate = await client.PostAsync($"{PagePath}/{Helpdesk}/rotate-secret", new FormUrlEncodedContent([new("graceHours", "24")]), Token);
        var active = await client.PostAsync($"{PagePath}/{Draft}/active", new FormUrlEncodedContent([new("isActive", "true")]), Token);

        rotate.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        active.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.SecretRotateCalls.ShouldBeEmpty();
        api.SetActiveCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AListTheApiCannotReadIsACalmError()
    {
        var api = Api();
        api.AdminResponse = path => path.StartsWith("/api/auth/admin/applications", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null;
        await using var factory = new WebAppFactory(api);
        using var client = await SignedInAsync(factory);

        (await client.GetStringAsync(PagePath, Token)).ShouldContain("We couldn&#x27;t load the products");
    }

    private static HttpResponseMessage Problem(HttpStatusCode status, string code) => new(status)
    {
        Content = new StringContent($$"""{"status":{{(int)status}},"title":"x","detail":"x","code":"{{code}}"}""", Encoding.UTF8, "application/problem+json"),
    };

    /// <summary>Opens a dialog that posts to the page itself (a Blazor form), fills it in, and posts it as a browser would.</summary>
    private static async Task<HttpResponseMessage> PostPageFormAsync(HttpClient client, string query, string handler, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(PagePath + query, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("name=\"_handler\"") && candidate.Contains($"value=\"{handler}\""));
        var body = HiddenFields(form);
        body.RemoveAll(field => fields.ContainsKey(field.Key));
        body.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync(PagePath + query, new FormUrlEncodedContent(body), Token);
    }

    /// <summary>Opens a dialog whose form posts to its own address, and posts it.</summary>
    private static async Task<HttpResponseMessage> PostFormInDialogAsync(HttpClient client, string query, string formContains, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(PagePath + query, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains(formContains));
        var target = WebUtility.HtmlDecode(Regex.Match(form, "action=\"([^\"]+)\"").Groups[1].Value);
        var body = HiddenFields(form);
        body.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync(target, new FormUrlEncodedContent(body), Token);
    }

    /// <summary>Posts a row's own small form (for example Make available) from the list page.</summary>
    private static async Task<HttpResponseMessage> PostActionAsync(HttpClient client, string action, Dictionary<string, string> fields)
    {
        var page = await client.GetStringAsync(PagePath, Token);
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains($"action=\"{action}\""));
        var body = HiddenFields(form);
        body.RemoveAll(field => fields.ContainsKey(field.Key));
        body.AddRange(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)));
        return await client.PostAsync(action, new FormUrlEncodedContent(body), Token);
    }

    private static List<KeyValuePair<string, string>> HiddenFields(string form)
    {
        var body = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            body.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        return body;
    }

    private static async Task<HttpResponseMessage> PostRotateAsync(HttpClient client, Guid product, string grace) =>
        await PostFormInDialogAsync(client, $"?rotate={product}", "/rotate-secret\"", new() { ["graceHours"] = grace });

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static FakeAuthApi Api() => new()
    {
        IsPlatformAdmin = true,
        MembershipsJson = """[{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","tenantName":"Acme Ltd","status":"Active"}]""",
        MembersJson = "[]",
        AdminProductsJson = $$"""
            [
              {"id":"{{Billing}}","key":"billing","name":"Billing","isActive":false,"permissionCount":0,"launchUrl":null,"createdAt":"2026-10-01T10:00:00Z"},
              {"id":"{{Draft}}","key":"draft","name":"Draft","isActive":false,"permissionCount":2,"launchUrl":null,"createdAt":"2026-10-02T10:00:00Z"},
              {"id":"{{Helpdesk}}","key":"helpdesk","name":"Helpdesk","isActive":true,"permissionCount":3,"launchUrl":"https://helpdesk.example.com/in","createdAt":"2026-10-03T10:00:00Z"}
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
