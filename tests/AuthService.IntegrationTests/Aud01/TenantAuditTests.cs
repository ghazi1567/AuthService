using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.BuiltIn;
using AuthService.Api.Features.Roles;
using AuthService.Api.Features.Signup;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Aud01;

/// <summary>
/// AUD-01 (AUTH-134): a tenant admin reads their own tenant's audit entries at <c>GET /api/auth/tenants/me/audit</c>, with a
/// tenant-admin token. Test method names start with their Test Case ID (AUD_TC3) per the workbook convention.
/// </summary>
public sealed class TenantAuditTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task AUD_TC3_ATenantAdminReadsOnlyTheirOwnTenantsAuditLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        var b = await TenantAsync("Tenant B", "boss@b.com", ct);
        await CreateRoleAsync(a.Token, "Role in A", ct);
        await CreateRoleAsync(b.Token, "Role in B", ct);

        foreach (var tampered in new[] { "", $"?tenantId={b.Account.TenantId}", $"?tid={b.Account.TenantId}" })
        {
            var page = await ReadAsync(a.Token, tampered, ct);

            page.Items.ShouldNotBeEmpty();
            page.Items.ShouldAllBe(entry => entry.TenantId == a.Account.TenantId, $"only A's entries, for '{tampered}'");
            page.Items.ShouldContain(entry => entry.Action == AuditActions.RoleCreated);
        }

        var forB = await ReadAsync(b.Token, "", ct);
        forB.Items.ShouldAllBe(entry => entry.TenantId == b.Account.TenantId);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task AUD01_EntriesThatBelongToNoTenantAreNotShownToATenantAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        await LoginAsync("boss@a.com", "wrong password here", ct);
        await WithDbAsync(async db => (await db.AuditEntries.AnyAsync(entry => entry.TenantId == null && entry.Action == AuditActions.LoginFailed, ct)).ShouldBeTrue());

        var page = await ReadAsync(a.Token, "", ct);

        page.Items.ShouldNotContain(entry => entry.TenantId == null);
        page.Items.ShouldNotContain(entry => entry.Action == AuditActions.LoginFailed);
    }

    [Fact]
    public async Task AUD01_EntriesAreNewestFirstAndCanBeFilteredByActionAndActor()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        await CreateRoleAsync(a.Token, "First", ct);
        await CreateRoleAsync(a.Token, "Second", ct);

        var all = await ReadAsync(a.Token, "", ct);
        var created = await ReadAsync(a.Token, $"?action={AuditActions.RoleCreated}", ct);
        var byActor = await ReadAsync(a.Token, $"?actorId={a.Account.UserId}", ct);
        var byOther = await ReadAsync(a.Token, $"?actorId={Guid.NewGuid()}", ct);

        all.Items.Select(entry => entry.OccurredAt).ShouldBe(all.Items.Select(entry => entry.OccurredAt).OrderDescending());
        created.Items.Count.ShouldBe(2);
        created.Items.ShouldAllBe(entry => entry.Action == AuditActions.RoleCreated);
        byActor.Items.ShouldNotBeEmpty();
        byActor.Items.ShouldAllBe(entry => entry.ActorId == a.Account.UserId);
        byOther.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task AUD01_ThePagesWalkThroughEveryEntryOnceWithTheCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        for (var index = 0; index < 5; index++)
        {
            await CreateRoleAsync(a.Token, $"Role {index}", ct);
        }

        var collected = new List<AuditEntryResponse>();
        var query = "?limit=2";
        for (var pages = 0; pages < 20; pages++)
        {
            var page = await ReadAsync(a.Token, query, ct);
            page.Items.Count.ShouldBeLessThanOrEqualTo(2);
            collected.AddRange(page.Items);
            if (page.Next is null)
            {
                break;
            }

            query = $"?limit=2&before={Uri.EscapeDataString(page.Next.Before.ToString("O"))}&beforeId={page.Next.BeforeId}";
        }

        collected.Count(entry => entry.Action == AuditActions.RoleCreated).ShouldBe(5);
        collected.Select(entry => entry.Id).Distinct().Count().ShouldBe(collected.Count, "no entry appears twice");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task AUD01_ALimitOutsideOneTo200IsRejectedWith400(int limit)
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);

        (await GetAsync($"{TenantAuditEndpoints.Route}?limit={limit}", a.Token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AUD01_ReadingTheLogIsItselfRecordedButNeverListsItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);

        var first = await ReadAsync(a.Token, "", ct);
        var second = await ReadAsync(a.Token, "", ct);

        first.Items.ShouldNotContain(entry => entry.Action == AuditActions.TenantAuditRead);
        var reads = second.Items.Where(entry => entry.Action == AuditActions.TenantAuditRead).ToList();
        reads.Count.ShouldBe(1, "the first read shows up in the second");
        (reads[0].ActorId, reads[0].TenantId).ShouldBe((a.Account.UserId, a.Account.TenantId));
    }

    [Fact]
    public async Task AUD01_AMemberNeedsTheAuditReadPermissionAndOnlyATenantTokenWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var a = await TenantAsync("Tenant A", "boss@a.com", ct);
        var reader = await CreateRoleAsync(a.Token, "Auditor", ct, new RoleGrantInput(BuiltInApplication.Id, "audit.read", "ReadOnly"));
        var readerMember = await AddMemberAsync(a.Account.TenantId, "auditor@other.com", ct);
        var plain = await AddMemberAsync(a.Account.TenantId, "plain@other.com", ct);
        await PutAsync(RoleEndpoints.MemberRolesRouteFor(readerMember.MembershipId), new AssignRolesRequest([reader]), a.Token, ct);

        (await GetAsync(TenantAuditEndpoints.Route, await TenantTokenAsync(readerMember.IdentityToken, a.Account.TenantId, ct), ct)).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await GetAsync(TenantAuditEndpoints.Route, await TenantTokenAsync(plain.IdentityToken, a.Account.TenantId, ct), ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync(TenantAuditEndpoints.Route, readerMember.IdentityToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "an identity token has no tenant");
        (await GetAsync(TenantAuditEndpoints.Route, null, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- helpers ----

    private sealed record Tenant(SignupResponse Account, string Token);

    private async Task<Tenant> TenantAsync(string tenantName, string email, CancellationToken ct)
    {
        var account = await SignUpVerifiedAsync(tenantName, email, ct);
        return new Tenant(account, await TenantTokenAsync((await LoginForTokensAsync(email, ct)).AccessToken, account.TenantId, ct));
    }

    private async Task<Guid> CreateRoleAsync(string token, string name, CancellationToken ct, params RoleGrantInput[] grants)
    {
        var response = await PostAsync(RoleEndpoints.Route, new CreateRoleRequest(name, grants), token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;
    }

    private async Task<AuditPageResponse> ReadAsync(string token, string query, CancellationToken ct)
    {
        var response = await GetAsync(TenantAuditEndpoints.Route + query, token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuditPageResponse>(Json, ct))!;
    }

    private sealed record Member(Guid UserId, Guid MembershipId, string IdentityToken);

    private async Task<Member> AddMemberAsync(Guid tenantId, string email, CancellationToken ct)
    {
        var person = await SignUpVerifiedAsync("Member Co " + email, email, ct);
        await AddMembershipAsync(person.UserId, tenantId, MembershipStatus.Active, ct);
        Guid membershipId = default;
        await WithDbAsync(async db =>
            membershipId = (await db.TenantMemberships.AsNoTracking()
                .SingleAsync(membership => membership.UserId == person.UserId && membership.TenantId == tenantId, ct)).Id);
        return new Member(person.UserId, membershipId, (await LoginForTokensAsync(email, ct)).AccessToken);
    }
}
