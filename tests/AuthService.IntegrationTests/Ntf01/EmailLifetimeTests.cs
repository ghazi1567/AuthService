using System.Net.Http.Json;
using AuthService.Api.Email;
using AuthService.Api.Features.Invitations;
using AuthService.Api.Features.Roles;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.Ntf01;

/// <summary>
/// NTF-01 (AUTH-135) and AUTH-266: the text of an email says how long its link works, and what it says is what the service does.
/// The service is started with lifetimes that are not the defaults, so a message that repeated a fixed sentence would fail.
/// </summary>
public sealed class EmailLifetimeTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    protected override HarnessOptions Options => new()
    {
        Settings =
        {
            ["Auth:EmailVerification:TokenHours"] = "48",
            ["Auth:PasswordReset:TokenMinutes"] = "30",
            ["Auth:Invitations:TokenDays"] = "3",
        },
    };

    [Fact]
    public async Task NTF01_TheVerificationEmailStatesTheConfiguredLifetime()
    {
        var ct = TestContext.Current.CancellationToken;

        (await SignUpAsync("Acme Ltd", Email, DefaultPassword, ct)).EnsureSuccessStatusCode();
        await WaitForVerificationTokenAsync(Email);

        var text = SentEmails.Single(message => message.To == Email).TextBody;
        text.ShouldContain("This link expires in 2 days.");
        text.ShouldNotContain("24 hours");
    }

    [Fact]
    public async Task NTF01_ThePasswordResetEmailStatesTheConfiguredLifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        (await ForgotPasswordAsync(Email, ct)).EnsureSuccessStatusCode();
        await WaitForResetTokenAsync(Email);

        var text = SentEmails.Single(message => message.To == Email && message.Subject == "Reset your password").TextBody;
        text.ShouldContain("This link expires in 30 minutes and can be used once.");
        text.ShouldNotContain("1 hour");
    }

    [Fact]
    public async Task NTF01_TheInvitationEmailStatesTheConfiguredLifetime()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var ownerToken = await TenantTokenAsync((await LoginForTokensAsync(Email, ct)).AccessToken, owner.TenantId, ct);
        var role = (await (await PostAsync(RoleEndpoints.Route, new CreateRoleRequest("Support", []), ownerToken, ct)).Content
            .ReadFromJsonAsync<RoleResponse>(Json, ct))!.Id;

        (await PostAsync(InvitationEndpoints.InviteRoute, new InviteRequest("new@example.com", [role], []), ownerToken, ct)).EnsureSuccessStatusCode();
        await WaitForEmailTokenAsync("new@example.com", "You have been invited to Acme Ltd");

        var text = SentEmails.Single(message => message.To == "new@example.com").TextBody;
        text.ShouldContain("This invitation expires in 3 days.");
        text.ShouldNotContain("7 days");
    }

    [Theory]
    [InlineData(1, "1 minute")]
    [InlineData(30, "30 minutes")]
    [InlineData(60, "1 hour")]
    [InlineData(90, "90 minutes")]
    [InlineData(120, "2 hours")]
    [InlineData(1440, "1 day")]
    [InlineData(2880, "2 days")]
    [InlineData(10080, "7 days")]
    [InlineData(0, "1 minute")]
    public void NTF01_Lifetime_IsWrittenInTheLargestUnitThatDividesItExactly(int minutes, string expected) =>
        EmailLifetime.Describe(TimeSpan.FromMinutes(minutes)).ShouldBe(expected);

    [Fact]
    public void NTF01_Templates_AnEmailQueuedWithoutALifetimeFallsBackToTheDefaultSentence()
    {
        var options = new EmailOptions { PublicBaseUrl = "https://app.test" };
        var data = new Dictionary<string, string> { ["token"] = "t", ["tenantName"] = "Acme Ltd" };

        EmailTemplates.Render(AuthService.Infrastructure.Persistence.Entities.EmailTemplate.Verification, "a@b.co", data, options)
            .TextBody.ShouldContain("This link expires in 24 hours.");
        EmailTemplates.Render(AuthService.Infrastructure.Persistence.Entities.EmailTemplate.PasswordReset, "a@b.co", data, options)
            .TextBody.ShouldContain("This link expires in 1 hour and can be used once.");
        EmailTemplates.Render(AuthService.Infrastructure.Persistence.Entities.EmailTemplate.Invitation, "a@b.co", data, options)
            .TextBody.ShouldContain("This invitation expires in 7 days.");
    }
}
