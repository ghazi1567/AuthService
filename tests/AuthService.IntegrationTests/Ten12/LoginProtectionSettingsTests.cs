using System.Net;
using AuthService.Infrastructure.Persistence;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ten12;

/// <summary>
/// TEN-12 (AUTH-130): the per-IP limit and the configurable thresholds. Runs the service with a low IP limit and a short
/// lockout, unlike the other login tests, which use a very high IP limit and the defaults.
/// Test method names start with their Test Case ID (AUTH_TC47) per the workbook convention.
/// </summary>
public sealed class LoginProtectionSettingsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    protected override HarnessOptions Options { get; } = new()
    {
        Settings =
        {
            ["Auth:LoginProtection:IpLimit"] = "5",
            ["Auth:LoginProtection:MaxFailedAttempts"] = "2",
            ["Auth:LoginProtection:LockoutMinutes"] = "1",
        },
    };

    [Fact]
    public async Task AUTH_TC47_ManyLoginAttemptsFromOneIpAcrossAccountsAreThrottledWith429()
    {
        var ct = TestContext.Current.CancellationToken;

        // One attempt each against five different accounts: no account is locked, but the address is using its allowance.
        for (var account = 1; account <= 5; account++)
        {
            (await LoginAsync($"user{account}@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var throttled = await LoginAsync("user6@acme.com", "not the password", ct);

        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadProblemAsync(throttled, ct)).Code.ShouldBe("rate_limited");
        throttled.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task TEN12_TheNumberOfFailuresAndTheLockoutLengthAreConfigurable()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        // With the limit set to 2, the second wrong password locks the account.
        (await LoginAsync("owner@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync("owner@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoginAsync("owner@acme.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        await using var scope = Harness.CreateScope();
        var user = await scope.ServiceProvider.GetRequiredService<AuthDbContext>()
            .Users.AsNoTracking().SingleAsync(candidate => candidate.Email == "owner@acme.com", ct);
        (user.LockedUntil!.Value - DateTimeOffset.UtcNow).ShouldBeInRange(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90));
    }
}
