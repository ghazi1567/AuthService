using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AuthService.IntegrationTests.Aud01;

/// <summary>
/// AUD-01 (AUTH-134) Audit Log: what is recorded, that it holds no secrets, that it cannot be changed, and that it is cleaned up
/// after a year. Events come from the real endpoints.
/// Test method names start with their Test Case ID (AUD_TC1..AUD_TC5) per the workbook convention.
/// </summary>
public sealed class AuditLogTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string WrongPassword = "not the password";
    private const string NewPassword = "a brand new passphrase";

    [Fact]
    public async Task AUD_TC1_UpdatingARolesPermissionsCreatesOneAuditEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var token = await TenantTokenAsync((await LoginForTokensAsync("owner@acme.com", ct)).AccessToken, owner.TenantId, ct);
        var created = await PostAsync("/api/auth/roles",
            new { name = "Viewer", permissions = new[] { new { applicationId = AuthService.Api.Features.BuiltIn.BuiltInApplication.Id, permission = "roles.read", level = "Full" } } }, token, ct);
        var roleId = (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct)).GetProperty("id").GetGuid();

        var update = await PatchJsonAsync($"/api/auth/roles/{roleId}",
            $$"""{"permissions": [{"applicationId": "{{AuthService.Api.Features.BuiltIn.BuiltInApplication.Id}}", "permission": "roles.read", "level": "ReadOnly"}]}""", token, ct);

        update.StatusCode.ShouldBe(HttpStatusCode.OK);
        await WithDbAsync(async db =>
        {
            var entry = await db.AuditEntries.AsNoTracking().SingleAsync(candidate => candidate.Action == AuditActions.RoleUpdated, ct);
            (entry.ActorId, entry.TenantId, entry.Detail).ShouldBe((owner.UserId, owner.TenantId, roleId.ToString()));
        });
    }

    [Fact]
    public async Task AUD_TC2_AuditEntriesContainNoPasswordsTokensOrSecrets()
    {
        var ct = TestContext.Current.CancellationToken;

        // A real signup, email verification, failed login, login, password reset and login again.
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var verifyToken = await WaitForVerificationTokenAsync(Email);
        (await VerifyEmailLinkAsync(verifyToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Email, WrongPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var firstLogin = await LoginForTokensAsync(Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var resetToken = await WaitForResetTokenAsync(Email);
        (await ResetPasswordAsync(resetToken, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var secondResponse = await LoginAsync(Email, NewPassword, ct);
        var secondLogin = (await secondResponse.Content.ReadFromJsonAsync<AuthService.Api.Features.Login.LoginResponse>(Json, ct))!;

        var entries = await LoadAuditAsync();
        string passwordHash = null!;
        await WithDbAsync(async db => passwordHash = (await db.Users.AsNoTracking().SingleAsync(ct)).PasswordHash!);

        // Not vacuous: all of those events were recorded.
        entries.Select(entry => entry.Action).Distinct().ShouldBeSubsetOf(
        [
            AuditActions.SigningKeyCreated, AuditActions.SignupCompleted, AuditActions.EmailVerified,
            AuditActions.LoginFailed, AuditActions.LoginSucceeded, AuditActions.PasswordResetCompleted,
        ]);
        entries.ShouldContain(entry => entry.Action == AuditActions.PasswordResetCompleted);
        entries.Count(entry => entry.Action == AuditActions.LoginSucceeded).ShouldBe(2);

        var everything = JsonSerializer.Serialize(entries);
        foreach (var secret in new[]
        {
            DefaultPassword, WrongPassword, NewPassword,
            firstLogin.AccessToken, firstLogin.RefreshToken, secondLogin.AccessToken, secondLogin.RefreshToken,
            verifyToken, resetToken, passwordHash, Email,
        })
        {
            everything.Contains(secret).ShouldBeFalse("an audit entry must never contain a password, token, hash or address");
        }
    }

    [Fact(Skip = "Enabled once TOK-01 (tenant-scoped tokens) and ROL (admin permissions) exist, and the tenant audit endpoint is built.")]
    public Task AUD_TC3_ATenantAdminReadsOnlyTheirOwnTenantsAuditLog() => Task.CompletedTask;

    [Fact]
    public async Task AUD_TC4_AnAuditEntryCannotBeEditedOrDeletedThroughAnyRoute()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var before = await LoadAuditAsync();
        before.ShouldNotBeEmpty();
        var id = before[0].Id;

        // No endpoint can change an entry: there is none to find.
        foreach (var path in new[] { $"/api/auth/audit/{id}", $"/api/auth/tenants/me/audit/{id}", $"/api/auth/admin/audit/{id}" })
        {
            foreach (var method in new[] { HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
            {
                var response = await Client.SendAsync(new HttpRequestMessage(method, path), ct);
                response.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
            }
        }

        // And the database itself refuses, even to someone with direct access through the application's own connection.
        var update = await Should.ThrowAsync<PostgresException>(PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString, "UPDATE audit_entries SET action = 'tampered'", ct));
        update.MessageText.ShouldContain("append-only");
        var delete = await Should.ThrowAsync<PostgresException>(PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString, "DELETE FROM audit_entries", ct));
        delete.MessageText.ShouldContain("append-only");
        var truncate = await Should.ThrowAsync<PostgresException>(PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString, "TRUNCATE audit_entries", ct));
        truncate.MessageText.ShouldContain("append-only");

        (await LoadAuditAsync()).Select(entry => (entry.Id, entry.Action)).ShouldBe(before.Select(entry => (entry.Id, entry.Action)));
    }

    [Fact]
    public async Task AUD_TC5_TheRetentionJobRemovesOnlyEntriesOlderThanOneYear()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        await WithDbAsync(async db =>
        {
            db.AuditEntries.AddRange(
                OldEntry("test.old_366_days", now.AddDays(-366)),
                OldEntry("test.just_over_a_year", now.AddDays(-365).AddMinutes(-1)),
                OldEntry("test.just_under_a_year", now.AddDays(-365).AddMinutes(1)),
                OldEntry("test.yesterday", now.AddDays(-1)));
            await db.SaveChangesAsync(ct);
        });

        int removed;
        await using (var scope = Harness.CreateScope())
        {
            removed = await scope.ServiceProvider.GetRequiredService<AuditRetention>().PurgeAsync(now, ct);
        }

        removed.ShouldBe(2);
        var remaining = (await LoadAuditAsync()).Select(entry => entry.Action).ToList();
        remaining.ShouldContain("test.just_under_a_year");
        remaining.ShouldContain("test.yesterday");
        remaining.ShouldNotContain("test.old_366_days");
        remaining.ShouldNotContain("test.just_over_a_year");

        // The job's permission to delete lasted only its own transaction: a direct delete is refused again.
        await Should.ThrowAsync<PostgresException>(PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString, "DELETE FROM audit_entries", ct));
    }

    [Fact]
    public async Task AUD01_TheRetentionWorkerDeletesOldEntriesOnItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthApiHarness.CreateAsync(
            Postgres,
            ct,
            new HarnessOptions
            {
                Settings = { ["Auth:Audit:RetentionEnabled"] = "true", ["Auth:Audit:RetentionCheckSeconds"] = "1" },
            });

        await using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthService.Infrastructure.Persistence.AuthDbContext>();
            db.AuditEntries.Add(OldEntry("test.two_years_old", DateTimeOffset.UtcNow.AddDays(-730)));
            db.AuditEntries.Add(OldEntry("test.recent", DateTimeOffset.UtcNow.AddDays(-2)));
            await db.SaveChangesAsync(ct);
        }

        // Nobody calls the job: the worker finds the old entry on its next pass.
        await Eventually.UntilAsync(
            async () =>
            {
                await using var scope = harness.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AuthService.Infrastructure.Persistence.AuthDbContext>();
                return !await db.AuditEntries.AnyAsync(entry => entry.Action == "test.two_years_old", ct);
            },
            "the retention worker deleted the two-year-old entry");

        await using var checkScope = harness.CreateScope();
        (await checkScope.ServiceProvider.GetRequiredService<AuthService.Infrastructure.Persistence.AuthDbContext>()
            .AuditEntries.AnyAsync(entry => entry.Action == "test.recent", ct)).ShouldBeTrue("a recent entry must be kept");
    }

    private static AuditEntry OldEntry(string action, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.CreateVersion7(),
        OccurredAt = occurredAt,
        Action = action,
        ActorType = AuditActorType.System,
    };

    private async Task<List<AuditEntry>> LoadAuditAsync()
    {
        List<AuditEntry> entries = [];
        await WithDbAsync(async db => entries = await db.AuditEntries.AsNoTracking()
            .OrderBy(entry => entry.OccurredAt).ThenBy(entry => entry.Id)
            .ToListAsync(TestContext.Current.CancellationToken));
        return entries;
    }
}
