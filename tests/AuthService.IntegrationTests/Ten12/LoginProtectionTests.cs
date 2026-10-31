using System.Net;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ten12;

/// <summary>
/// TEN-12 (AUTH-130) Login brute-force protection: the failure count and temporary lockout, with the default settings
/// (5 failures, 15 minutes), through the real login endpoint.
/// Test method names start with their Test Case ID (AUTH_TC44..AUTH_TC46) per the workbook convention.
/// </summary>
public sealed class LoginProtectionTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string WrongPassword = "not the password";

    [Fact]
    public async Task AUTH_TC44_FiveWrongPasswordsLockTheAccountForFifteenMinutes()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        await FailLoginsAsync(Email, 5, ct);

        // Even the right password is refused now, with the same generic 401.
        var locked = await LoginAsync(Email, DefaultPassword, ct);
        locked.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(locked, ct)).Code.ShouldBe("invalid_credentials");

        var user = await LoadUserAsync(Email);
        var untilLockEnds = user.LockedUntil!.Value - DateTimeOffset.UtcNow;
        untilLockEnds.ShouldBeInRange(TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(16));
        user.FailedLoginCount.ShouldBe(0, "the count starts again once the account is locked");

        // Trying again while locked neither counts nor extends the lock.
        await LoginAsync(Email, WrongPassword, ct);
        await LoginAsync(Email, DefaultPassword, ct);
        (await LoadUserAsync(Email)).LockedUntil.ShouldBe(user.LockedUntil);

        // The lockout is logged by user id: never by email.
        Harness.Logs.Entries.ShouldContain(entry => entry.Contains("locked for 15 minutes"));
        Harness.Logs.Entries.ShouldNotContain(entry => entry.Contains(Email));
    }

    [Fact]
    public async Task AUTH_TC45_LoggingInAfterTheLockoutEndsSucceedsAndResetsTheCount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await FailLoginsAsync(Email, 5, ct);
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The 15 minutes pass.
        await WithDbAsync(db => db.Users.ExecuteUpdateAsync(
            setters => setters.SetProperty(user => user.LockedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), ct));

        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var user = await LoadUserAsync(Email);
        user.FailedLoginCount.ShouldBe(0);
        user.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task AUTH_TC46_ALockedAccountGetsTheSameResponseAsABadPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        var badPassword = await LoginAsync(Email, WrongPassword, ct);
        await FailLoginsAsync(Email, 4, ct); // five in total, so the account is locked
        var lockedRightPassword = await LoginAsync(Email, DefaultPassword, ct);
        var lockedWrongPassword = await LoginAsync(Email, WrongPassword, ct);

        badPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var expected = await ReadProblemAsync(badPassword, ct);
        lockedRightPassword.StatusCode.ShouldBe(badPassword.StatusCode);
        (await ReadProblemAsync(lockedRightPassword, ct)).ShouldBe(expected, "status, title, detail and code must all match");
        lockedWrongPassword.StatusCode.ShouldBe(badPassword.StatusCode);
        (await ReadProblemAsync(lockedWrongPassword, ct)).ShouldBe(expected);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN12_ARightPasswordClearsTheRunOfFailures()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        await FailLoginsAsync(Email, 4, ct);
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoadUserAsync(Email)).FailedLoginCount.ShouldBe(0);

        // Four more would lock the account if the earlier four still counted.
        await FailLoginsAsync(Email, 4, ct);
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN12_TheLockoutIsPerAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);

        await FailLoginsAsync(Email, 5, ct);

        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync("boss@beta.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN12_FailuresForAnUnknownEmailAreNotCountedAnywhere()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        await FailLoginsAsync("nobody@acme.com", 10, ct);

        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task FailLoginsAsync(string email, int count, CancellationToken ct)
    {
        for (var attempt = 0; attempt < count; attempt++)
        {
            (await LoginAsync(email, WrongPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }
    }

    private async Task<User> LoadUserAsync(string email)
    {
        await using var scope = Harness.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .Users.AsNoTracking().SingleAsync(user => user.Email == email);
    }
}
