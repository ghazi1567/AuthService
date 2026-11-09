using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using AuthService.IntegrationTests.Tok04;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Ten08;

/// <summary>
/// Seeing who has not accepted an invitation yet (<c>GET /api/auth/tenants/me/invitations</c>) and sending the link again
/// (<c>POST .../{membershipId}/resend</c>), with a controllable clock so a week passes in an instant. These tests have no ticket:
/// they close a known gap (an expired or lost invitation could not be re-sent).
/// </summary>
public sealed class ManageInvitationsTests(PostgresContainerFixture postgres) : IsolationTestBase(postgres)
{
    private const string Subject = "You have been invited to Acme Ltd";
    private const string NewPassword = "a-good-password";

    // Starts on a whole microsecond: the database stores microseconds, and expiry is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task TheListShowsEachOpenInvitationWithWhoWhatAndWhen()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        await InviteAsync(t.Owner.OwnerToken, "first@example.com", ct, t.Role, t.Product.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));
        await InviteAsync(t.Owner.OwnerToken, "second@example.com", ct, t.Role);

        var pending = await ListAsync(t.Owner.OwnerToken, ct);

        pending.Select(item => item.Email).ShouldBe(["second@example.com", "first@example.com"], "newest first");
        var first = pending.Single(item => item.Email == "first@example.com");
        first.InvitedBy.ShouldBe("boss@acme.com");
        first.Roles.Select(role => role.Name).ShouldBe(["Support"]);
        first.ApplicationIds.ShouldBe([t.Product.Id]);
        first.IsExpired.ShouldBeFalse();
        first.ExpiresAt.ShouldBe(first.InvitedAt.AddDays(7));
        first.LastSentAt.ShouldBe(first.InvitedAt);
    }

    [Fact]
    public async Task AnAcceptedInvitationLeavesTheListAndAnExpiredOneIsFlagged()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        await InviteAsync(t.Owner.OwnerToken, "accepts@example.com", ct, t.Role);
        await InviteAsync(t.Owner.OwnerToken, "waits@example.com", ct, t.Role);
        var token = await WaitForEmailTokenAsync("accepts@example.com", Subject);

        (await AcceptAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromDays(8));

        var pending = await ListAsync(t.Owner.OwnerToken, ct);
        pending.Select(item => item.Email).ShouldBe(["waits@example.com"]);
        pending.Single().IsExpired.ShouldBeTrue("the seven days have passed");
    }

    [Fact]
    public async Task OnlyThisTenantsInvitationsAreListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct);
        var otherRole = await CreateRoleAsync(other.OwnerToken, "Support", ct);
        await InviteAsync(other.OwnerToken, "theirs@example.com", ct, otherRole);
        await InviteAsync(t.Owner.OwnerToken, "ours@example.com", ct, t.Role);

        var pending = await ListAsync(t.Owner.OwnerToken, ct);

        pending.Select(item => item.Email).ShouldBe(["ours@example.com"]);
    }

    [Fact]
    public async Task ResendingEmailsANewLinkAndTheOldOneStopsWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        var oldToken = await WaitForEmailTokenAsync("person@example.com", Subject);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var response = await ResendAsync(t.Owner.OwnerToken, membershipId, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!;
        (body.MembershipId, body.Email, body.Status).ShouldBe((membershipId, "person@example.com", "Invited"));
        body.ExpiresAt.ShouldBe(_clock.GetUtcNow().AddDays(7), "a full invitation period from now");
        var newToken = await WaitForEmailTokenAsync("person@example.com", Subject, nth: 2);
        newToken.ShouldNotBe(oldToken);

        (await AcceptAsync(oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await AcceptAsync(newToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync("person@example.com", NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AnExpiredInvitationCanBeRevivedByResending()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "late@example.com", ct, t.Role);
        var oldToken = await WaitForEmailTokenAsync("late@example.com", Subject);
        _clock.Advance(TimeSpan.FromDays(8));
        (await AcceptAsync(oldToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);

        (await ResendAsync(t.Owner.OwnerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var pending = (await ListAsync(t.Owner.OwnerToken, ct)).Single();
        pending.IsExpired.ShouldBeFalse();
        (await AcceptAsync(await WaitForEmailTokenAsync("late@example.com", Subject, nth: 2), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendingTwiceInARowIsRefusedUntilTheCooldownHasPassed()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);

        var tooSoon = await ResendAsync(t.Owner.OwnerToken, membershipId, ct);

        tooSoon.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadProblemAsync(tooSoon, ct)).Code.ShouldBe("resend_too_soon");
        tooSoon.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(60));
        await WaitForEmailTokenAsync("person@example.com", Subject);
        SentEmails.Count(email => email.To == "person@example.com").ShouldBe(1, "no second email was queued");

        _clock.Advance(TimeSpan.FromSeconds(59));
        (await ResendAsync(t.Owner.OwnerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        _clock.Advance(TimeSpan.FromSeconds(1));
        (await ResendAsync(t.Owner.OwnerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SimultaneousResendsSendExactlyOneEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ResendAsync(t.Owner.OwnerToken, membershipId, ct)));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1);
        responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests).ShouldBe(3);
        await WaitForEmailTokenAsync("person@example.com", Subject, nth: 2);
        SentEmails.Count(email => email.To == "person@example.com").ShouldBe(2, "the invitation and one resend");
    }

    [Fact]
    public async Task OnlyAnInvitationStillWaitingCanBeResent()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "accepts@example.com", ct, t.Role);
        (await AcceptAsync(await WaitForEmailTokenAsync("accepts@example.com", Subject), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var accepted = await ResendAsync(t.Owner.OwnerToken, membershipId, ct);
        var owner = await ResendAsync(t.Owner.OwnerToken, await OwnerMembershipAsync(t.Owner.Id, ct), ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(accepted, ct)).Code.ShouldBe("invitation_not_pending");
        owner.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the Owner was never invited");
        await WaitForEmailTokenAsync("accepts@example.com", Subject);
        SentEmails.Count(email => email.To == "accepts@example.com").ShouldBe(1);
    }

    [Fact]
    public async Task AnUnknownOrAnotherTenantsInvitationIs404()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct);
        var otherRole = await CreateRoleAsync(other.OwnerToken, "Support", ct);
        var theirs = await InviteAsync(other.OwnerToken, "theirs@example.com", ct, otherRole);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await ResendAsync(t.Owner.OwnerToken, theirs, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ResendAsync(t.Owner.OwnerToken, Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await WaitForEmailTokenAsync("theirs@example.com", "You have been invited to Other Ltd");
        SentEmails.Count(email => email.To == "theirs@example.com").ShouldBe(1, "the other tenant's invitation was not re-sent");
    }

    [Fact]
    public async Task ListingNeedsMembersReadAndResendingNeedsMembersInviteAtFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        var reader = await CreateRoleAsync(t.Owner.OwnerToken, "Reader", ct, new RoleGrantInput(BuiltInApplication.Id, "members.read", "ReadOnly"));
        var inviterOnly = await CreateRoleAsync(t.Owner.OwnerToken, "Inviter", ct, new RoleGrantInput(BuiltInApplication.Id, "members.invite", "CreateRead"));
        var inviter = await CreateRoleAsync(t.Owner.OwnerToken, "Full inviter", ct, new RoleGrantInput(BuiltInApplication.Id, "members.invite", "Full"));
        var nobody = await TokenForNewMemberAsync(t, "nobody@acme.com", null, ct);
        var readerToken = await TokenForNewMemberAsync(t, "reader@acme.com", reader, ct);
        var weakInviterToken = await TokenForNewMemberAsync(t, "weak@acme.com", inviterOnly, ct);
        var inviterToken = await TokenForNewMemberAsync(t, "inviter@acme.com", inviter, ct);

        // Tokens are made first: the service validates them against the real clock, which a moved test clock would put out of step.
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await GetAsync(InvitationEndpoints.InviteRoute, nobody, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(InvitationEndpoints.InviteRoute, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync(InvitationEndpoints.InviteRoute, t.Product.ServiceToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(InvitationEndpoints.InviteRoute, readerToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ResendAsync(readerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "reading is not enough");
        (await ResendAsync(weakInviterToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Full is needed");
        (await ResendAsync(nobody, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ResendAsync(null, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        await WaitForEmailTokenAsync("person@example.com", Subject);
        SentEmails.Count(email => email.To == "person@example.com").ShouldBe(1, "none of those sent anything");

        (await ResendAsync(inviterToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendingIsAuditedWithTheAdminAndTheMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        _clock.Advance(TimeSpan.FromMinutes(5));

        await ResendAsync(t.Owner.OwnerToken, membershipId, ct);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.InvitationResent, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.Owner.UserId, t.Owner.Id, membershipId.ToString()));
        });
    }

    // ---- AUTH-269: revoking an invitation, and an expired one giving its seat back ----

    [Fact]
    public async Task RevokingFreesTheSeatRemovesThePendingMembershipAndKillsTheLink()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var baseline = await SeatsUsedAsync(t.Owner.OwnerToken, ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role, t.Product.Id);
        var token = await WaitForEmailTokenAsync("person@example.com", Subject);
        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline + 1);

        var response = await RevokeAsync(t.Owner.OwnerToken, membershipId, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline, "the seat is free again");
        (await ListAsync(t.Owner.OwnerToken, ct)).ShouldBeEmpty();
        (await AcceptAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone, "the link no longer works");
        await WithDbAsync(async db =>
        {
            (await db.TenantMemberships.AnyAsync(membership => membership.Id == membershipId, ct)).ShouldBeFalse();
            (await db.ApplicationAccess.AnyAsync(access => access.MembershipId == membershipId, ct)).ShouldBeFalse();
            (await db.MembershipRoles.AnyAsync(assignment => assignment.MembershipId == membershipId, ct)).ShouldBeFalse();
            (await db.Invitations.AnyAsync(invitation => invitation.MembershipId == membershipId, ct)).ShouldBeFalse();
            (await db.Users.AnyAsync(user => user.Email == "person@example.com", ct)).ShouldBeFalse("nobody could sign in as them, so the account is not kept");
        });
    }

    [Fact]
    public async Task ARevokedAddressCanBeInvitedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var first = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        (await RevokeAsync(t.Owner.OwnerToken, first, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var again = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);

        again.ShouldNotBe(first);
        var token = await WaitForEmailTokenAsync("person@example.com", Subject, nth: 2);
        (await AcceptAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RevokingAnExpiredInvitationWorksAndAnExistingAccountIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct);
        var existing = await InviteAsync(t.Owner.OwnerToken, "boss@other.com", ct, t.Role);
        _clock.Advance(TimeSpan.FromDays(8));

        (await RevokeAsync(t.Owner.OwnerToken, existing, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await WithDbAsync(async db =>
        {
            (await db.TenantMemberships.AnyAsync(membership => membership.Id == existing, ct)).ShouldBeFalse();
            (await db.Users.AnyAsync(user => user.Email == "boss@other.com", ct)).ShouldBeTrue("they have an account and another organisation");
            (await db.TenantMemberships.AnyAsync(membership => membership.TenantId == other.Id, ct)).ShouldBeTrue();
        });
    }

    [Fact]
    public async Task OnlyAnInvitationStillWaitingCanBeRevokedAndOnlyInThisTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var other = await TenantAsync("Other Ltd", "boss@other.com", ct);
        var otherRole = await CreateRoleAsync(other.OwnerToken, "Support", ct);
        var theirs = await InviteAsync(other.OwnerToken, "theirs@example.com", ct, otherRole);
        var accepted = await InviteAsync(t.Owner.OwnerToken, "accepts@example.com", ct, t.Role);
        (await AcceptAsync(await WaitForEmailTokenAsync("accepts@example.com", Subject), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var acceptedResponse = await RevokeAsync(t.Owner.OwnerToken, accepted, ct);
        var owner = await RevokeAsync(t.Owner.OwnerToken, await OwnerMembershipAsync(t.Owner.Id, ct), ct);

        acceptedResponse.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(acceptedResponse, ct)).Code.ShouldBe("invitation_not_pending");
        owner.StatusCode.ShouldBe(HttpStatusCode.Conflict, "the Owner was never invited");
        (await RevokeAsync(t.Owner.OwnerToken, theirs, ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound, "another tenant's invitation");
        (await RevokeAsync(t.Owner.OwnerToken, Guid.NewGuid(), ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RevokeAsync(t.Owner.OwnerToken, accepted, ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a second revoke finds an active member, not an invitation");
        await WithDbAsync(async db =>
        {
            (await db.TenantMemberships.AsNoTracking().SingleAsync(membership => membership.Id == accepted, ct)).Status.ShouldBe(MembershipStatus.Active);
            (await db.TenantMemberships.AnyAsync(membership => membership.Id == theirs, ct)).ShouldBeTrue("the other tenant's invitation is untouched");
        });
    }

    [Fact]
    public async Task RevokingNeedsMembersInviteAtFull()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);
        var reader = await CreateRoleAsync(t.Owner.OwnerToken, "Reader", ct, new RoleGrantInput(BuiltInApplication.Id, "members.read", "ReadOnly"));
        var weak = await CreateRoleAsync(t.Owner.OwnerToken, "Inviter", ct, new RoleGrantInput(BuiltInApplication.Id, "members.invite", "CreateRead"));
        var full = await CreateRoleAsync(t.Owner.OwnerToken, "Full inviter", ct, new RoleGrantInput(BuiltInApplication.Id, "members.invite", "Full"));
        var nobody = await TokenForNewMemberAsync(t, "nobody@acme.com", null, ct);
        var readerToken = await TokenForNewMemberAsync(t, "reader@acme.com", reader, ct);
        var weakToken = await TokenForNewMemberAsync(t, "weak@acme.com", weak, ct);
        var inviterToken = await TokenForNewMemberAsync(t, "inviter@acme.com", full, ct);

        (await RevokeAsync(null, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RevokeAsync(nobody, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RevokeAsync(readerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "reading is not enough");
        (await RevokeAsync(weakToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "Full is needed");
        (await RevokeAsync(t.Product.ServiceToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a product cannot withdraw invitations");
        (await ListAsync(t.Owner.OwnerToken, ct)).Count.ShouldBe(1, "none of those removed anything");

        (await RevokeAsync(inviterToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task RevokingIsAuditedWithTheAdminAndTheMembership()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role);

        await RevokeAsync(t.Owner.OwnerToken, membershipId, ct);

        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.InvitationRevoked, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((t.Owner.Owner.UserId, t.Owner.Id, membershipId.ToString()));
        });
    }

    [Fact]
    public async Task AnExpiredInvitationGivesItsSeatBackSoARealInvitationCanTakeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var baseline = await SeatsUsedAsync(t.Owner.OwnerToken, ct);
        await SetSeatLimitAsync(t, baseline + 1, ct);
        var stale = await InviteAsync(t.Owner.OwnerToken, "stale@example.com", ct, t.Role, t.Product.Id);
        var staleToken = await WaitForEmailTokenAsync("stale@example.com", Subject);
        var full = await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest("real@example.com", [t.Role], [t.Product.Id]), t.Owner.OwnerToken, ct);
        full.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadProblemAsync(full, ct)).Code.ShouldBe("no_seat_available");

        _clock.Advance(TimeSpan.FromDays(8));

        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline, "the expired invitation no longer holds a seat");
        (await InviteAsync(t.Owner.OwnerToken, "real@example.com", ct, t.Role, t.Product.Id)).ShouldNotBe(stale);
        (await AcceptAsync(staleToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Gone, "the expired link stays dead");
        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline + 1);
    }

    [Fact]
    public async Task ResendingAnExpiredInvitationTakesTheSeatBackOnlyIfThereIsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var baseline = await SeatsUsedAsync(t.Owner.OwnerToken, ct);
        await SetSeatLimitAsync(t, baseline + 1, ct);
        var stale = await InviteAsync(t.Owner.OwnerToken, "stale@example.com", ct, t.Role, t.Product.Id);
        _clock.Advance(TimeSpan.FromDays(8));
        var real = await InviteAsync(t.Owner.OwnerToken, "real@example.com", ct, t.Role, t.Product.Id);

        var refused = await ResendAsync(t.Owner.OwnerToken, stale, ct);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await ReadProblemAsync(refused, ct);
        problem.Code.ShouldBe("no_seat_available");
        problem.Detail!.ShouldContain("whatsapp-saas");
        await WaitForEmailTokenAsync("stale@example.com", Subject);
        SentEmails.Count(email => email.To == "stale@example.com").ShouldBe(1, "nothing was sent");
        (await ListAsync(t.Owner.OwnerToken, ct)).Single(item => item.MembershipId == stale).IsExpired.ShouldBeTrue("still expired");

        (await RevokeAsync(t.Owner.OwnerToken, real, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ResendAsync(t.Owner.OwnerToken, stale, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline + 1, "the resent invitation holds the seat again");
        (await AcceptAsync(await WaitForEmailTokenAsync("stale@example.com", Subject, nth: 2), ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ResendingALiveInvitationNeedsNoSeatCheckBecauseItAlreadyHoldsOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var t = await WorldAsync(ct);
        var baseline = await SeatsUsedAsync(t.Owner.OwnerToken, ct);
        await SetSeatLimitAsync(t, baseline + 1, ct);
        var membershipId = await InviteAsync(t.Owner.OwnerToken, "person@example.com", ct, t.Role, t.Product.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));

        (await ResendAsync(t.Owner.OwnerToken, membershipId, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the product is full, but this person already has the seat");
        (await SeatsUsedAsync(t.Owner.OwnerToken, ct)).ShouldBe(baseline + 1);
    }

    // ---- helpers ----

    private sealed record World(Tenant Owner, TestProduct Product, Guid Role);

    private Task<HttpResponseMessage> RevokeAsync(string? token, Guid membershipId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, InvitationEndpoints.RevokeRouteFor(membershipId));
        if (token is not null)
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return Client.SendAsync(request, ct);
    }

    private async Task SetSeatLimitAsync(World t, int maxSeats, CancellationToken ct) =>
        (await PatchJsonAsync(SubscriptionEndpoints.SeatsRouteFor(t.Owner.Id, t.Product.Id), $$"""{"maxSeats": {{maxSeats}}}""", t.Product.ServiceToken, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

    private async Task<int> SeatsUsedAsync(string ownerToken, CancellationToken ct)
    {
        var response = await GetAsync(ListSubscriptionsEndpoints.Route, ownerToken, ct);
        return (await response.Content.ReadPageItemsAsync<TenantSubscriptionItem>(Json, ct))!.Single().SeatsUsed;
    }

    private async Task<World> WorldAsync(CancellationToken ct)
    {
        var product = await ProductAsync(await PlatformAdminTokenAsync(ct), "whatsapp-saas", ct);
        var owner = await TenantAsync("Acme Ltd", "boss@acme.com", ct, product);
        return new World(owner, product, await CreateRoleAsync(owner.OwnerToken, "Support", ct));
    }

    private async Task<Guid> InviteAsync(string ownerToken, string email, CancellationToken ct, Guid roleId, params Guid[] applicationIds)
    {
        var response = await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(email, [roleId], applicationIds), ownerToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
    }

    private async Task<List<PendingInvitation>> ListAsync(string token, CancellationToken ct)
    {
        var response = await GetAsync(InvitationEndpoints.InviteRoute, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadPageItemsAsync<PendingInvitation>(Json, ct))!;
    }

    private Task<HttpResponseMessage> ResendAsync(string? token, Guid membershipId, CancellationToken ct) =>
        PostAsync(InvitationEndpoints.ResendRouteFor(membershipId), new { }, token, ct);

    private Task<HttpResponseMessage> AcceptAsync(string token, CancellationToken ct) =>
        PostAsync(InvitationEndpoints.AcceptRoute, new AcceptInvitationRequest(token, NewPassword), null, ct);

    /// <summary>A tenant-admin token for a new active member holding the given role (or none).</summary>
    private async Task<string> TokenForNewMemberAsync(World t, string email, Guid? role, CancellationToken ct)
    {
        var member = await AddMemberAsync(t.Owner.Id, email, ct);
        if (role is { } roleId)
        {
            await AssignRolesAsync(t.Owner.OwnerToken, member.MembershipId, ct, roleId);
        }

        return await TenantTokenAsync(await IdentityTokenAsync(email, ct), t.Owner.Id, ct);
    }

    private async Task<Guid> OwnerMembershipAsync(Guid tenantId, CancellationToken ct)
    {
        Guid id = default;
        await WithDbAsync(async db => id = await db.TenantMemberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenantId).OrderBy(membership => membership.CreatedAt).Select(membership => membership.Id).FirstAsync(ct));
        return id;
    }
}
