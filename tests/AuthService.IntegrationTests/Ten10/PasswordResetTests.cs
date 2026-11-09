using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Common;
using AuthService.Api.Features.PasswordReset;
using AuthService.Api.Tokens;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten10;

/// <summary>
/// TEN-10 (AUTH-128) Password Reset, through the real forgot, reset, login and refresh endpoints. The reset link is read
/// from the email the service sent, exactly as a person would.
/// Test method names start with their Test Case ID (AUTH_TC35..AUTH_TC38) per the workbook convention.
/// </summary>
public sealed class PasswordResetTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";
    private const string NewPassword = "a brand new passphrase";

    [Fact]
    public async Task AUTH_TC35_ResetThroughTheEmailedLinkThenLogInWithTheNewPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);
        var reset = await ResetPasswordAsync(token, NewPassword, ct);

        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reset.Content.ReadFromJsonAsync<ResetPasswordResponse>(Json, ct))!.Reset.ShouldBeTrue();
        (await LoginAsync(Email, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // The link was sent by the real email path, and only its hash was kept.
        await WithDbAsync(async db =>
        {
            var stored = await db.PasswordResetTokens.SingleAsync(ct);
            stored.UsedAt.ShouldNotBeNull();
            stored.TokenHash.ShouldBe(SecretToken.Hash(token));
            stored.TokenHash.ShouldNotBe(token, "only a hash of the token may be stored");
            (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromMinutes(60));
        });
    }

    [Fact]
    public async Task TEN10_ResettingThePasswordOfAnUnverifiedAccountVerifiesItsEmailSoLoginWorksAtOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var refused = await LoginAsync(Email, DefaultPassword, ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(refused, ct)).Code.ShouldBe("email_not_verified");

        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var reset = await ResetPasswordAsync(await WaitForResetTokenAsync(Email), NewPassword, ct);

        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Email, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "the reset proved control of the inbox");
        await WithDbAsync(async db => (await db.Users.AsNoTracking().SingleAsync(user => user.Email == Email, ct)).EmailVerifiedAt.ShouldNotBeNull());
    }

    [Fact]
    public async Task TEN10_ResettingThePasswordOfAVerifiedAccountKeepsTheTimeItWasVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        DateTimeOffset? verifiedAt = null;
        await WithDbAsync(async db => verifiedAt = (await db.Users.AsNoTracking().SingleAsync(user => user.Email == Email, ct)).EmailVerifiedAt);
        verifiedAt.ShouldNotBeNull();

        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ResetPasswordAsync(await WaitForResetTokenAsync(Email), NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db => (await db.Users.AsNoTracking().SingleAsync(user => user.Email == Email, ct)).EmailVerifiedAt.ShouldBe(verifiedAt));
    }

    [Fact]
    public async Task AUTH_TC36_ForgotPasswordForAnUnknownEmailGetsTheSameResponseAndSendsNoEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        var real = await ForgotPasswordAsync(Email, ct);
        var unknown = await ForgotPasswordAsync("nobody@acme.com", ct);

        real.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var expectedBody = await real.Content.ReadAsStringAsync(ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await unknown.Content.ReadAsStringAsync(ct)).ShouldBe(expectedBody, "the response must not reveal whether an email exists");

        // Only the real account is sent a link.
        await WaitForResetTokenAsync(Email);
        await Task.Delay(300, ct);
        SentEmails.Count(message => message.To == Email && message.Subject == "Reset your password").ShouldBe(1);
        SentEmails.Count(message => message.To == "nobody@acme.com").ShouldBe(0);
        await WithDbAsync(async db => (await db.PasswordResetTokens.CountAsync(ct)).ShouldBe(1));
    }

    [Fact]
    public async Task AUTH_TC37_ReusingALinkOrUsingOneOlderThanAnHourIsRejectedWith410()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var usedToken = await WaitForResetTokenAsync(Email, nth: 1);
        (await ResetPasswordAsync(usedToken, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Used a second time.
        var reused = await ResetPasswordAsync(usedToken, "yet another passphrase", ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(reused, ct)).Code.ShouldBe("reset_link_invalid");
        (await LoginAsync(Email, "yet another passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Older than an hour.
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var staleToken = await WaitForResetTokenAsync(Email, nth: 2);
        await WithDbAsync(db => db.PasswordResetTokens
            .Where(token => token.TokenHash == SecretToken.Hash(staleToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), ct));

        var expired = await ResetPasswordAsync(staleToken, "yet another passphrase", ct);
        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(expired, ct)).ShouldBe(await ReadProblemAsync(reused, ct), "expired and reused links look the same");
    }

    [Fact]
    public async Task AUTH_TC38_ResettingThePasswordRevokesEveryActiveSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var laptop = await LoginForTokensAsync(Email, ct);
        var phone = await LoginForTokensAsync(Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);

        (await ResetPasswordAsync(token, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db =>
        {
            var sessions = await db.RefreshTokens.ToListAsync(ct);
            sessions.Count.ShouldBe(2);
            sessions.ShouldAllBe(session => session.RevokedAt != null, "every session must be signed out");
        });
        (await RefreshAsync(laptop.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RefreshAsync(phone.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN10_AResetEndsALockoutAndTheRunOfWrongPasswords()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(Email, "not the password", ct);
        }

        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A locked-out person can still reset, because resetting does not need a login.
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);
        (await ResetPasswordAsync(token, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await LoginAsync(Email, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await WithDbAsync(async db =>
        {
            var user = await db.Users.AsNoTracking().SingleAsync(ct);
            user.FailedLoginCount.ShouldBe(0);
            user.LockedUntil.ShouldBeNull();
        });
    }

    [Fact]
    public async Task TEN10_AWeakNewPasswordIsRejectedWith400AndTheLinkIsNotUsedUp()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);

        (await ResetPasswordAsync(token, "short", ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The same link still works with a good password, and the old password was not changed in between.
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ResetPasswordAsync(token, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN10_AnUnknownTokenIsRejectedWith410()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await ResetPasswordAsync("not-a-token-we-issued", NewPassword, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("reset_link_invalid");
    }

    [Fact]
    public async Task TEN10_TheSameLinkUsedAtTheSameTimeSucceedsExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var token = await WaitForResetTokenAsync(Email);

        var responses = await Task.WhenAll(
            ResetPasswordAsync(token, "first new passphrase", ct),
            ResetPasswordAsync(token, "second new passphrase", ct),
            ResetPasswordAsync(token, "third new passphrase", ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1, "a reset link may be used once");
        responses.Count(response => response.StatusCode == HttpStatusCode.Gone).ShouldBe(2);
    }

    [Fact]
    public async Task TEN10_OtherOutstandingLinksStopWorkingOnceThePasswordIsReset()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await ForgotPasswordAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var first = await WaitForResetTokenAsync(Email, nth: 1);
        var second = await WaitForResetTokenAsync(Email, nth: 2);

        (await ResetPasswordAsync(first, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ResetPasswordAsync(second, "someone else's passphrase", ct)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await LoginAsync(Email, NewPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN10_ForgotPasswordMatchesTheEmailIgnoringCaseAndWhitespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        (await ForgotPasswordAsync(" Owner@ACME.com ", ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForResetTokenAsync(Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN10_Validation_ForgotWithoutAnEmailIsRejectedWith400(string? email)
    {
        var ct = TestContext.Current.CancellationToken;

        (await ForgotPasswordAsync(email, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null, "a good new passphrase")]
    [InlineData("   ", "a good new passphrase")]
    [InlineData("some-token", null)]
    [InlineData("some-token", "")]
    public async Task TEN10_Validation_ResetWithAMissingTokenOrPasswordIsRejectedWith400(string? token, string? newPassword)
    {
        var ct = TestContext.Current.CancellationToken;

        (await ResetPasswordAsync(token, newPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TEN10_Validation_ANewPasswordLongerThanTheLimitIsRejectedWith400()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await ResetPasswordAsync("some-token", new string('x', PasswordPolicy.MaxLength + 1), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
