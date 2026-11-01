using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Roles;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.IntegrationTests.Ten08;

/// <summary>
/// TEN-08 (AUTH-103): an invitation link works for 7 days and not a moment longer, walked through with a controllable clock instead
/// of waiting a week. Kept apart from the other invitation tests so only this one moves time.
/// Test method names start with their Test Case ID (AUTH_TC18) per the workbook convention.
/// </summary>
public sealed class InvitationExpiryTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Password = "a-good-password";

    // Starts on a whole microsecond: the database stores microseconds, and expiry is compared exactly.
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(DateTime.UtcNow.Ticks / 10 * 10, TimeSpan.Zero));

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<TimeProvider>(_clock),
    };

    [Fact]
    public async Task AUTH_TC18_AnExpiredInvitationLinkIsRejectedWith410AndThePersonStaysInvitedAndCannotLogIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var ownerToken = await TenantTokenAsync((await LoginForTokensAsync("owner@acme.com", ct)).AccessToken, owner.TenantId, ct);
        var role = (await (await PostAsync(RoleEndpoints.Route, new CreateRoleRequest("Support", []), ownerToken, ct)).Content
            .ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
        var late = await InviteAsync(ownerToken, "late@example.com", role, ct);
        var inTime = await InviteAsync(ownerToken, "in.time@example.com", role, ct);
        var lateToken = await WaitForEmailTokenAsync("late@example.com", "You have been invited to Acme Ltd");
        var inTimeToken = await WaitForEmailTokenAsync("in.time@example.com", "You have been invited to Acme Ltd");

        // Just inside the 7 days it still works...
        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
        (await AcceptAsync(inTimeToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // ...and from the 7th day on it does not.
        _clock.Advance(TimeSpan.FromMinutes(1));
        var response = await AcceptAsync(lateToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invitation_invalid");
        await WithDbAsync(async db =>
        {
            (await db.TenantMemberships.AsNoTracking().SingleAsync(membership => membership.Id == late, ct)).Status.ShouldBe(MembershipStatus.Invited);
            (await db.TenantMemberships.AsNoTracking().SingleAsync(membership => membership.Id == inTime, ct)).Status.ShouldBe(MembershipStatus.Active);
        });
        (await LoginAsync("late@example.com", Password, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<Guid> InviteAsync(string ownerToken, string email, Guid roleId, CancellationToken ct)
    {
        var response = await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest(email, [roleId], []), ownerToken, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<InvitationResponse>(Json, ct))!.MembershipId;
    }

    private Task<HttpResponseMessage> AcceptAsync(string token, CancellationToken ct) =>
        PostAsync(InvitationEndpoints.AcceptRoute, new AcceptInvitationRequest(token, Password), null, ct);
}
