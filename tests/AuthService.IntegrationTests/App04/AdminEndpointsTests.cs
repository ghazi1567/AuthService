using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Admin;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Jwks;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.App04;

/// <summary>
/// The two endpoints behind the platform-admin policy (APP-04): emergency signing-key rotation (TOK-02) and the platform-wide
/// audit read (AUD-01). These tests have no workbook Test Case ID yet: the spec's acceptance criteria cover them.
/// </summary>
public sealed class AdminEndpointsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string AdminEmail = "ops@platform.test";

    [Fact]
    public async Task APP04_EmergencyRotationReplacesTheKeyAtOnceAndIsAuditedUnderTheAdmin()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        var oldKid = ServiceSigningKey().KeyId!;

        var response = await PostAsync(SigningKeyAdminEndpoints.RotateRoute, new { }, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rotated = (await response.Content.ReadFromJsonAsync<RotateSigningKeyResponse>(Json, ct))!;
        rotated.KeyId.ShouldNotBe(oldKid);
        rotated.KeyId.ShouldBe(ServiceSigningKey().KeyId);

        // The old key left the JWKS at once, so the admin's own token, signed by it, no longer works.
        using var jwks = System.Text.Json.JsonDocument.Parse(await (await GetAsync(JwksEndpoints.Route, null, ct)).Content.ReadAsStringAsync(ct));
        jwks.RootElement.GetProperty("keys").EnumerateArray().Select(key => key.GetProperty("kid").GetString()).ShouldBe([rotated.KeyId]);
        (await GetAsync(AuditAdminEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // And the event names the admin who did it.
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.SigningKeyRotated, ct);
            entry.ActorType.ShouldBe(AuditActorType.User);
            entry.ActorId.ShouldBe(admin.UserId);
            entry.Detail.ShouldBe("emergency");
        });
    }

    [Fact]
    public async Task APP04_TheAuditReadShowsEntriesFromEveryTenantAndCanBeFiltered()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var beta = await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var all = await ReadAuditAsync("", token, ct);
        // The platform admin sees signups from every tenant, not just their own.
        var signupTenants = all.Items.Where(entry => entry.Action == AuditActions.SignupCompleted).Select(entry => entry.TenantId).ToList();
        signupTenants.ShouldContain(acme.TenantId);
        signupTenants.ShouldContain(beta.TenantId);

        var onlyAcme = await ReadAuditAsync($"?tenantId={acme.TenantId}", token, ct);
        onlyAcme.Items.ShouldNotBeEmpty();
        onlyAcme.Items.ShouldAllBe(entry => entry.TenantId == acme.TenantId);

        var logins = await ReadAuditAsync($"?action={AuditActions.LoginSucceeded}", token, ct);
        logins.Items.ShouldNotBeEmpty();
        logins.Items.ShouldAllBe(entry => entry.Action == AuditActions.LoginSucceeded);

        var byActor = await ReadAuditAsync($"?actorId={acme.UserId}", token, ct);
        byActor.Items.ShouldNotBeEmpty();
        byActor.Items.ShouldAllBe(entry => entry.ActorId == acme.UserId);
    }

    [Fact]
    public async Task APP04_TheAuditReadPagesNewestFirstWithoutLosingOrRepeatingEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);
        for (var login = 0; login < 3; login++)
        {
            await LoginForTokensAsync("owner@acme.com", ct);
        }

        int totalBefore = 0;
        await WithDbAsync(async db => totalBefore = await db.AuditEntries.CountAsync(ct));

        var collected = new List<AuditEntryResponse>();
        var query = "?limit=2";
        var pages = 0;
        while (true)
        {
            var page = await ReadAuditAsync(query, token, ct);
            collected.AddRange(page.Items);
            pages++;
            if (page.Next is null)
            {
                break;
            }

            query = $"?limit=2&before={Uri.EscapeDataString(page.Next.Before.ToString("O"))}&beforeId={page.Next.BeforeId}";
        }

        pages.ShouldBeGreaterThan(2);
        collected.Count.ShouldBe(totalBefore, "every entry, once");
        collected.Select(entry => entry.Id).Distinct().Count().ShouldBe(collected.Count);
        collected.Select(entry => entry.OccurredAt).ShouldBe(collected.Select(entry => entry.OccurredAt).OrderDescending(), "newest first");
    }

    [Fact]
    public async Task APP04_TheAuditReadIsItselfAnAuditedAdminAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var (admin, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        var first = await ReadAuditAsync("", token, ct);
        first.Items.ShouldNotContain(entry => entry.Action == AuditActions.AdminAuditRead, "a read never lists itself");

        var second = await ReadAuditAsync("", token, ct);
        second.Items.Count(entry => entry.Action == AuditActions.AdminAuditRead).ShouldBe(1);
        second.Items.Single(entry => entry.Action == AuditActions.AdminAuditRead).ActorId.ShouldBe(admin.UserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(201)]
    public async Task APP04_TheAuditReadRefusesALimitOutsideTheAllowedRange(int limit)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, token) = await CreatePlatformAdminAsync(AdminEmail, ct);

        (await GetAsync($"{AuditAdminEndpoints.Route}?limit={limit}", token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<AuditPageResponse> ReadAuditAsync(string query, string token, CancellationToken ct)
    {
        var response = await GetAsync($"{AuditAdminEndpoints.Route}{query}", token, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<AuditPageResponse>(Json, ct))!;
    }
}
