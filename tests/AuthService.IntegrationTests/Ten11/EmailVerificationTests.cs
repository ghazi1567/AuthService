using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Api.Common;
using AuthService.Api.Features.EmailVerification;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AuthService.IntegrationTests.Ten11;

/// <summary>
/// TEN-11 (AUTH-129) Email Verification, through the real signup, verify, resend and login endpoints. The verification
/// link is read from the email the service sent, exactly as a person would.
/// Test method names start with their Test Case ID (AUTH_TC39..AUTH_TC42) per the workbook convention; AUTH_TC43 needs invitations and is in the TEN-08 tests.
/// </summary>
public sealed class EmailVerificationTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    [Fact]
    public async Task AUTH_TC39_SignUpOpenTheVerificationLinkThenLogIn()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForVerificationTokenAsync(Email);

        var verify = await VerifyEmailLinkAsync(token, ct);

        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await verify.Content.ReadFromJsonAsync<VerifyEmailResponse>(Json, ct))!.Verified.ShouldBeTrue();
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db =>
        {
            (await db.Users.SingleAsync(ct)).EmailVerifiedAt.ShouldNotBeNull();
            var stored = await db.EmailVerificationTokens.SingleAsync(ct);
            stored.UsedAt.ShouldNotBeNull("a link is single-use");
            stored.TokenHash.ShouldBe(SecretToken.Hash(token));
            stored.TokenHash.ShouldNotBe(token, "only a hash of the token may be stored");
            (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromHours(24));
        });
    }

    [Fact]
    public async Task AUTH_TC40_LoginBeforeVerifyingTheEmailIsRejectedWith403AndADistinctCode()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var login = await LoginAsync(Email, DefaultPassword, ct);

        login.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(login, ct)).Code.ShouldBe("email_not_verified");
    }

    [Fact]
    public async Task AUTH_TC41_AnExpiredLinkIsRejectedWith410AndANewLinkVerifiesTheEmail()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var expiredToken = await WaitForVerificationTokenAsync(Email, nth: 1);
        await WithDbAsync(db => db.EmailVerificationTokens.ExecuteUpdateAsync(
            setters => setters.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)), ct));

        var expired = await VerifyEmailLinkAsync(expiredToken, ct);

        expired.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(expired, ct)).Code.ShouldBe("verification_link_invalid");

        (await ResendVerificationAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var newToken = await WaitForVerificationTokenAsync(Email, nth: 2);
        newToken.ShouldNotBe(expiredToken);
        (await VerifyEmailLinkAsync(newToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync(Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AUTH_TC42_ResendingForAnUnknownEmailGetsTheSameResponseAsForARealOne()
    {
        var ct = TestContext.Current.CancellationToken;
        const string verifiedEmail = "verified@acme.com";
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await SignUpVerifiedAsync("Beta Co", verifiedEmail, ct);
        await WaitForVerificationTokenAsync(Email);
        await WaitForVerificationTokenAsync(verifiedEmail);

        var unverified = await ResendVerificationAsync(Email, ct);
        var unknown = await ResendVerificationAsync("nobody@acme.com", ct);
        var verified = await ResendVerificationAsync(verifiedEmail, ct);

        unverified.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var expectedBody = await unverified.Content.ReadAsStringAsync(ct);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await unknown.Content.ReadAsStringAsync(ct)).ShouldBe(expectedBody, "the response must not reveal whether an email exists");
        verified.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await verified.Content.ReadAsStringAsync(ct)).ShouldBe(expectedBody);

        // Only the real, unverified account is sent a second email.
        await WaitForVerificationTokenAsync(Email, nth: 2);
        await Task.Delay(300, ct);
        SentEmails.Count(message => message.To == Email).ShouldBe(2);
        SentEmails.Count(message => message.To == verifiedEmail).ShouldBe(1);
        SentEmails.Count(message => message.To == "nobody@acme.com").ShouldBe(0);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN11_Verify_OpeningTheSameLinkAgainIsAHarmlessNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForVerificationTokenAsync(Email);

        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        DateTimeOffset? verifiedAt = null;
        await WithDbAsync(async db => verifiedAt = (await db.Users.SingleAsync(ct)).EmailVerifiedAt);

        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db => (await db.Users.SingleAsync(ct)).EmailVerifiedAt.ShouldBe(verifiedAt));
    }

    [Fact]
    public async Task TEN11_Verify_ConcurrentOpensOfTheSameLinkAllSucceedAndVerifyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForVerificationTokenAsync(Email);

        var responses = await Task.WhenAll(
            VerifyEmailLinkAsync(token, ct),
            VerifyEmailLinkAsync(token, ct),
            VerifyEmailLinkAsync(token, ct));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.OK);
        await WithDbAsync(async db => (await db.Users.SingleAsync(ct)).EmailVerifiedAt.ShouldNotBeNull());
    }

    [Fact]
    public async Task TEN11_Verify_OtherOutstandingLinksAreRetiredOnceTheEmailIsVerified()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var first = await WaitForVerificationTokenAsync(Email, nth: 1);
        (await ResendVerificationAsync(Email, ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var second = await WaitForVerificationTokenAsync(Email, nth: 2);

        (await VerifyEmailLinkAsync(first, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        await WithDbAsync(async db =>
            (await db.EmailVerificationTokens.ToListAsync(ct)).ShouldAllBe(token => token.UsedAt != null));
        (await VerifyEmailLinkAsync(second, ct)).StatusCode.ShouldBe(HttpStatusCode.OK, "an already-verified address is a no-op");
    }

    [Fact]
    public async Task TEN11_Verify_AnUnknownLinkIsRejectedWith410()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await VerifyEmailLinkAsync("not-a-token-we-issued", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("verification_link_invalid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN11_Validation_MissingTokenIsRejectedWith400(string? token)
    {
        var ct = TestContext.Current.CancellationToken;

        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN11_Validation_ResendWithoutAnEmailIsRejectedWith400(string? email)
    {
        var ct = TestContext.Current.CancellationToken;

        (await ResendVerificationAsync(email, ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TEN11_Resend_MatchesTheEmailIgnoringCaseAndWhitespace()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await WaitForVerificationTokenAsync(Email);

        (await ResendVerificationAsync(" Owner@ACME.com ", ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await WaitForVerificationTokenAsync(Email, nth: 2);
    }
}
