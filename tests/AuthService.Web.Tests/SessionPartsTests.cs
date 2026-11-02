using AuthService.Web.Api;
using AuthService.Web.Security;

namespace AuthService.Web.Tests;

/// <summary>The small parts of the session: the places it may send someone, reading a token, the store, and what printing shows.</summary>
public sealed class SessionPartsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/account", "/account")]
    [InlineData("/members?page=2#top", "/members?page=2#top")]
    [InlineData("account", "/")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example/", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    public void OnlyAPathOnThisSiteIsFollowed(string? returnUrl, string expected) =>
        LocalRedirect.Safe(returnUrl).ShouldBe(expected);

    [Fact]
    public void TheSubjectOfATokenIsReadWithoutCheckingItsSignature()
    {
        var userId = Guid.NewGuid();

        AccessTokenClaims.ReadUserId(TestTokens.Jwt(userId, 1)).ShouldBe(userId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("a.b")]
    [InlineData("a.!!!.c")]
    [InlineData("e30.e30.c")]
    public void ATokenWithoutAPersonInItIsRefused(string token) =>
        AccessTokenClaims.ReadUserId(token).ShouldBeNull();

    [Fact]
    public void PrintingASessionOrARequestNeverShowsASecret()
    {
        var session = new UserSession("the-session-id", Guid.NewGuid(), "anna@acme.com", "access-secret", DateTimeOffset.UtcNow, "refresh-secret", DateTimeOffset.UtcNow);
        var printed = string.Join(
            ' ',
            session.ToString(),
            new LoginRequest("anna@acme.com", "password-secret").ToString(),
            new LoginResponse("access-secret", "Bearer", 900, "refresh-secret").ToString(),
            new RefreshRequest("refresh-secret").ToString(),
            new RefreshResponse("access-secret", "Bearer", 900, "refresh-secret").ToString(),
            new LogoutRequest("refresh-secret").ToString());

        printed.ShouldNotContain("secret");
    }

    [Fact]
    public async Task TheStoreForgetsSessionsThatHaveRunOutWhenANewOneStarts()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var store = new MemorySessionStore(clock);
        await store.AddAsync(Session("old", clock.GetUtcNow().AddHours(1)), Token);

        clock.Advance(TimeSpan.FromHours(2));
        await store.AddAsync(Session("new", clock.GetUtcNow().AddHours(1)), Token);

        (await store.FindAsync("old", Token)).ShouldBeNull();
        (await store.FindAsync("new", Token)).ShouldNotBeNull();
    }

    [Fact]
    public async Task OnlyOneRequestAtATimeCanHoldASession()
    {
        var store = new MemorySessionStore(TimeProvider.System);
        await store.AddAsync(Session("one", DateTimeOffset.UtcNow.AddHours(1)), Token);

        var first = await store.LockAsync("one", Token);
        var second = store.LockAsync("one", Token);
        await Task.Delay(50, Token);
        second.IsCompleted.ShouldBeFalse("the second request waits for the first");

        await first.DisposeAsync();
        await using var held = await second;
    }

    [Fact]
    public async Task SavingASessionThatWasRemovedDoesNotBringItBack()
    {
        var store = new MemorySessionStore(TimeProvider.System);
        var session = Session("gone", DateTimeOffset.UtcNow.AddHours(1));
        await store.AddAsync(session, Token);
        await store.RemoveAsync("gone", Token);

        await store.SaveAsync(session, Token);

        (await store.FindAsync("gone", Token)).ShouldBeNull();
    }

    private static UserSession Session(string id, DateTimeOffset expiresAt) =>
        new(id, Guid.NewGuid(), "anna@acme.com", "access", expiresAt, "refresh", expiresAt);
}
