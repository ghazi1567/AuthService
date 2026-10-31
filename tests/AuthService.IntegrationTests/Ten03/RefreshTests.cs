using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Login;
using AuthService.Api.Features.Refresh;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.IntegrationTests.Ten03;

/// <summary>
/// TEN-03 (AUTH-10) Refresh Identity Session, through the real <c>POST /api/auth/refresh</c> endpoint.
/// Test method names start with their Test Case ID (AUTH_TC9..AUTH_TC11) per the workbook convention.
/// </summary>
public sealed class RefreshTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task AUTH_TC9_RefreshWithValidTokenReturnsNewAccessTokenAndRotatedRefreshToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var account = await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        var response = await RefreshAsync(login.RefreshToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl?.NoStore.ShouldBeTrue("a response carrying tokens must never be cached");
        var refreshed = (await response.Content.ReadFromJsonAsync<RefreshResponse>(Json, ct))!;

        refreshed.TokenType.ShouldBe("Bearer");
        refreshed.ExpiresIn.ShouldBe(15 * 60);
        refreshed.AccessToken.ShouldNotBe(login.AccessToken);
        refreshed.RefreshToken.ShouldNotBe(login.RefreshToken, "rotation must hand out a new refresh token");

        // The new access token is genuinely signed by the service and names the same user.
        await using var scope = Harness.CreateScope();
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(
            refreshed.AccessToken,
            new TokenValidationParameters
            {
                ValidIssuer = "AuthService",
                ValidateAudience = false,
                IssuerSigningKey = scope.ServiceProvider.GetRequiredService<SigningKeyProvider>().Key,
            });
        validation.IsValid.ShouldBeTrue(validation.Exception?.Message);
        new JsonWebToken(refreshed.AccessToken).Subject.ShouldBe(account.UserId.ToString());

        // The old token is spent, the new one continues the same family, and the chain keeps working.
        var tokens = await LoadTokensAsync(ct);
        tokens.Count.ShouldBe(2);
        tokens.Select(token => token.FamilyId).Distinct().ShouldHaveSingleItem();
        tokens.Single(token => token.TokenHash == IdentityTokenService.Hash(login.RefreshToken)).RevokedAt.ShouldNotBeNull();
        tokens.Single(token => token.TokenHash == IdentityTokenService.Hash(refreshed.RefreshToken)).RevokedAt.ShouldBeNull();

        (await RefreshAsync(refreshed.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AUTH_TC10_RefreshWithExpiredTokenIsRejectedWith401()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);
        await WithDbAsync(db => db.RefreshTokens.ExecuteUpdateAsync(
            setters => setters.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), ct));

        var response = await RefreshAsync(login.RefreshToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("invalid_refresh_token");
    }

    [Fact]
    public async Task AUTH_TC11_ReusingARefreshTokenIsRejectedAndRevokesTheWholeFamily()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        var first = await RefreshAsync(login.RefreshToken, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var next = (await first.Content.ReadFromJsonAsync<RefreshResponse>(Json, ct))!;

        var replay = await RefreshAsync(login.RefreshToken, ct);

        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(replay, ct)).Code.ShouldBe("invalid_refresh_token");

        // The replay is treated as theft: the newest token in the family stops working too.
        (await RefreshAsync(next.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await LoadTokensAsync(ct)).ShouldAllBe(token => token.RevokedAt != null, "every token in the family must be revoked");
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN03_UnknownExpiredAndReplayedTokensAllGetTheSameResponse()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var spent = await LoginForTokensAsync("owner@acme.com", ct);
        (await RefreshAsync(spent.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var expired = await LoginForTokensAsync("owner@acme.com", ct);
        await WithDbAsync(db => db.RefreshTokens
            .Where(token => token.TokenHash == IdentityTokenService.Hash(expired.RefreshToken))
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), ct));

        var unknownResponse = await RefreshAsync("not-a-token-we-issued", ct);
        var expiredResponse = await RefreshAsync(expired.RefreshToken, ct);
        var replayedResponse = await RefreshAsync(spent.RefreshToken, ct);

        unknownResponse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var unknown = await ReadProblemAsync(unknownResponse, ct);
        (await ReadProblemAsync(expiredResponse, ct)).ShouldBe(unknown, "the response must not reveal why a token was refused");
        (await ReadProblemAsync(replayedResponse, ct)).ShouldBe(unknown);
    }

    [Fact]
    public async Task TEN03_ReplayOnOneLoginDoesNotEndTheSessionOnAnotherDevice()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var laptop = await LoginForTokensAsync("owner@acme.com", ct);
        var phone = await LoginForTokensAsync("owner@acme.com", ct);

        (await RefreshAsync(laptop.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(laptop.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await RefreshAsync(phone.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TEN03_AccountWithEveryMembershipDisabledCannotRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);
        await SetMembershipStatusesAsync("owner@acme.com", MembershipStatus.Disabled, ct);

        var response = await RefreshAsync(login.RefreshToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ReadProblemAsync(response, ct)).Code.ShouldBe("account_disabled");
    }

    [Fact]
    public async Task TEN03_Concurrency_SimultaneousRefreshesWithTheSameTokenSucceedOnlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        var responses = await Task.WhenAll(
            RefreshAsync(login.RefreshToken, ct),
            RefreshAsync(login.RefreshToken, ct),
            RefreshAsync(login.RefreshToken, ct));

        responses.Count(response => response.StatusCode == HttpStatusCode.OK).ShouldBe(1, "a refresh token may be used once");
        responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized).ShouldBe(2);
        (await LoadTokensAsync(ct)).ShouldAllBe(token => token.RevokedAt != null, "the replays end the whole family");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN03_Validation_MissingTokenIsRejectedWith400(string? refreshToken)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await RefreshAsync(refreshToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> RefreshAsync(string? refreshToken, CancellationToken ct) =>
        Client.PostAsJsonAsync(RefreshEndpoints.Route, new { refreshToken }, ct);

    private async Task<LoginResponse> LoginForTokensAsync(string email, CancellationToken ct)
    {
        var response = await LoginAsync(email, DefaultPassword, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Json, ct))!;
    }

    private async Task<List<RefreshToken>> LoadTokensAsync(CancellationToken ct)
    {
        await using var scope = Harness.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.RefreshTokens.AsNoTracking().ToListAsync(ct);
    }
}
