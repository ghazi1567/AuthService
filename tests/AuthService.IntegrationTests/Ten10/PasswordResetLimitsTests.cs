using System.Net;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten10;

/// <summary>
/// TEN-10 (AUTH-128): the limits on asking for password reset links. Runs the service with a real cooldown and a low per-IP
/// limit, unlike the other password reset tests, which turn the cooldown off and use a very high limit.
/// These tests have no workbook Test Case ID yet: the spec only says requests are rate-limited per email and per IP.
/// </summary>
public sealed class PasswordResetLimitsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    protected override HarnessOptions Options { get; } = new()
    {
        Settings =
        {
            ["Auth:PasswordReset:RequestCooldownSeconds"] = "60",
            ["Auth:PasswordReset:IpLimit"] = "3",
        },
    };

    [Fact]
    public async Task TEN10_AnAccountThatWasJustSentALinkIsNotSentAnotherInsideTheCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForResetTokenAsync(Email);

        // Answered normally, but nothing more is sent: a stranger cannot flood the owner's inbox.
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(300, ct);
        SentEmails.Count(message => message.To == Email && message.Subject == "Reset your password").ShouldBe(1);
        await WithDbAsync(async db => (await db.PasswordResetTokens.CountAsync(ct)).ShouldBe(1));

        // Once the cooldown has passed, a new link is sent.
        await WithDbAsync(db => db.PasswordResetTokens.ExecuteUpdateAsync(
            setters => setters.SetProperty(token => token.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-2)), ct));
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForResetTokenAsync(Email, nth: 2);
    }

    [Fact]
    public async Task TEN10_AnIpAddressThatAsksTooOftenGets429()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await ForgotPasswordAsync($"user{attempt}@acme.com", ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await ForgotPasswordAsync("user4@acme.com", ct);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadProblemAsync(limited, ct)).Code.ShouldBe("rate_limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }

    [Fact]
    public async Task TEN10_TheLimitCoversTryingToResetWithGuessedLinksToo()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await ResetPasswordAsync($"guess-{attempt}", "a good new passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        }

        (await ResetPasswordAsync("guess-4", "a good new passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }
}
