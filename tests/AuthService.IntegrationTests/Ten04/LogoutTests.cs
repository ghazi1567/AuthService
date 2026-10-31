using System.Net;
using System.Net.Http.Json;
using AuthService.Api.Features.Logout;
using AuthService.Api.Features.Refresh;
using AuthService.IntegrationTests.Infrastructure;

namespace AuthService.IntegrationTests.Ten04;

/// <summary>
/// TEN-04 (AUTH-11) Logout, through the real <c>POST /api/auth/logout</c> endpoint.
/// Test method names start with their Test Case ID (AUTH_TC26..AUTH_TC28) per the workbook convention.
/// </summary>
public sealed class LogoutTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    [Fact]
    public async Task AUTH_TC26_LogoutThenRefreshWithTheSameTokenIsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        var logout = await LogoutAsync(login.RefreshToken, ct);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var refresh = await RefreshAsync(login.RefreshToken, ct);
        refresh.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ReadProblemAsync(refresh, ct)).Code.ShouldBe("invalid_refresh_token");
    }

    [Fact(Skip = "Enabled once TOK-01 (token exchange) exists and issues product access tokens.")]
    public Task AUTH_TC27_ProductAccessTokenIssuedBeforeLogoutStaysValidUntilItExpires() => Task.CompletedTask;

    [Fact]
    public async Task AUTH_TC28_LogoutOnOneDeviceLeavesTheOtherDevicesSessionWorking()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var deviceA = await LoginForTokensAsync("owner@acme.com", ct);
        var deviceB = await LoginForTokensAsync("owner@acme.com", ct);

        (await LogoutAsync(deviceA.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RefreshAsync(deviceB.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RefreshAsync(deviceA.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // The tests below have no workbook Test Case ID yet: the spec's acceptance criteria cover them.

    [Fact]
    public async Task TEN04_LogoutWithAnUnknownTokenGetsTheSameResponse()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await LogoutAsync("not-a-token-we-issued", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, "the endpoint must not reveal which tokens exist");
    }

    [Fact]
    public async Task TEN04_LogoutCanBeRepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);

        (await LogoutAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await LogoutAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task TEN04_LogoutWithAnOlderTokenFromTheSameLoginStillEndsThatSession()
    {
        var ct = TestContext.Current.CancellationToken;
        await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct);
        var login = await LoginForTokensAsync("owner@acme.com", ct);
        var refreshed = await RefreshAsync(login.RefreshToken, ct);
        var current = (await refreshed.Content.ReadFromJsonAsync<RefreshResponse>(Json, ct))!;

        // The client logs out with a token that has already been rotated away.
        (await LogoutAsync(login.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await RefreshAsync(current.RefreshToken, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TEN04_Validation_MissingTokenIsRejectedWith400(string? refreshToken)
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await LogoutAsync(refreshToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private Task<HttpResponseMessage> LogoutAsync(string? refreshToken, CancellationToken ct) =>
        Client.PostAsJsonAsync(LogoutEndpoints.Route, new { refreshToken }, ct);
}
