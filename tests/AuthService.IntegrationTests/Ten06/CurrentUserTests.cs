using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using AuthService.Api.Features.Accounts;
using AuthService.Api.Features.CurrentUser;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Ten06;

/// <summary>
/// TEN-06 (AUTH-13) Get Current User Profile, through the real <c>GET /api/auth/users/me</c> endpoint with the access
/// token a real login returns. This is also the first endpoint behind bearer authentication, so it carries the tests of
/// what that authentication accepts and refuses.
/// Test method names start with their Test Case ID (AUTH_TC29..AUTH_TC32) per the workbook convention.
/// </summary>
public sealed class CurrentUserTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private const string Email = "owner@acme.com";

    [Fact]
    public async Task AUTH_TC29_CurrentUserForAPersonWithTwoMembershipsReturnsTheProfileAndBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var acme = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var beta = await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);
        await AddMembershipAsync(userId: acme.UserId, tenantId: beta.TenantId, MembershipStatus.Active, ct);
        var login = await LoginForTokensAsync(Email, ct);

        var response = await GetAsync(CurrentUserEndpoints.Route, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = (await response.Content.ReadFromJsonAsync<CurrentUserResponse>(Json, ct))!;
        profile.Id.ShouldBe(acme.UserId);
        profile.Email.ShouldBe(Email);
        profile.DisplayName.ShouldBeNull();
        profile.EmailVerified.ShouldBeTrue();
        profile.Memberships.ShouldBe(
        [
            new MembershipSummary(acme.TenantId, "Acme Ltd", MembershipStatus.Active),
            new MembershipSummary(beta.TenantId, "Beta Co", MembershipStatus.Active),
        ]);
    }

    [Fact]
    public async Task AUTH_TC30_CurrentUserResponseContainsNoCredentialFields()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var login = await LoginForTokensAsync(Email, ct);

        var response = await GetAsync(CurrentUserEndpoints.Route, login.AccessToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(raw);

        // Exactly these fields, nothing more: there is nowhere for a hash, a token or a secret to hide.
        document.RootElement.EnumerateObject().Select(property => property.Name).ShouldBe(
            ["id", "email", "displayName", "emailVerified", "createdAt", "memberships"],
            ignoreOrder: true);
        foreach (var forbidden in new[] { "password", "hash", "token", "secret" })
        {
            raw.Contains(forbidden, StringComparison.OrdinalIgnoreCase).ShouldBeFalse($"the response must not mention '{forbidden}'");
        }

        response.Headers.CacheControl?.NoStore.ShouldBeTrue("personal data must not be cached");
    }

    [Fact]
    public async Task AUTH_TC31_AnotherUsersIdInTheQueryStringIsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var me = await SignUpVerifiedAsync("Acme Ltd", Email, ct);
        var other = await SignUpVerifiedAsync("Beta Co", "boss@beta.com", ct);
        var login = await LoginForTokensAsync(Email, ct);

        foreach (var parameter in new[] { "id", "userId", "sub", "email" })
        {
            var value = parameter == "email" ? "boss@beta.com" : other.UserId.ToString();
            var response = await GetAsync($"{CurrentUserEndpoints.Route}?{parameter}={value}", login.AccessToken, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadFromJsonAsync<CurrentUserResponse>(Json, ct))!.Id.ShouldBe(me.UserId);
        }
    }

    [Fact]
    public async Task AUTH_TC32_CurrentUserWithNoAuthorizationHeaderIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await GetAsync(CurrentUserEndpoints.Route, bearerToken: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unauthorized");
        response.Headers.WwwAuthenticate.ShouldContain(challenge => challenge.Scheme == "Bearer");
    }

    // The tests below have no workbook Test Case ID yet: they cover what bearer authentication must accept and refuse.

    [Fact]
    public async Task TEN06_AGarbageTokenIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;

        (await GetAsync(CurrentUserEndpoints.Route, "not.a.token", ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN06_ATokenSignedWithAnotherKeyIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        using var foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = CreateToken(account.UserId, new ECDsaSecurityKey(foreignKey), expiresIn: TimeSpan.FromMinutes(5));

        (await GetAsync(CurrentUserEndpoints.Route, forged, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN06_AnExpiredTokenIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        var expired = CreateToken(account.UserId, ServiceSigningKey(), expiresIn: TimeSpan.FromMinutes(-10));

        (await GetAsync(CurrentUserEndpoints.Route, expired, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN06_ATokenFromAnotherIssuerIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        var token = CreateToken(account.UserId, ServiceSigningKey(), expiresIn: TimeSpan.FromMinutes(5), issuer: "SomeoneElse");

        (await GetAsync(CurrentUserEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TEN06_AProductTokenWithAnAudienceIsRefusedWith403()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", Email, ct);

        // Correctly signed and unexpired, but scoped to one product: not an identity token.
        var productToken = CreateToken(account.UserId, ServiceSigningKey(), expiresIn: TimeSpan.FromMinutes(5), audience: "whatsapp-saas");

        (await GetAsync(CurrentUserEndpoints.Route, productToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task TEN06_ATokenForAUserThatNoLongerExistsIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;

        var token = CreateToken(Guid.NewGuid(), ServiceSigningKey(), expiresIn: TimeSpan.FromMinutes(5));

        var response = await GetAsync(CurrentUserEndpoints.Route, token, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("unauthorized");
    }

    [Fact]
    public async Task TEN06_ATokenWithoutASubjectIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = "AuthService",
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(ServiceSigningKey(), SecurityAlgorithms.EcdsaSha256),
        };
        var token = new JsonWebTokenHandler().CreateToken(descriptor);

        (await GetAsync(CurrentUserEndpoints.Route, token, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private SecurityKey ServiceSigningKey()
    {
        using var scope = Harness.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SigningKeyProvider>().Key;
    }

    private static string CreateToken(
        Guid userId,
        SecurityKey key,
        TimeSpan expiresIn,
        string issuer = "AuthService",
        string? audience = null)
    {
        var now = DateTime.UtcNow;

        // Expired tokens need an issue time before their expiry, so shift the whole token back instead of just the expiry.
        var issuedAt = expiresIn < TimeSpan.Zero ? now + expiresIn - TimeSpan.FromMinutes(1) : now;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())]),
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = now + expiresIn,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
