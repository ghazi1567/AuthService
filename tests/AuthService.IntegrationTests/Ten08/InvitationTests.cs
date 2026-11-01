using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Members;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten08;

/// <summary>
/// TEN-08 (AUTH-103) Invite User, through the real <c>POST /api/auth/tenants/me/invitations</c> (a tenant-admin token) and
/// <c>POST /api/auth/invitations/accept</c> (the emailed link). Email goes through the in-memory sender, so the real link is read.
/// Test method names start with their Test Case ID (AUTH_TC16..AUTH_TC19, AUTH_TC22, AUTH_TC23, AUTH_TC43) per the workbook convention.
/// </summary>
public sealed class InvitationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";
    private const string OwnerEmail = "owner@acme.com";
    private const string Password = "a-good-password";

    [Fact]
    public async Task AUTH_TC16_InvitingANewEmailWithOneRoleThenAcceptingLetsThemLogInHoldingThatRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        var invite = await InviteAsync(t.OwnerToken, "New.Person@Example.com", ct, support);

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var invitation = (await invite.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!;
        (invitation.Email, invitation.Status).ShouldBe(("new.person@example.com", "Invited"));
        invitation.Roles.Select(role => role.Name).ShouldBe(["Support"]);
        (await StatusOfAsync(invitation.MembershipId, ct)).ShouldBe(MembershipStatus.Invited);

        var token = await WaitForEmailTokenAsync("new.person@example.com", "You have been invited to Acme Ltd");
        var accept = await AcceptAsync(token, Password, null, ct);

        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StatusOfAsync(invitation.MembershipId, ct)).ShouldBe(MembershipStatus.Active);
        var loginResponse = await LoginAsync("new.person@example.com", Password, ct);
        loginResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = (await loginResponse.Content.ReadFromJsonAsync<AuthService.Api.Features.Login.LoginResponse>(Json, ct))!;
        login.Memberships.Select(membership => membership.TenantId).ShouldBe([t.Owner.TenantId]);
        var members = await ListMembersAsync(t.OwnerToken, ct);
        members.Single(member => member.Email == "new.person@example.com").Roles.Select(role => role.Name).ShouldBe(["Support"]);
    }

    [Fact]
    public async Task AUTH_TC17_InvitingAnEmailThatIsAlreadyAMemberDifferingOnlyInCaseIsRejectedWith409AndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        var response = await InviteAsync(t.OwnerToken, "OWNER@ACME.COM", ct, support);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("already_member");
        await WithDbAsync(async db =>
        {
            (await db.Invitations.CountAsync(ct)).ShouldBe(0);
            (await db.TenantMemberships.CountAsync(membership => membership.TenantId == t.Owner.TenantId, ct)).ShouldBe(1);
        });
    }

    [Fact]
    public async Task AUTH_TC18_AnAlreadyUsedInvitationLinkIsRejectedWith410AndNothingChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        (await AcceptAsync(token, Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var reuse = await AcceptAsync(token, "another-password-1", null, ct);

        reuse.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(reuse, ct)).Code.ShouldBe("invitation_invalid");
        (await LoginAsync("new@example.com", Password, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the first password is still the one that works");
        (await LoginAsync("new@example.com", "another-password-1", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AUTH_TC19_LoggingInAsAnInvitedPersonWhoHasNotAcceptedGetsTheSameGeneric401AsBadCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);

        var invited = await LoginAsync("new@example.com", Password, ct);
        var wrong = await LoginAsync(OwnerEmail, "not-the-password", ct);

        invited.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var invitedProblem = await ReadProblemAsync(invited, ct);
        var wrongProblem = await ReadProblemAsync(wrong, ct);
        (invitedProblem.Code, invitedProblem.Title, invitedProblem.Detail).ShouldBe((wrongProblem.Code, wrongProblem.Title, wrongProblem.Detail));
    }

    [Fact]
    public async Task AUTH_TC22_AnExistingUserFromAnotherTenantAcceptsWhileLoggedInWithTheirPasswordAndOtherMembershipsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var existing = await SignUpVerifiedAsync("Home Co", "person@home.com", ct);

        var invite = await InviteAsync(t.OwnerToken, "person@home.com", ct, support);

        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForEmailTokenAsync("person@home.com", "You have been invited to Acme Ltd");
        var identity = (await LoginForTokensAsync("person@home.com", ct)).AccessToken;
        var accept = await AcceptAsync(token, null, identity, ct);

        accept.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = await LoginForTokensAsync("person@home.com", ct);
        login.Memberships.Select(membership => (membership.TenantId, membership.Status))
            .ShouldBe([(existing.TenantId, MembershipStatus.Active), (t.Owner.TenantId, MembershipStatus.Active)], ignoreOrder: true);
    }

    [Fact]
    public async Task AUTH_TC23_AcceptingWhileLoggedInAsADifferentUserIsRejectedWith403AndTheMembershipStaysInvited()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await SignUpVerifiedAsync("Home Co", "person@home.com", ct);
        await SignUpVerifiedAsync("Other Co", "someone@else.com", ct);
        var invite = await InviteAsync(t.OwnerToken, "person@home.com", ct, support);
        var membershipId = (await invite.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
        var token = await WaitForEmailTokenAsync("person@home.com", "You have been invited to Acme Ltd");
        var wrongIdentity = (await LoginForTokensAsync("someone@else.com", ct)).AccessToken;

        var response = await AcceptAsync(token, null, wrongIdentity, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("wrong_account");
        (await StatusOfAsync(membershipId, ct)).ShouldBe(MembershipStatus.Invited);

        // The mistake did not use up the invitation: the right person can still accept.
        var rightIdentity = (await LoginForTokensAsync("person@home.com", ct)).AccessToken;
        (await AcceptAsync(token, null, rightIdentity, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AUTH_TC43_ANewPersonAcceptingAnInvitationIsTreatedAsVerifiedAndCanLogInAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        await WithDbAsync(async db => (await db.Users.AsNoTracking().SingleAsync(user => user.Email == "new@example.com", ct)).EmailVerifiedAt.ShouldBeNull());

        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        (await AcceptAsync(token, Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db => (await db.Users.AsNoTracking().SingleAsync(user => user.Email == "new@example.com", ct)).EmailVerifiedAt.ShouldNotBeNull());
        (await LoginAsync("new@example.com", Password, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "no verification email step was needed");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN08_TheResponseLooksTheSameWhetherTheEmailIsNewOrAlreadyHasAnAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await SignUpVerifiedAsync("Home Co", "known@home.com", ct);

        var forNew = await (await InviteAsync(t.OwnerToken, "brand.new@example.com", ct, support)).Content.ReadFromJsonAsync<JsonElement>(ct);
        var forKnown = await (await InviteAsync(t.OwnerToken, "known@home.com", ct, support)).Content.ReadFromJsonAsync<JsonElement>(ct);

        forNew.EnumerateObject().Select(property => property.Name).ShouldBe(forKnown.EnumerateObject().Select(property => property.Name));
        forNew.GetProperty("status").GetString().ShouldBe(forKnown.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TEN08_TheEmailCarriesTheLinkAndOnlyAHashOfTheTokenIsStored()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);

        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        await WithDbAsync(async db =>
        {
            var stored = await db.Invitations.AsNoTracking().SingleAsync(ct);
            stored.TokenHash.ShouldBe(AuthService.Api.Common.SecretToken.Hash(token));
            stored.TokenHash.ShouldNotBe(token);
            (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromDays(7));
            stored.InvitedByUserId.ShouldBe(t.Owner.UserId);
        });
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(token), "the token is never logged");
    }

    [Fact]
    public async Task TEN08_ARoleOfAnotherTenantReturns404AndTheRoleCountMustBeOneToTen()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var other = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        var otherToken = await TenantTokenAsync((await LoginForTokensAsync("boss@other.com", ct)).AccessToken, other.TenantId, ct);
        var theirs = await CreateRoleAsync(otherToken, "Theirs", ct);
        var roles = new List<Guid>();
        for (var index = 0; index < 11; index++)
        {
            roles.Add(await CreateRoleAsync(t.OwnerToken, $"Role {index}", ct));
        }

        (await InviteAsync(t.OwnerToken, "a@example.com", ct, theirs)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await InviteAsync(t.OwnerToken, "b@example.com", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await InviteAsync(t.OwnerToken, "c@example.com", ct, [.. roles])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await InviteAsync(t.OwnerToken, "d@example.com", ct, [.. roles.Take(10)])).StatusCode.ShouldBe(HttpStatusCode.Created);
        await WithDbAsync(async db => (await db.Users.CountAsync(user => user.Email == "a@example.com" || user.Email == "b@example.com" || user.Email == "c@example.com", ct)).ShouldBe(0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("two@@example.com")]
    public async Task TEN08_AMissingOrInvalidEmailIsRejectedWith400(string? email)
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(email, [support], null), t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TEN08_OnlyAnOwnerCanInviteSomeoneAsAnOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var ownerRole = await ReadAsync(db => db.Roles.AsNoTracking().Where(role => role.TenantId == t.Owner.TenantId && role.IsBuiltIn).Select(role => role.Id).SingleAsync(ct));
        var inviterRole = await CreateRoleAsync(t.OwnerToken, "Inviter", ct, new RoleGrantInput(BuiltInApplication.Id, "members.invite", "Full"));
        var delegateMember = await AddMemberAsync(t.Owner.TenantId, "delegate@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(delegateMember.MembershipId), new AssignRolesRequest([inviterRole]), t.OwnerToken, ct);
        var delegateToken = await TenantTokenAsync(delegateMember.IdentityToken, t.Owner.TenantId, ct);

        var byDelegate = await InviteAsync(delegateToken, "boss@example.com", ct, ownerRole);

        byDelegate.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(byDelegate, ct)).Code.ShouldBe("owner_role_restricted");
        (await InviteAsync(t.OwnerToken, "boss@example.com", ct, ownerRole)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await InviteAsync(delegateToken, "ordinary@example.com", ct, inviterRole)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TEN08_InvitingWithProductAccessTakesASeatAndAFullProductRefusesTheWholeInvitation()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct, maxSeats: 2);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        var first = await InviteAsync(t.OwnerToken, "first@example.com", ct, [support], [t.Product.Id]);
        var second = await InviteAsync(t.OwnerToken, "second@example.com", ct, [support], [t.Product.Id]);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await SeatsUsedAsync(t.OwnerToken, ct)).ShouldBe(2, "the Owner and the Invited person");
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(second, ct)).Code.ShouldBe("no_seat_available");
        await WithDbAsync(async db =>
        {
            (await db.Users.AnyAsync(user => user.Email == "second@example.com", ct)).ShouldBeFalse();
            (await db.Invitations.CountAsync(ct)).ShouldBe(1);
        });
        (await InviteAsync(t.OwnerToken, "third@example.com", ct, [support], [])).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task TEN08_AnUnsubscribedProductIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        foreach (var applicationId in new[] { Guid.NewGuid(), BuiltInApplication.Id })
        {
            (await InviteAsync(t.OwnerToken, "new@example.com", ct, [support], [applicationId])).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task TEN08_ABadOrMissingPasswordIsRejectedWith400AndTheInvitationStaysUsable()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");

        (await AcceptAsync(token, null, null, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await AcceptAsync(token, "short", null, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await AcceptAsync(token, Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN08_AnExistingUserMustBeLoggedInAndCannotSetAPasswordThisWay()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await SignUpVerifiedAsync("Home Co", "person@home.com", ct);
        var invite = await InviteAsync(t.OwnerToken, "person@home.com", ct, support);
        var membershipId = (await invite.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
        var token = await WaitForEmailTokenAsync("person@home.com", "You have been invited to Acme Ltd");
        var identity = (await LoginForTokensAsync("person@home.com", ct)).AccessToken;

        var anonymous = await AcceptAsync(token, null, null, ct);
        var withPassword = await AcceptAsync(token, "a-new-password-1", identity, ct);
        var garbageToken = await AcceptAsync(token, null, "garbage", ct);

        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(anonymous, ct)).Code.ShouldBe("login_required");
        withPassword.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        garbageToken.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "a bad bearer token is the same as none");
        (await StatusOfAsync(membershipId, ct)).ShouldBe(MembershipStatus.Invited);
        (await LoginAsync("person@home.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "their own password is unchanged");
    }

    [Fact]
    public async Task TEN08_ADisabledInvitationCannotBeAcceptedAndAnUnknownOrMissingTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var invite = await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        var membershipId = (await invite.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        (await PatchJsonAsync(MemberEndpoints.MemberRouteFor(membershipId), """{"status": "Disabled"}""", t.OwnerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await AcceptAsync(token, Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await AcceptAsync("not-a-real-token", Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await AcceptAsync(null, Password, null, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await StatusOfAsync(membershipId, ct)).ShouldBe(MembershipStatus.Disabled);
    }

    [Fact]
    public async Task TEN08_TwoSimultaneousAcceptancesOfOneLinkSucceedOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");

        var responses = await Task.WhenAll(AcceptAsync(token, Password, null, ct), AcceptAsync(token, Password, null, ct), AcceptAsync(token, Password, null, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Gone).ShouldBe(2);
    }

    [Fact]
    public async Task TEN08_SimultaneousInvitationsOfOneEmailCreateExactlyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);

        var responses = await Task.WhenAll(
            InviteAsync(t.OwnerToken, "new@example.com", ct, support),
            InviteAsync(t.OwnerToken, "NEW@example.com", ct, support),
            InviteAsync(t.OwnerToken, "new@example.com", ct, support));

        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ShouldBe(2);
        await WithDbAsync(async db => (await db.Invitations.CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task TEN08_InvitingAndAcceptingAreAudited()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var invite = await InviteAsync(t.OwnerToken, "new@example.com", ct, support);
        var membershipId = (await invite.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
        var token = await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");
        await AcceptAsync(token, Password, null, ct);

        await WithDbAsync(async db =>
        {
            var sent = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.InvitationSent, ct);
            (sent.ActorId, sent.TenantId, sent.Detail).ShouldBe((t.Owner.UserId, t.Owner.TenantId, membershipId.ToString()));
            var accepted = await db.AuditEntries.AsNoTracking().SingleAsync(entry => entry.Action == AuditActions.InvitationAccepted, ct);
            var newUser = await db.Users.AsNoTracking().SingleAsync(user => user.Email == "new@example.com", ct);
            (accepted.ActorId, accepted.TenantId, accepted.Detail).ShouldBe((newUser.Id, t.Owner.TenantId, membershipId.ToString()));
        });
    }

    [Fact]
    public async Task TEN08_InvitingNeedsMembersInviteFullAndATenantToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await TenantAsync(ct);
        var support = await CreateRoleAsync(t.OwnerToken, "Support", ct);
        var plain = await AddMemberAsync(t.Owner.TenantId, "plain@other.com", ct);

        (await InviteAsync(await TenantTokenAsync(plain.IdentityToken, t.Owner.TenantId, ct), "new@example.com", ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await InviteAsync(plain.IdentityToken, "new@example.com", ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await InviteAsync(t.Product.ServiceToken, "new@example.com", ct, support)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await InviteAsync(null, "new@example.com", ct, support)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await WithDbAsync(async db => (await db.Invitations.CountAsync(ct)).ShouldBe(0));
    }

    // ---- helpers ----

    private sealed record Tenant(TestProduct Product, SignupResponse Owner, string OwnerToken);

    private async Task<Tenant> TenantAsync(CancellationToken ct, int? maxSeats = null)
    {
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "whatsapp-saas", ct);
        var owner = await SignUpVerifiedAsync("Acme Ltd", OwnerEmail, ct);
        (await PostAsync(SubscriptionEndpoints.RouteFor(owner.TenantId), new SubscribeRequest(maxSeats), product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        return new Tenant(product, owner, await TenantTokenAsync((await LoginForTokensAsync(OwnerEmail, ct)).AccessToken, owner.TenantId, ct));
    }

    private async Task<Guid> CreateRoleAsync(string token, string name, CancellationToken ct, params RoleGrantInput[] grants)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    private Task<HttpResponseMessage> InviteAsync(string? token, string email, CancellationToken ct, params Guid[] roleIds) =>
        InviteAsync(token, email, ct, roleIds, []);

    private Task<HttpResponseMessage> InviteAsync(string? token, string email, CancellationToken ct, Guid[] roleIds, Guid[] applicationIds) =>
        PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(email, roleIds, applicationIds), token, ct);

    private Task<HttpResponseMessage> AcceptAsync(string? token, string? password, string? bearerToken, CancellationToken ct) =>
        PostAsync(InvitationEndpoints.AcceptRoute, new AcceptInvitationRequest(token, password), bearerToken, ct);

    private async Task<List<MemberItem>> ListMembersAsync(string ownerToken, CancellationToken ct) =>
        (await (await GetAsync(MemberEndpoints.Route, ownerToken, ct)).Content.ReadFromJsonAsync<List<MemberItem>>(Json, ct))!;

    private async Task<int> SeatsUsedAsync(string ownerToken, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, ownerToken, ct);
        return (await response.Content.ReadFromJsonAsync<List<TenantSubscriptionItem>>(Json, ct))!.Single().SeatsUsed;
    }

    private Task<MembershipStatus> StatusOfAsync(Guid membershipId, CancellationToken ct) =>
        ReadAsync(db => db.TenantMemberships.AsNoTracking().Where(membership => membership.Id == membershipId).Select(membership => membership.Status).SingleAsync(ct));

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);
        var membershipId = await ReadAsync(db => db.TenantMemberships.AsNoTracking()
            .Where(membership => membership.UserId == person.UserId && membership.TenantId == tenantId).Select(membership => membership.Id).SingleAsync(ct));
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }

    private async Task<T> ReadAsync<T>(Func<AuthService.Infrastructure.Persistence.AuthDbContext, Task<T>> read)
    {
        T result = default!;
        await WithDbAsync(async db => result = await read(db));
        return result;
    }
}
