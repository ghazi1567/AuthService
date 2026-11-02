using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Web.Tests;

/// <summary>
/// UI02_TC1: the whole admin flow, page after page, against one API that changes as the steps go: an Owner invites a person with a role and
/// a product, the person accepts, the Owner sees them active, changes their role, removes and gives back their product access, and
/// revives an expired invitation. A read-only admin is then shown none of it. This runs the web app in process with a stand-in API; it is
/// not a run in a real browser.
/// </summary>
public sealed class ConsoleFlowTests
{
    private static readonly Guid Acme = Guid.Parse("7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11");
    private static readonly Guid OwnerRole = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid ViewerRole = Guid.Parse("10000000-0000-4000-8000-000000000003");
    private static readonly Guid AdminRole = Guid.Parse("10000000-0000-4000-8000-000000000002");
    private static readonly Guid Helpdesk = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid Newcomer = Guid.Parse("f7777777-7777-4777-8777-777777777777");
    private static readonly Guid Omar = Guid.Parse("e5555555-5555-4555-8555-555555555555");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnOwnerInvitesAPersonWhoAcceptsThenAssignsRolesAndAccessAndRevivesAnExpiredInvitation()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var api = new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            RolesJson = RolesJson,
            SubscriptionsJson = Subscriptions(seatsUsed: 0),
            MembersJson = Members(status: null),
            PendingJson = Pending(includeNewcomer: false),
            AcceptResponse = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tenantId":"7d8e6b92-2d0b-4f43-9e0a-0d6a0f0f4d11","membershipId":"f7777777-7777-4777-8777-777777777777"}""", System.Text.Encoding.UTF8, "application/json"),
            },
        };
        await using var factory = new WebAppFactory(api, time: clock);
        using var owner = await SignedInAsync(factory);

        // 1. The Owner invites a new person with a role and a product.
        var invited = await PostInviteAsync(owner, "new@acme.com", [ViewerRole], [Helpdesk]);
        Where(invited).ShouldBe($"/console/{Acme}/invitations?notice=sent&n=1");
        var invite = api.InviteCalls.ShouldHaveSingleItem();
        invite.Body.ShouldContain("\"email\":\"new@acme.com\"");
        invite.Body.ShouldContain(ViewerRole.ToString());
        invite.Body.ShouldContain(Helpdesk.ToString());
        api.PendingJson = Pending(includeNewcomer: true);
        api.SubscriptionsJson = Subscriptions(seatsUsed: 1);
        var pending = await owner.GetStringAsync(Where(invited), Token);
        pending.ShouldContain("Invitation sent");
        pending.ShouldContain("new@acme.com");

        // 2. The person opens the link from the email, sets a password and joins.
        using var invitee = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var accepted = await WebForms.PostFormAsync(
            invitee,
            "/accept-invitation?token=invite-link-token",
            "accept-new",
            new Dictionary<string, string> { ["accept-password"] = "a-long-password-1", ["accept-confirm"] = "a-long-password-1" });
        Where(accepted).ShouldBe("/accept-invitation?result=done");
        api.AcceptCalls.ShouldHaveSingleItem().Body.ShouldContain("invite-link-token");

        // 3. The Owner sees the new member become active.
        api.MembersJson = Members(status: "Active");
        var members = await owner.GetStringAsync($"/console/{Acme}/members", Token);
        members.ShouldContain("new@acme.com");
        Regex.IsMatch(members, "new@acme.com.*?Active", RegexOptions.Singleline).ShouldBeTrue();

        // 4. The Owner changes the role.
        var roles = await PostRolesAsync(owner, [AdminRole]);
        Where(roles).ShouldBe($"/console/{Acme}/members?notice=roles&m={Newcomer}");
        api.AssignRolesCalls.ShouldHaveSingleItem().Body.ShouldContain(AdminRole.ToString());

        // 5. The Owner removes the product access (seat freed), then gives it back.
        var access = await owner.GetStringAsync($"/console/{Acme}/access", Token);
        access.ShouldContain("1 of 5 seats used");
        var revokeDialog = await owner.GetStringAsync($"/console/{Acme}/access?revoke={Newcomer}&app={Helpdesk}", Token);
        var revoked = await WebForms.PostPlainFormAsync(owner, revokeDialog, $"/access/{Helpdesk}/{Newcomer}/revoke");
        Where(revoked).ShouldBe($"/console/{Acme}/access?notice=revoked&m={Newcomer}&a={Helpdesk}");
        api.SubscriptionsJson = Subscriptions(seatsUsed: 0);
        api.MembersJson = Members(status: "Active", withProduct: false);
        var afterRevoke = await owner.GetStringAsync(Where(revoked), Token);
        afterRevoke.ShouldContain("0 of 5 seats used");
        var granted = await PostGrantAsync(owner, afterRevoke, Helpdesk, Newcomer);
        Where(granted).ShouldBe($"/console/{Acme}/access?notice=granted&m={Newcomer}&a={Helpdesk}");
        api.AccessCalls.Select(call => call.Method).ShouldBe(["DELETE", "PUT"], "removed first, then given back");
        api.AccessCalls.ShouldAllBe(call => call.Path == $"/api/auth/tenants/me/members/{Newcomer}/applications/{Helpdesk}");

        // 6. A second person's invitation expired; the Owner revives it from the list.
        var list = await owner.GetStringAsync($"/console/{Acme}/invitations", Token);
        list.ShouldContain("omar@acme.com");
        list.ShouldContain("Expired");
        var resent = await WebForms.PostPlainFormAsync(owner, list, $"/invitations/{Omar}/resend");
        Where(resent).ShouldBe($"/console/{Acme}/invitations?notice=resent&m={Omar}");
        api.ResendCalls.ShouldHaveSingleItem().Path.ShouldBe($"/api/auth/tenants/me/invitations/{Omar}/resend");

        // Every write above was made with the console token, as the signed-in Owner, never with the sign-in token.
        var consoleToken = $"Bearer {api.ProductTokensIssued[0]}";
        api.InviteCalls.ShouldAllBe(call => call.Authorization == consoleToken);
        api.AssignRolesCalls.ShouldAllBe(call => call.Authorization == consoleToken);
        api.AccessCalls.ShouldAllBe(call => call.Authorization == consoleToken);
        api.ResendCalls.ShouldAllBe(call => call.Authorization == consoleToken);
    }

    [Fact]
    public async Task AReadOnlyAdminWalksTheSamePagesAndIsOfferedNoWriteAtAll()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var api = new FakeAuthApi
        {
            MembershipsJson = $$"""[{"tenantId":"{{Acme}}","tenantName":"Acme Ltd","status":"Active"}]""",
            ConsolePermissions = ["members.read:ReadOnly", "roles.read:ReadOnly", "access.manage:ReadOnly", "subscriptions.read:ReadOnly"],
            RolesJson = RolesJson,
            SubscriptionsJson = Subscriptions(seatsUsed: 1),
            MembersJson = Members(status: "Active"),
            PendingJson = Pending(includeNewcomer: true),
        };
        await using var factory = new WebAppFactory(api, time: clock);
        using var reader = await SignedInAsync(factory);

        foreach (var page in new[] { "members", "invitations", "roles", "access" })
        {
            var html = await reader.GetStringAsync($"/console/{Acme}/{page}", Token);
            foreach (Match form in Regex.Matches(html, "<form[^>]*method=\"post\"[^>]*>"))
            {
                form.Value.ShouldContain("action=\"/logout\"", customMessage: $"{page} offers a write form to a read-only admin");
            }
        }

        // A forced invite, role change or access change shows no dialog, no form and calls nothing.
        (await reader.GetStringAsync($"/console/{Acme}/invitations?invite=1", Token)).ShouldNotContain("role=\"dialog\"");
        (await reader.GetStringAsync($"/console/{Acme}/members/{Newcomer}/roles", Token)).ShouldNotContain("name=\"roleId\"");
        api.InviteCalls.ShouldBeEmpty();
        api.AssignRolesCalls.ShouldBeEmpty();
        api.AccessCalls.ShouldBeEmpty();
        api.ResendCalls.ShouldBeEmpty();
        api.StatusCalls.ShouldBeEmpty();
        api.RoleDeleteCalls.ShouldBeEmpty();
    }

    private const string RolesJson = """
        [
          {"id":"10000000-0000-4000-8000-000000000001","name":"Owner","isBuiltIn":true,"memberCount":1},
          {"id":"10000000-0000-4000-8000-000000000002","name":"Admin","isBuiltIn":false,"memberCount":1},
          {"id":"10000000-0000-4000-8000-000000000003","name":"Viewer","isBuiltIn":false,"memberCount":3}
        ]
        """;

    private static string Subscriptions(int seatsUsed) =>
        $$"""[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk","applicationName":"Helpdesk","status":"Active","maxSeats":5,"seatsUsed":{{seatsUsed}}}]""";

    private static string Members(string? status, bool withProduct = true)
    {
        var owner = $$"""{"membershipId":"a1111111-1111-4111-8111-111111111111","email":"anna@acme.com","displayName":"Anna Kowalski","status":"Active","roles":[{"id":"{{OwnerRole}}","name":"Owner"}],"applications":[],"createdAt":"2026-09-01T10:00:00Z"}""";
        if (status is null)
        {
            return $"[{owner}]";
        }

        var apps = withProduct ? $$"""[{"applicationId":"{{Helpdesk}}","applicationKey":"helpdesk"}]""" : "[]";
        var newcomer = $$"""{"membershipId":"{{Newcomer}}","email":"new@acme.com","displayName":null,"status":"{{status}}","roles":[{"id":"{{ViewerRole}}","name":"Viewer"}],"applications":{{apps}},"createdAt":"2026-11-02T12:00:00Z"}""";
        return $"[{owner},{newcomer}]";
    }

    private static string Pending(bool includeNewcomer)
    {
        var omar = $$"""{"membershipId":"{{Omar}}","email":"omar@acme.com","roles":[{"id":"{{AdminRole}}","name":"Admin","isBuiltIn":false}],"applicationIds":[],"invitedBy":"Anna Kowalski","invitedAt":"2026-10-20T10:00:00Z","lastSentAt":"2026-10-25T10:00:00Z","expiresAt":"2026-11-01T10:00:00Z","isExpired":true}""";
        var newcomer = $$"""{"membershipId":"{{Newcomer}}","email":"new@acme.com","roles":[{"id":"{{ViewerRole}}","name":"Viewer","isBuiltIn":false}],"applicationIds":["{{Helpdesk}}"],"invitedBy":"Anna Kowalski","invitedAt":"2026-11-02T12:00:00Z","lastSentAt":"2026-11-02T12:00:00Z","expiresAt":"2026-11-09T12:00:00Z","isExpired":false}""";
        return includeNewcomer ? $"[{newcomer},{omar}]" : $"[{omar}]";
    }

    private static async Task<HttpResponseMessage> PostInviteAsync(HttpClient client, string emails, Guid[] roles, Guid[] applications)
    {
        var path = $"/console/{Acme}/invitations?invite=1";
        var page = await client.GetStringAsync(path, Token);
        var fields = HiddenFieldsOfFormWithHandler(page);
        fields.Add(new("emails", emails));
        fields.AddRange(roles.Select(role => new KeyValuePair<string, string>("roleId", role.ToString())));
        fields.AddRange(applications.Select(app => new KeyValuePair<string, string>("applicationId", app.ToString())));
        return await client.PostAsync(path, new FormUrlEncodedContent(fields), Token);
    }

    private static async Task<HttpResponseMessage> PostRolesAsync(HttpClient client, Guid[] roles)
    {
        var path = $"/console/{Acme}/members/{Newcomer}/roles";
        var page = await client.GetStringAsync(path, Token);
        var fields = HiddenFieldsOfFormWithHandler(page);
        fields.AddRange(roles.Select(role => new KeyValuePair<string, string>("roleId", role.ToString())));
        return await client.PostAsync(path, new FormUrlEncodedContent(fields), Token);
    }

    private static async Task<HttpResponseMessage> PostGrantAsync(HttpClient client, string page, Guid product, Guid person)
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

        fields.Add(new("membershipId", person.ToString()));
        return await client.PostAsync($"/console/{Acme}/access/{product}/grant", new FormUrlEncodedContent(fields), Token);
    }

    private static List<KeyValuePair<string, string>> HiddenFieldsOfFormWithHandler(string page)
    {
        var form = Regex.Matches(page, "<form[^>]*>.*?</form>", RegexOptions.Singleline).Select(match => match.Value).First(candidate => candidate.Contains("name=\"_handler\""));
        var fields = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form, "<input[^>]*type=\"hidden\"[^>]*>"))
        {
            fields.Add(new(
                WebUtility.HtmlDecode(Regex.Match(input.Value, "name=\"([^\"]+)\"").Groups[1].Value),
                WebUtility.HtmlDecode(Regex.Match(input.Value, "value=\"([^\"]*)\"").Groups[1].Value)));
        }

        return fields;
    }

    private static async Task<HttpClient> SignedInAsync(WebAppFactory factory)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        (await WebForms.PostLoginAsync(client, "anna@acme.com", "a-long-password")).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return client;
    }

    private static string Where(HttpResponseMessage response) =>
        new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;
}
