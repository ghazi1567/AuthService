using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Features.Tenants;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Aud01;

/// <summary>
/// AUD-01 (AUTH-134): each event the audit log records today, checked at the real endpoint that causes it. Events from stories
/// not built yet (roles, API keys, invitations, subscriptions) are recorded when those stories land.
/// These tests have no workbook Test Case ID yet: the spec's acceptance criteria cover them.
/// </summary>
public sealed class AuditEventTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string WrongPassword = "not the password";

    [Fact]
    public async Task AUD01_SignupRecordsTheNewUserAndTheTenantItCreated()
    {
        var ct = TestContext.Current.CancellationToken;

        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        var entry = (await EntriesAsync(AuditActions.SignupCompleted)).ShouldHaveSingleItem();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.ActorId.ShouldBe(account.UserId);
        entry.TenantId.ShouldBe(account.TenantId);
        entry.OccurredAt.ShouldBe(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AUD01_ASuccessfulLoginIsRecordedWithNoTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        await LoginForTokensAsync(Email, ct);

        var entry = (await EntriesAsync(AuditActions.LoginSucceeded)).ShouldHaveSingleItem();
        entry.ActorType.ShouldBe(AuditActorType.User);
        entry.ActorId.ShouldBe(account.UserId);
        entry.TenantId.ShouldBeNull("login concerns no single tenant");
        entry.Detail.ShouldBeNull();
    }

    [Fact]
    public async Task AUD01_FailedLoginsAreRecordedWithoutSayingWhichEmailWasTried()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        await LoginAsync(Email, WrongPassword, ct);
        await LoginAsync("nobody@acme.com", WrongPassword, ct);

        var failures = await EntriesAsync(AuditActions.LoginFailed);
        failures.Count.ShouldBe(2);

        var known = failures.Single(entry => entry.Detail == "wrong_password");
        known.ActorType.ShouldBe(AuditActorType.User);
        known.ActorId.ShouldBe(account.UserId);

        var unknown = failures.Single(entry => entry.Detail == "unknown_account");
        unknown.ActorType.ShouldBe(AuditActorType.Anonymous);
        unknown.ActorId.ShouldBeNull();
    }

    [Fact]
    public async Task AUD01_ALockoutIsRecordedOnceAndAttemptsWhileLockedAreRecordedToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(Email, WrongPassword, ct);
        }

        await LoginAsync(Email, DefaultPassword, ct);

        var lockouts = await EntriesAsync(AuditActions.AccountLocked);
        lockouts.ShouldHaveSingleItem().ActorId.ShouldBe(account.UserId);
        (await EntriesAsync(AuditActions.LoginFailed)).Count(entry => entry.Detail == "wrong_password").ShouldBe(5);
        (await EntriesAsync(AuditActions.LoginFailed)).ShouldContain(entry => entry.Detail == "account_locked");
    }

    [Fact]
    public async Task AUD01_ALoginRefusedAfterTheRightPasswordIsRecordedWithItsReason()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await EntriesAsync(AuditActions.LoginFailed)).ShouldHaveSingleItem().Detail.ShouldBe("email_not_verified");
    }

    [Fact]
    public async Task AUD01_VerifyingTheEmailIsRecordedOnceEvenIfTheLinkIsOpenedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForVerificationTokenAsync(Email);

        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EntriesAsync(AuditActions.EmailVerified)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task AUD01_ACompletedPasswordResetIsRecordedButARefusedOneIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        (await ResetPasswordAsync("not-a-token-we-issued", "a good new passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await EntriesAsync(AuditActions.PasswordResetCompleted)).ShouldBeEmpty();

        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);
        (await ResetPasswordAsync(token, "a good new passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await EntriesAsync(AuditActions.PasswordResetCompleted)).ShouldHaveSingleItem().ActorId.ShouldBe(account.UserId);
    }

    [Fact]
    public async Task AUD01_CreatingAnAdditionalTenantIsRecordedAgainstThatTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);

        var response = await PostAsync(CreateTenantEndpoints.Route, new { tenantName = "Second Co" }, login.AccessToken, ct);
        var created = (await response.Content.ReadFromJsonAsync<CreateTenantResponse>(Json, ct))!;

        var entry = (await EntriesAsync(AuditActions.TenantCreated)).ShouldHaveSingleItem();
        entry.ActorId.ShouldBe(account.UserId);
        entry.TenantId.ShouldBe(created.TenantId);
    }

    [Fact]
    public async Task AUD01_ReusingARefreshTokenIsRecordedAsASecurityEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);
        (await RefreshAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await RefreshAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await EntriesAsync(AuditActions.RefreshTokenReuseDetected)).ShouldHaveSingleItem().ActorId.ShouldBe(account.UserId);
    }

    [Fact]
    public async Task AUD01_TheFirstSigningKeyIsRecordedAtStartup()
    {
        var entry = (await EntriesAsync(AuditActions.SigningKeyCreated)).ShouldHaveSingleItem();

        entry.ActorType.ShouldBe(AuditActorType.System);
        entry.Detail.ShouldBe("initial");
    }

    [Fact]
    public async Task AUD01_AnActionThatRollsBackLeavesNoAuditEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        await PostgresContainerFixture.ExecuteAsync(
            Database.ConnectionString,
            """
            CREATE FUNCTION fail_insert() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'simulated failure'; END $$;
            CREATE TRIGGER fail_insert BEFORE INSERT ON tenant_memberships FOR EACH ROW EXECUTE FUNCTION fail_insert();
            """,
            ct);

        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        (await EntriesAsync(AuditActions.SignupCompleted)).ShouldBeEmpty("an entry is saved with its action or not at all");
    }

    [Theory]
    [InlineData("Has Capitals")]
    [InlineData("has spaces")]
    [InlineData("")]
    [InlineData("a-token-like-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    public void AUD01_ADetailThatIsNotAShortCodeIsRefusedSoASecretCannotBeRecordedByMistake(string detail)
    {
        using var scope = Harness.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

        Should.Throw<ArgumentException>(() => audit.Record("test.event", AuditActor.System, detail: detail));
    }

    [Fact]
    public void AUD01_AShortCodeDetailIsAccepted()
    {
        using var scope = Harness.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLog>();

        Should.NotThrow(() => audit.Record("test.event", AuditActor.System, detail: "wrong_password.attempt_1"));
    }

    private async Task<List<AuditEntry>> EntriesAsync(string action)
    {
        List<AuditEntry> entries = [];
        await WithDbAsync(async db => entries = await db.AuditEntries.AsNoTracking()
            .Where(entry => entry.Action == action)
            .OrderBy(entry => entry.OccurredAt)
            .ToListAsync(TestContext.Current.CancellationToken));
        return entries;
    }
}
