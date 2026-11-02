using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Admin;
using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Aud02;

/// <summary>
/// AUD-02 (AUTH-260) Audit entries say who did it, in words: <c>ActorName</c> on both reads. A tenant admin is only told the names of
/// people who belong to their own organisation. The tests have no workbook Test Case ID: the story's acceptance criteria cover them.
/// </summary>
public sealed class AuditActorNamesTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task AUD02_ATenantAdminSeesTheNameOfAMemberWhoActedAndTheEmailWhenThereIsNoDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        var named = await SignUpVerifiedAsync("Member Co", "priya@other.com", ct);
        await AddMembershipAsync(named.UserId, a.Account.TenantId, MembershipStatus.Active, ct);
        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(user => user.Id == named.UserId, ct)).DisplayName = "Priya Shah";
            await db.SaveChangesAsync(ct);
        });
        await RecordAsync(a.Account.TenantId, AuditActorType.User, named.UserId, AuditActions.RoleCreated, ct);
        await CreateRoleAsync(a.Token, "A role", ct);

        var items = (await ReadTenantAsync(a.Token, ct)).Items;

        items.First(entry => entry.ActorId == named.UserId).ActorName.ShouldBe("Priya Shah");
        items.First(entry => entry.ActorId == a.Account.UserId).ActorName.ShouldBe("boss@a.com");
    }

    [Fact]
    public async Task AUD02_ATenantAdminIsNotToldWhoAPersonFromAnotherOrganisationIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        var outsider = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        await RecordAsync(a.Account.TenantId, AuditActorType.User, outsider.UserId, AuditActions.RoleUpdated, ct);

        var items = (await ReadTenantAsync(a.Token, ct)).Items;

        var entry = items.Single(candidate => candidate.ActorId == outsider.UserId);
        entry.ActorName.ShouldBeNull("someone from another organisation is not named to this one");
        (await (await GetAsync(TenantAuditEndpoints.Route, a.Token, ct)).Content.ReadAsStringAsync(ct)).ShouldNotContain("boss@other.com");
    }

    [Fact]
    public async Task AUD02_AProductThatActedIsNamedByItsName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var product = await CreateActiveProductAsync(adminToken, "helpdesk", ct);
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        await RecordAsync(a.Account.TenantId, AuditActorType.Product, product.Id, AuditActions.TenantSubscribed, ct);

        var items = (await ReadTenantAsync(a.Token, ct)).Items;

        items.Single(entry => entry.ActorId == product.Id).ActorName.ShouldBe("helpdesk");
    }

    [Fact]
    public async Task AUD02_AnAnonymousOrSystemEntryHasNoName()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        await RecordAsync(a.Account.TenantId, AuditActorType.System, null, AuditActions.SeatLimitChanged, ct);
        await RecordAsync(a.Account.TenantId, AuditActorType.Anonymous, null, AuditActions.LoginFailed, ct);

        var items = (await ReadTenantAsync(a.Token, ct)).Items;

        items.Where(entry => entry.ActorType is AuditActorType.System or AuditActorType.Anonymous).ShouldAllBe(entry => entry.ActorName == null);
    }

    [Fact]
    public async Task AUD02_APlatformAdminSeesEveryName()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, adminToken) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        var outsider = await SignUpVerifiedAsync("Other Co", "boss@other.com", ct);
        await RecordAsync(a.Account.TenantId, AuditActorType.User, outsider.UserId, AuditActions.RoleUpdated, ct);

        var response = await GetAsync($"{AuditAdminEndpoints.Route}?actorId={outsider.UserId}", adminToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var items = (await response.Content.ReadFromJsonAsync<AuditPageResponse>(Json, ct))!.Items;
        items.ShouldNotBeEmpty();
        items.ShouldAllBe(entry => entry.ActorName == "boss@other.com");
        admin.UserId.ShouldNotBe(outsider.UserId);
    }

    [Fact]
    public async Task AUD02_ANameNeverAppearsWhereThereIsNoActorAndTheOldFieldsAreUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);

        var items = (await ReadTenantAsync(a.Token, ct)).Items;

        items.ShouldNotBeEmpty();
        items.ShouldAllBe(entry => entry.ActorId != null || entry.ActorName == null);
        items.ShouldAllBe(entry => entry.TenantId == a.Account.TenantId);
    }

    private Task RecordAsync(Guid tenantId, AuditActorType type, Guid? actorId, string action, CancellationToken ct) =>
        WithDbAsync(async db =>
        {
            db.AuditEntries.Add(new AuditEntry
            {
                Id = Guid.CreateVersion7(),
                OccurredAt = DateTimeOffset.UtcNow,
                Action = action,
                ActorType = type,
                ActorId = actorId,
                TenantId = tenantId,
            });
            await db.SaveChangesAsync(ct);
        });

    private sealed record Tenant(SignupResponse Account, string Token);

    private async Task<Tenant> TenantAsync(string tenantName, string email, CancellationToken ct)
    {
        var account = await SignUpVerifiedAsync(tenantName, email, ct);
        return new Tenant(account, await TenantTokenAsync((await LoginForTokensAsync(email, ct)).AccessToken, account.TenantId, ct));
    }

    private async Task<AuditPageResponse> ReadTenantAsync(string token, CancellationToken ct)
    {
        var response = await GetAsync(TenantAuditEndpoints.Route, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuditPageResponse>(Json, ct))!;
    }

    private async Task CreateRoleAsync(string token, string name, CancellationToken ct)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, []), token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}
