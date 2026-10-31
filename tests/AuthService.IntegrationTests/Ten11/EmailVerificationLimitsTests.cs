using System.Net;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten11;

/// <summary>
/// TEN-11 (AUTH-129): the limits on asking for verification emails. Runs the service with a real cooldown and a low
/// per-IP limit, unlike the other TEN-11 tests, which turn the cooldown off.
/// These tests have no workbook Test Case ID yet: the spec only says resend is rate-limited.
/// </summary>
public sealed class EmailVerificationLimitsTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    protected override HarnessOptions Options { get; } = new()
    {
        Settings =
        {
            ["Auth:EmailVerification:ResendCooldownSeconds"] = "60",
            ["Auth:EmailVerification:ResendIpLimit"] = "3",
        },
    };

    [Fact]
    public async Task TEN11_Resend_AnAccountThatWasJustSentALinkIsNotSentAnotherInsideTheCooldown()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await WaitForVerificationTokenAsync(Email);

        // Signup just sent a link, so this request is answered normally but sends nothing.
        (await ResendVerificationAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await Task.Delay(300, ct);
        SentEmails.Count(message => message.To == Email).ShouldBe(1);
        await WithDbAsync(async db => (await db.EmailVerificationTokens.CountAsync(ct)).ShouldBe(1));

        // Once the cooldown has passed, a new link is sent.
        await WithDbAsync(db => db.EmailVerificationTokens.ExecuteUpdateAsync(
            setters => setters.SetProperty(token => token.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-2)), ct));
        (await ResendVerificationAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForVerificationTokenAsync(Email, nth: 2);
    }

    [Fact]
    public async Task TEN11_Resend_AnIpAddressThatAsksTooOftenGets429()
    {
        var ct = TestContext.Current.CancellationToken;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            (await ResendVerificationAsync($"user{attempt}@acme.com", ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        }

        var limited = await ResendVerificationAsync("user4@acme.com", ct);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ReadProblemAsync(limited, ct)).Code.ShouldBe("rate_limited");
        limited.Headers.RetryAfter.ShouldNotBeNull();
    }
}
