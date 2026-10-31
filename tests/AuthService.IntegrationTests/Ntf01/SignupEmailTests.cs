using System.Net;
using AuthService.Api.Email;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>
/// NTF-01 (AUTH-135) Email Sending, as seen from signup (TEN-11 queues the verification email).
/// Test method names start with their Test Case ID (NTF_TC1, NTF_TC2) per the workbook convention.
/// </summary>
public sealed class SignupEmailTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task NTF_TC1_SignUpSendsTheVerificationEmailThroughTheConfiguredProviderWithTheRightTemplate()
    {
        var ct = TestContext.Current.CancellationToken;

        (await SignUpAsync("Acme Ltd", "owner@acme.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = await WaitForVerificationTokenAsync("owner@acme.com");
        await EmailTestSupport.WaitUntilStatusAsync(Harness, EmailOutboxStatus.Sent, 1, ct);

        var sent = SentEmails.ShouldHaveSingleItem();
        sent.To.ShouldBe("owner@acme.com");
        sent.Subject.ShouldBe("Verify your email address");
        sent.HtmlBody.ShouldContain($"https://app.test/verify-email?token={Uri.EscapeDataString(token)}");
        sent.HtmlBody.ShouldContain("Verify email address");

        var queued = (await EmailTestSupport.LoadOutboxAsync(Harness, ct)).ShouldHaveSingleItem();
        queued.Template.ShouldBe(EmailTemplate.Verification);
        queued.ToAddress.ShouldBe("owner@acme.com");
    }
}

/// <summary>NTF-01 (AUTH-135): the signup variant of NTF_TC2, where the email provider fails while a person signs up.</summary>
public sealed class SignupEmailFailureTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private readonly FlakyEmailSender _provider = new(failFirst: 2);

    protected override HarnessOptions Options => new()
    {
        ConfigureServices = services => services.AddSingleton<IEmailSender>(_provider),
    };

    protected override IReadOnlyList<EmailMessage> SentEmails => _provider.Sent;

    [Fact]
    public async Task NTF_TC2_SignupStillSucceedsWhenTheEmailProviderFailsAndTheEmailIsRetriedAndLogged()
    {
        var ct = TestContext.Current.CancellationToken;

        // The provider refuses the first two sends, yet signup is not affected.
        var signup = await SignUpAsync("Acme Ltd", "owner@acme.com", DefaultPassword, ct);
        signup.StatusCode.ShouldBe(HttpStatusCode.Created);

        // The email still arrives, after retries, and every failure was logged.
        var token = await WaitForVerificationTokenAsync("owner@acme.com");
        _provider.Attempts.ShouldBe(3);
        Harness.Logs.Entries.Count(entry => entry.Contains("[Warning]") && entry.Contains("failed, will retry")).ShouldBe(2);

        // And the link in that late email works.
        (await VerifyEmailLinkAsync(token, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await LoginAsync("owner@acme.com", DefaultPassword, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
