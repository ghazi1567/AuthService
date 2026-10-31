using AuthService.Api.Email;
using AuthService.Api.Email.Providers;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>
/// NTF-01 (AUTH-135) Email Sending: queueing, the templates and delivery by the background worker.
/// The tests that need a signup (NTF_TC1, and the signup variant of NTF_TC2) arrive with TEN-11.
/// These tests have no workbook Test Case ID yet: the spec's acceptance criteria cover them.
/// </summary>
public sealed class EmailSendingTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private InMemoryEmailSender Provider
    {
        get
        {
            using var scope = Harness.CreateScope();
            return scope.ServiceProvider.GetRequiredService<InMemoryEmailSender>();
        }
    }

    [Fact]
    public async Task NTF01_Delivery_QueuedEmailIsSentThroughTheConfiguredProviderAndItsPayloadIsCleared()
    {
        var ct = TestContext.Current.CancellationToken;

        await EmailTestSupport.EnqueueAsync(
            Harness, EmailTemplate.Verification, "owner@acme.com", new() { ["token"] = "tok-123" }, ct);
        await EmailTestSupport.WaitUntilStatusAsync(Harness, EmailOutboxStatus.Sent, 1, ct);

        var sent = Provider.Sent.ShouldHaveSingleItem();
        sent.To.ShouldBe("owner@acme.com");
        sent.Subject.ShouldBe("Verify your email address");
        sent.TextBody.ShouldContain("https://app.test/verify-email?token=tok-123");
        sent.HtmlBody.ShouldContain("https://app.test/verify-email?token=tok-123");

        var row = (await EmailTestSupport.LoadOutboxAsync(Harness, ct)).ShouldHaveSingleItem();
        row.Attempts.ShouldBe(1);
        row.SentAt.ShouldNotBeNull();
        row.LastError.ShouldBeNull();
        row.PayloadJson.ShouldBeNull("the token must not stay in the database once the email is sent");
    }

    [Fact]
    public void NTF01_Templates_EachTemplateRendersItsSubjectAndLink()
    {
        var options = new EmailOptions { PublicBaseUrl = "https://app.test/" };

        var verification = EmailTemplates.Render(EmailTemplate.Verification, "a@b.co", new Dictionary<string, string> { ["token"] = "t" }, options);
        verification.Subject.ShouldBe("Verify your email address");
        verification.TextBody.ShouldContain("https://app.test/verify-email?token=t");

        var reset = EmailTemplates.Render(EmailTemplate.PasswordReset, "a@b.co", new Dictionary<string, string> { ["token"] = "t" }, options);
        reset.Subject.ShouldBe("Reset your password");
        reset.TextBody.ShouldContain("https://app.test/reset-password?token=t");

        var invitation = EmailTemplates.Render(
            EmailTemplate.Invitation, "a@b.co", new Dictionary<string, string> { ["token"] = "t", ["tenantName"] = "Acme Ltd" }, options);
        invitation.Subject.ShouldBe("You have been invited to Acme Ltd");
        invitation.TextBody.ShouldContain("https://app.test/accept-invitation?token=t");
    }

    [Fact]
    public void NTF01_Templates_TokenIsUrlEncodedAndTenantNameIsHtmlEncoded()
    {
        var options = new EmailOptions { PublicBaseUrl = "https://app.test" };

        var message = EmailTemplates.Render(
            EmailTemplate.Invitation,
            "a@b.co",
            new Dictionary<string, string> { ["token"] = "a b/c+d", ["tenantName"] = "<b>Evil</b> & Co" },
            options);

        message.TextBody.ShouldContain("token=a%20b%2Fc%2Bd");
        message.HtmlBody.ShouldContain("&lt;b&gt;Evil&lt;/b&gt; &amp; Co");
        message.HtmlBody.ShouldNotContain("<b>Evil</b>");
    }

    [Fact]
    public async Task NTF01_Enqueue_MissingTemplateValueFailsImmediatelyAndQueuesNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<InvalidOperationException>(
            EmailTestSupport.EnqueueAsync(Harness, EmailTemplate.Verification, "owner@acme.com", [], ct));

        (await EmailTestSupport.LoadOutboxAsync(Harness, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NTF01_Delivery_WorkersRunningAtTheSameTimeSendEachEmailExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        const int count = 12;

        for (var i = 0; i < count; i++)
        {
            await EmailTestSupport.EnqueueAsync(
                Harness, EmailTemplate.Verification, $"user{i}@acme.com", new() { ["token"] = $"token-{i}" }, ct);
        }

        // Three extra deliveries race the background worker for the same rows.
        await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ =>
        {
            await using var scope = Harness.CreateScope();
            await scope.ServiceProvider.GetRequiredService<EmailDeliveryService>().DeliverDueAsync(ct);
        }));

        await EmailTestSupport.WaitUntilStatusAsync(Harness, EmailOutboxStatus.Sent, count, ct);
        await Task.Delay(300, ct); // long enough for any duplicate send to show up

        Provider.Sent.Count.ShouldBe(count, "no email may be sent twice");
        Provider.Sent.Select(message => message.To).Distinct().Count().ShouldBe(count);
        (await EmailTestSupport.LoadOutboxAsync(Harness, ct)).ShouldAllBe(message => message.Attempts == 1);
    }
}
