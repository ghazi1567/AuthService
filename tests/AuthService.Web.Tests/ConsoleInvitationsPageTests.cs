using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>The tenant console's invitations (UI02_T2 and UI02_T3): the pending list with resend, and the invite dialog. No real API is involved.</summary>
public sealed class ConsoleInvitationsPageTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid Sam = Guid.Parse("d4444444-4444-4444-8444-444444444444");
    private static readonly Guid Omar = Guid.Parse("e5555555-5555-4555-8555-555555555555");
    private static readonly Guid OwnerRole = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid ViewerRole = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Billing = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid Analytics = Guid.Parse("20000000-0000-4000-8000-000000000003");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string PagePath => $"/console/{Acme}/invitations";

    [Fact]
    public async Task APendingInvitationIsListedWithItsStatusRolesProductsAndDates()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("<title>Invitations · Auth Service</title>");
        html.ShouldContain("""<h1 class="page-title">Invitations</h1>""");
        html.ShouldContain("sam@acme.com");
        html.ShouldContain("omar@acme.com");
        html.ShouldContain("Expired");
        html.ShouldContain("Pending");
        html.ShouldContain("Helpdesk, Billing");
        html.ShouldContain("Priya Shah");
        html.ShouldContain("1 Nov 2026");
        html.ShouldContain("9 Nov 2026");
        html.ShouldContain("Showing 2 invitations");
        html.IndexOf("omar@acme.com", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("sam@acme.com", StringComparison.Ordinal), "expired invitations come first");
    }

    [Fact]
    public async Task WithoutPermissionToReadSubscriptionsProductsAreCounted()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ConsolePermissions = ["members.read:Full", "members.invite:Full", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("2 products");
        html.ShouldNotContain("Helpdesk, Billing");
    }

    [Fact]
    public async Task NoPendingInvitationsShowsAnEmptyState()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.PendingJson = "[]";
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("No pending invitations");
    }

    [Fact]
    public async Task TheResendWaitIsShownAsACountdownFromWhenTheInvitationWasLastSent()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        Regex.Match(html, "data-countdown=\"(?<seconds>\\d+)\"[^>]*>Resend</button>", RegexOptions.Singleline).Success.ShouldBeTrue();
        html.ShouldContain("""data-countdown="40" """.TrimEnd(), customMessage: "sam was sent 20 seconds ago, so 40 are left");
        html.ShouldContain("""data-countdown="0" """.TrimEnd(), customMessage: "omar was sent long ago");
        html.ShouldContain("""data-countdown-label="Resend in {0}s" """.TrimEnd());
    }

    [Fact]
    public async Task ResendingAsksTheApiAsTheAdminAndSaysSo()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(PagePath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/invitations/{Omar}/resend");

        Where(posted).ShouldBe($"{PagePath}?notice=resent&m={Omar}");
        var call = api.ResendCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/tenants/me/invitations/{Omar}/resend");
        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain("Invitation sent again");
        html.ShouldContain("omar@acme.com has a new link and a fresh expiry date.");
    }

    [Fact]
    public async Task ResendingTooSoonSaysHowLongToWait()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ResendResponse = () =>
        {
            var response = Problem(HttpStatusCode.TooManyRequests, "resend_too_soon");
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        };
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(PagePath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/invitations/{Sam}/resend");

        Where(posted).ShouldBe($"{PagePath}?error=resend_too_soon&m={Sam}&wait=42");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("You can send it again in 42 seconds.");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "invitation_not_pending", "invitation_not_pending", "This person has already accepted")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "not_found", "The list below is up to date.")]
    [InlineData(HttpStatusCode.Forbidden, "something_else", "forbidden", "You need the 'Members · Full access' permission to invite people.")]
    public async Task EachOtherRefusalWhenResendingIsExplained(HttpStatusCode status, string code, string expectedCode, string expectedText)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ResendResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(PagePath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/invitations/{Sam}/resend");

        Where(posted).ShouldBe($"{PagePath}?error={expectedCode}&m={Sam}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain(expectedText);
    }

    [Fact]
    public async Task APostedResendWithoutTheAntiForgeryTokenChangesNothing()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath, Token);

        var posted = await client.PostAsync($"/console/{Acme}/invitations/{Sam}/resend", new FormUrlEncodedContent(new Dictionary<string, string>()), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.ResendCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AReadOnlyAdminCanSeeTheListButNotResendOrInvite()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ConsolePermissions = ["members.read:ReadOnly", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("sam@acme.com");
        html.ShouldContain("permission to invite people.\"");
        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Resend</button>").ShouldBeTrue();
        html.ShouldNotContain("/resend\"", customMessage: "nothing to submit that the API would refuse");
        html.ShouldNotContain("?invite=1");
    }

    [Fact]
    public async Task EachPendingInvitationHasAWithdrawLinkThatOpensAConfirmationNamingThePerson()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var list = await client.GetStringAsync(PagePath, Token);
        var dialog = await client.GetStringAsync($"{PagePath}?revoke={Sam}", Token);

        list.ShouldContain($"href=\"{PagePath}?revoke={Sam}\"");
        list.ShouldNotContain("revoke-title", customMessage: "nothing is asked until the link is followed");
        dialog.ShouldContain("Withdraw the invitation for sam@acme.com?");
        dialog.ShouldContain("The link in their email stops working at once");
        dialog.ShouldContain($"action=\"/console/{Acme}/invitations/{Sam}/revoke\"");
        dialog.ShouldContain("Withdraw invitation");
        api.RevokeInvitationCalls.ShouldBeEmpty("opening the question changes nothing");
    }

    [Fact]
    public async Task ConfirmingWithdrawsTheInvitationAsTheAdminAndSaysSo()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{PagePath}?revoke={Omar}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/invitations/{Omar}/revoke");

        Where(posted).ShouldBe($"{PagePath}?notice=revoked");
        var call = api.RevokeInvitationCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Path.ShouldBe($"/api/auth/tenants/me/invitations/{Omar}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Invitation withdrawn");
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "invitation_not_pending", "invitation_not_pending", "nothing to resend or withdraw")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "not_found", "The list below is up to date.")]
    [InlineData(HttpStatusCode.Forbidden, "something_else", "forbidden", "permission")]
    [InlineData(HttpStatusCode.InternalServerError, "something_else", "unavailable", "Nothing was changed")]
    public async Task EachRefusalWhenWithdrawingIsExplained(HttpStatusCode status, string code, string expectedCode, string expectedText)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.RevokeInvitationResponse = () => Problem(status, code);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var dialog = await client.GetStringAsync($"{PagePath}?revoke={Sam}", Token);

        var posted = await WebForms.PostPlainFormAsync(client, dialog, $"/invitations/{Sam}/revoke");

        Where(posted).ShouldBe($"{PagePath}?error={expectedCode}&m={Sam}");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain(expectedText);
    }

    [Fact]
    public async Task ResendingAnExpiredInvitationWhenTheProductIsFullSaysWhatToDo()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ResendResponse = () => Problem(HttpStatusCode.Conflict, "no_seat_available");
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        var list = await client.GetStringAsync(PagePath, Token);

        var posted = await WebForms.PostPlainFormAsync(client, list, $"/invitations/{Omar}/resend");

        Where(posted).ShouldBe($"{PagePath}?error=no_seat_available&m={Omar}");
        var html = await client.GetStringAsync(Where(posted), Token);
        html.ShouldContain("No free seat");
        html.ShouldContain("Free a seat first, then send it again.");
    }

    [Fact]
    public async Task APostedWithdrawWithoutTheAntiForgeryTokenChangesNothing()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);
        await client.GetStringAsync(PagePath, Token);

        var posted = await client.PostAsync($"/console/{Acme}/invitations/{Sam}/revoke", new FormUrlEncodedContent(new Dictionary<string, string>()), Token);

        posted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        api.RevokeInvitationCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AdminsWhoCannotInviteSeeWithdrawDisabledAndGetNoQuestion()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ConsolePermissions = ["members.read:ReadOnly", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?revoke={Sam}", Token);

        Regex.IsMatch(html, "<button[^>]*disabled[^>]*>Withdraw</button>").ShouldBeTrue();
        html.ShouldContain("permission to withdraw invitations.\"");
        html.ShouldNotContain("?revoke=", customMessage: "no link to follow");
        html.ShouldNotContain("revoke-title", customMessage: "the question is not opened for someone who could not confirm it");
    }

    [Fact]
    public async Task AskingToWithdrawSomeoneWhoIsNotInTheListOpensNothing()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?revoke={Guid.NewGuid()}", Token);

        html.ShouldNotContain("revoke-title");
        html.ShouldContain("sam@acme.com");
    }

    [Fact]
    public async Task InvitingNeedsToBeAbleToReadRolesBecauseRolesMustBeChosen()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ConsolePermissions = ["members.read:ReadOnly", "members.invite:Full"];
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("permission to choose roles for people you invite.");
        html.ShouldNotContain("?invite=1");
    }

    [Fact]
    public async Task TheInviteDialogOffersRolesAndProductsAndExplainsWhatIsNotAvailable()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var plain = await client.GetStringAsync(PagePath, Token);
        var html = await client.GetStringAsync($"{PagePath}?invite=1", Token);

        plain.ShouldNotContain("role=\"dialog\"");
        html.ShouldContain("role=\"dialog\"");
        html.ShouldContain("Invite people");
        html.ShouldContain("""<label for="invite-emails">Email addresses</label>""");
        html.ShouldContain("One per line, or separated by commas.");
        html.ShouldContain($"""name="roleId" value="{AdminRole}" """.TrimEnd());
        html.ShouldContain($"""name="applicationId" value="{Helpdesk}" """.TrimEnd());
        html.ShouldContain("2 of 5 seats used");
        html.ShouldContain("No free seats");
        html.ShouldContain("Not subscribed");
        Regex.IsMatch(html, $"""<input[^>]*value="{Billing}"[^>]*disabled""").ShouldBeTrue("a full product cannot be chosen");
        Regex.IsMatch(html, $"""<input[^>]*value="{Analytics}"[^>]*disabled""").ShouldBeTrue("a product the organisation is not subscribed to cannot be chosen");
    }

    [Fact]
    public async Task TheOwnerRoleIsOnlyOfferedToAnOwner()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var notOwner = Api(clock);
        var owner = Api(clock);
        owner.MembersJson = MembersJson(owner: true);
        await using var notOwnerFactory = new WebAppFactory(notOwner, time: clock);
        await using var ownerFactory = new WebAppFactory(owner, time: clock);
        using var notOwnerClient = await SignedInAsync(notOwnerFactory);
        using var ownerClient = await SignedInAsync(ownerFactory);

        var forAdmin = await notOwnerClient.GetStringAsync($"{PagePath}?invite=1", Token);
        var forOwner = await ownerClient.GetStringAsync($"{PagePath}?invite=1", Token);

        Regex.IsMatch(forAdmin, $"""<input[^>]*value="{OwnerRole}"[^>]*disabled""").ShouldBeTrue();
        forAdmin.ShouldContain("Only an Owner can invite someone as an Owner.");
        Regex.IsMatch(forOwner, $"""<input[^>]*value="{OwnerRole}"[^>]*disabled""").ShouldBeFalse();
    }

    [Fact]
    public async Task WithoutPermissionToReadSubscriptionsTheDialogLeavesProductsForLater()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.ConsolePermissions = ["members.read:Full", "members.invite:Full", "roles.read:ReadOnly"];
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync($"{PagePath}?invite=1", Token);

        html.ShouldContain("Product access can be given once they've joined.");
        html.ShouldNotContain("name=\"applicationId\"");
    }

    [Fact]
    public async Task InvitingSendsTheRolesAndProductsAsTheAdminAndLeadsBackToTheList()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var posted = await PostInviteAsync(client, "new@acme.com", [AdminRole, ViewerRole], [Helpdesk]);

        posted.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        Where(posted).ShouldBe($"{PagePath}?notice=sent&n=1");
        var call = api.InviteCalls.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {api.ProductTokensIssued[0]}");
        call.Body.ShouldContain("\"email\":\"new@acme.com\"");
        call.Body.ShouldContain($"\"roleIds\":[\"{AdminRole}\",\"{ViewerRole}\"]");
        call.Body.ShouldContain($"\"applicationIds\":[\"{Helpdesk}\"]");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("Invitation sent");
    }

    [Fact]
    public async Task ACreatedInvitationIsNeverReportedAsFailedBecauseOfHowTheAnswerReads()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.InviteResponse = _ => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("not json at all", Encoding.UTF8, "text/plain") };
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var posted = await PostInviteAsync(client, "new@acme.com", [ViewerRole], []);

        Where(posted).ShouldBe($"{PagePath}?notice=sent&n=1");
    }

    [Fact]
    public async Task SeveralAddressesAtOnceAreInvitedOneByOne()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var posted = await PostInviteAsync(client, "a@acme.com, b@acme.com\nA@acme.com\n c@acme.com ", [ViewerRole], []);

        Where(posted).ShouldBe($"{PagePath}?notice=sent&n=3");
        api.InviteCalls.Count.ShouldBe(3, "a repeated address is invited once");
        (await client.GetStringAsync(Where(posted), Token)).ShouldContain("3 invitations sent");
    }

    [Fact]
    public async Task ProblemsWithTheFormAreListedAndNothingIsSent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = await (await PostInviteAsync(client, "anna", [], [])).Content.ReadAsStringAsync(Token);
        var noEmails = await (await PostInviteAsync(client, "", [ViewerRole], [])).Content.ReadAsStringAsync(Token);

        html.ShouldContain("Fix 2 problems");
        html.ShouldContain("anna isn&#x27;t a valid email address, like anna@acme.com.", Case.Insensitive);
        html.ShouldContain("Choose between 1 and 10 roles.");
        noEmails.ShouldContain("Enter at least one email address.");
        api.InviteCalls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "already_member", "already belongs to Acme Ltd.")]
    [InlineData(HttpStatusCode.Forbidden, "owner_role_restricted", "only an Owner can invite someone as an Owner.")]
    [InlineData(HttpStatusCode.BadRequest, "application_not_subscribed", "isn't subscribed to one of the products you chose.")]
    [InlineData(HttpStatusCode.NotFound, "not_found", "one of the roles no longer exists.")]
    public async Task EachRefusalIsExplainedAndTheAddressStaysInTheDialog(HttpStatusCode status, string code, string expected)
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.InviteResponse = _ => Problem(status, code);
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var posted = await PostInviteAsync(client, "taken@acme.com", [ViewerRole], []);
        var html = WebUtility.HtmlDecode(await posted.Content.ReadAsStringAsync(Token));

        posted.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Some invitations weren't sent");
        html.ShouldContain(expected);
        html.ShouldContain("taken@acme.com");
        html.ShouldContain("role=\"dialog\"");
    }

    [Fact]
    public async Task ASeatShortageNamesTheFullProducts()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.InviteResponse = _ => Problem(HttpStatusCode.Conflict, "no_seat_available", "There is no free seat in: helpdesk.");
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await (await PostInviteAsync(client, "new@acme.com", [ViewerRole], [Helpdesk])).Content.ReadAsStringAsync(Token));

        html.ShouldContain("there is no free seat in helpdesk. Free a seat or choose fewer products.");
    }

    [Fact]
    public async Task WhenSomeInvitationsGoOutAndSomeDoNotOnlyTheFailedAddressesRemain()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.InviteResponse = body => body.Contains("taken@acme.com") ? Problem(HttpStatusCode.Conflict, "already_member") : new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"membershipId":"00000000-0000-0000-0000-000000000001","email":"x","status":"Invited","roles":[],"applicationIds":[],"expiresAt":"2026-11-09T10:00:00Z"}""", Encoding.UTF8, "application/json"),
        };
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await (await PostInviteAsync(client, "ok@acme.com\ntaken@acme.com", [ViewerRole], [])).Content.ReadAsStringAsync(Token));

        html.ShouldContain("1 invitation was sent.");
        html.ShouldContain("taken@acme.com already belongs to Acme Ltd.");
        Regex.IsMatch(html, "<textarea[^>]*>taken@acme.com</textarea>").ShouldBeTrue("only the address that still needs sending stays");
    }

    [Fact]
    public async Task AnApiThatCannotBeReachedWhileInvitingSaysNothingWasSent()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.InviteResponse = _ => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api, time: clock);
        using var client = await SignedInAsync(factory);

        var html = WebUtility.HtmlDecode(await (await PostInviteAsync(client, "new@acme.com", [ViewerRole], [])).Content.ReadAsStringAsync(Token));

        html.ShouldContain("Auth Service isn't responding right now. Nothing was sent to this address.");
        html.ShouldNotContain("connection refused");
    }

    [Fact]
    public async Task APendingListThatCannotBeLoadedShowsACalmErrorAndTheTokenIsNeverLogged()
    {
        var logs = new CapturingLogs();
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = Api(clock);
        api.PendingResponse = () => throw new HttpRequestException("connection refused");
        await using var factory = new WebAppFactory(api, time: clock, logs: logs);
        using var client = await SignedInAsync(factory);

        var html = await client.GetStringAsync(PagePath, Token);

        html.ShouldContain("We couldn&#x27;t load the invitations");
        html.ShouldNotContain("connection refused");
        string.Join('\n', logs.Lines).ShouldNotContain(api.ProductTokensIssued[0]);
    }

    private static FakeAuthApi Api(FakeClock clock)
    {
        var now = clock.GetUtcNow();
        return new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            MembersJson = MembersJson(owner: false),
            RolesJson = $$"""
                [
                  {"id":"{{OwnerRole}}","name":"Owner","isBuiltIn":true,"memberCount":1},
                  {"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false,"memberCount":2},
                  {"id":"{{ViewerRole}}","name":"Viewer","isBuiltIn":false,"memberCount":5}
                ]
                """,
            SubscriptionsJson = $$"""
                [
                  {"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":5,"seatsUsed":2},
                  {"applicationId":"{{Billing}}","applicationKey":"billing","applicationName":"Billing","status":"Active","maxSeats":3,"seatsUsed":3},
                  {"applicationId":"{{Analytics}}","applicationKey":"analytics","applicationName":"Analytics","status":"Suspended","maxSeats":null,"seatsUsed":0}
                ]
                """,
            PendingJson = $$"""
                [
                  {"membershipId":"{{Sam}}","email":"sam@acme.com","roles":[{"id":"{{ViewerRole}}","name":"Viewer","isBuiltIn":false}],"applicationIds":["{{Helpdesk}}","{{Billing}}"],"invitedBy":"Priya Shah","invitedAt":"{{now.AddDays(-1):O}}","lastSentAt":"{{now.AddSeconds(-20):O}}","expiresAt":"2026-11-09T10:00:00Z","isExpired":false},
                  {"membershipId":"{{Omar}}","email":"omar@acme.com","roles":[{"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false}],"applicationIds":[],"invitedBy":"Priya Shah","invitedAt":"2026-10-20T10:00:00Z","lastSentAt":"2026-10-25T10:00:00Z","expiresAt":"2026-11-01T10:00:00Z","isExpired":true}
                ]
                """,
        };
    }

    private static string MembersJson(bool owner) => $$"""
        [{"membershipId":"a1111111-1111-4111-8111-111111111111","email":"anna@acme.com","displayName":"Anna Kowalski","status":"Active","roles":[{"id":"{{(owner ? OwnerRole : AdminRole)}}","name":"{{(owner ? "Owner" : "Admin")}}"}],"applications":[],"createdAt":"2026-09-01T10:00:00Z"}]
        """;

    /// <summary>Opens the invite dialog and posts it the way a browser would: the text box, then each ticked role and product.</summary>
    private static async Task<HttpResponseMessage> PostInviteAsync(HttpClient client, string emails, Guid[] roles, Guid[] applications)
    {
        var page = await client.GetStringAsync($"{PagePath}?invite=1", Token);
        var fields = new List<KeyValuePair<string, string>>();
        // Only the invite form's own hidden fields: the page has other forms (one per Resend) with tokens of their own.
        var invite = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(form => form.Contains("name=\"_handler\""));
        foreach (Match input in Regex.Matches(invite, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        fields.Add(new("emails", emails));
        fields.AddRange(roles.Select(role => new KeyValuePair<string, string>("roleId", role.ToString())));
        fields.AddRange(applications.Select(app => new KeyValuePair<string, string>("applicationId", app.ToString())));
        return await client.PostAsync($"{PagePath}?invite=1", new FormUrlEncodedContent(fields), Token);
    }

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
