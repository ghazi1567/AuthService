using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthService.Api.Features.Accounts;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.Signup;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Ten02;

/// <summary>
/// TEN-02 (AUTH-9) Login, through the real <c>POST /api/auth/login</c> endpoint.
/// Test method names start with their Test Case ID (AUTH_TC5..AUTH_TC8, AUTH_TC21) per the workbook convention.
/// </summary>
public sealed class LoginTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Password = DefaultPassword;

    [Fact]
    public async Task AUTH_TC5_LoginWithCorrectCredentialsReturnsTokensAndMemberships()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        var response = await LoginAsync("owner@acme.com", Password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("a response carrying tokens must never be cached");
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;

        login.TokenType.ShouldBe("Bearer");
        login.ExpiresIn.ShouldBe(15 * 60);
        login.RefreshToken.ShouldNotBeNullOrEmpty();
        login.Memberships.ShouldBe([new MembershipSummary(account.TenantId, "Acme Ltd", MembershipStatus.Active)]);

        // The access token is genuinely signed by the service, and names the user only.
        await using var scope = Harness.CreateScope();
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(
            login.AccessToken,
            new TokenValidationParameters
            {
                ValidIssuer = "AuthService",
                ValidateAudience = false,
                IssuerSigningKey = scope.ServiceProvider.GetRequiredService<SigningKeyProvider>().Key,
            });
        validation.IsValid.ShouldBeTrue(validation.Exception?.Message);

        var token = new JsonWebToken(login.AccessToken);
        token.Subject.ShouldBe(account.UserId.ToString());
        token.Audiences.ShouldBeEmpty("an identity token has no audience");
        token.Claims.Select(claim => claim.Type).ShouldBe(
            ["sub", "jti", "iss", "iat", "nbf", "exp"],
            ignoreOrder: true,
            "no tid, aud or permission claims: those belong to product tokens (TOK-01)");
    }

    [Fact]
    public async Task AUTH_TC6_LoginWithWrongPasswordIsRejectedWithGeneric401()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        var response = await LoginAsync("owner@acme.com", "not the password", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_credentials");
    }

    [Fact]
    public async Task AUTH_TC7_LoginWithUnknownEmailGetsTheSameResponseAsWrongPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        var wrongPassword = await LoginAsync("owner@acme.com", "not the password", ct);
        var unknownEmail = await LoginAsync("nobody@acme.com", Password, ct);

        unknownEmail.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(unknownEmail, ct)).ShouldBe(await ReadProblemAsync(wrongPassword, ct),
            "the two responses must be indistinguishable, so an email's existence is never revealed");
    }

    [Fact]
    public async Task AUTH_TC8_LoginWhenEveryMembershipIsDisabledIsRejectedWith403AndADistinctCode()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await SetMembershipStatusesAsync("owner@acme.com", MembershipStatus.Disabled, ct);

        var response = await LoginAsync("owner@acme.com", Password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("account_disabled");

        // Without the right password the account's state stays hidden.
        (await LoginAsync("owner@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AUTH_TC21_LoginAsUserInTwoTenantsListsBothMembershipsAndHasNoTenantClaim()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var beta = await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);
        await AddMembershipAsync(userId: acme.UserId, tenantId: beta.TenantId, MembershipStatus.Active, ct);

        var response = await LoginAsync("owner@acme.com", Password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;
        login.Memberships.ShouldBe(
        [
            new MembershipSummary(acme.TenantId, "Acme Ltd", MembershipStatus.Active),
            new MembershipSummary(beta.TenantId, "Beta Co", MembershipStatus.Active),
        ]);
        new JsonWebToken(login.AccessToken).Claims.Select(claim => claim.Type).ShouldNotContain("tid");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria and v1.0 additions cover them.

    [Fact]
    public async Task TEN02_OneDisabledMembershipDoesNotBlockLogin()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var beta = await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);
        await AddMembershipAsync(userId: acme.UserId, tenantId: beta.TenantId, MembershipStatus.Disabled, ct);

        var response = await LoginAsync("owner@acme.com", Password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;
        login.Memberships.Select(membership => membership.Status)
            .ShouldBe([MembershipStatus.Active, MembershipStatus.Disabled]);
    }

    [Fact]
    public async Task TEN02_UnverifiedEmailIsRejectedWith403AndADistinctCode()
    {
        var ct = TestContext.Current.CancellationToken;
        (await SignUpAsync("Acme Ltd", "owner@acme.com", Password, ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await LoginAsync("owner@acme.com", Password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("email_not_verified");

        // Without the right password the account's state stays hidden.
        (await LoginAsync("owner@acme.com", "not the password", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN02_UserWithoutAPasswordGetsTheSameResponseAsBadCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        await WithDbAsync(
            async db =>
            {
                db.Users.Add(new User
                {
                    Id = Guid.CreateVersion7(),
                    Email = "invited@acme.com",
                    PasswordHash = null,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync(ct);
            });

        var invited = await LoginAsync("invited@acme.com", Password, ct);
        var wrongPassword = await LoginAsync("owner@acme.com", "not the password", ct);

        invited.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(invited, ct)).ShouldBe(await ReadProblemAsync(wrongPassword, ct));
    }

    [Fact]
    public async Task TEN02_EmailIsMatchedIgnoringCaseAndSurroundingWhitespace()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        (await LoginAsync(" Owner@ACME.com ", Password, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN02_RefreshTokenIsStoredOnlyAsAHash()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);

        var response = await LoginAsync("owner@acme.com", Password, ct);
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;

        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var stored = (await db.RefreshTokens.ToListAsync(ct)).ShouldHaveSingleItem();

        stored.UserId.ShouldBe(account.UserId);
        stored.TokenHash.ShouldBe(IdentityTokenService.Hash(login.RefreshToken));
        stored.TokenHash.ShouldNotBe(login.RefreshToken);
        stored.RevokedAt.ShouldBeNull();
        (stored.ExpiresAt - stored.CreatedAt).ShouldBe(TimeSpan.FromDays(30));
    }

    [Theory]
    [InlineData(null, Password)]
    [InlineData("   ", Password)]
    [InlineData("owner@acme.com", null)]
    [InlineData("owner@acme.com", "")]
    public async Task TEN02_Validation_MissingFieldsAreRejectedWith400(string? email, string? password)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await LoginAsync(email, password, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

}
