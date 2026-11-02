using System.Security.Cryptography;
using AuthService.Web.Api;
using Microsoft.Extensions.Options;

namespace AuthService.Web.Security;

/// <summary>Whether a session can still be used after it was checked.</summary>
public enum SessionState
{
    /// <summary>The session is alive (its tokens are fresh, or could not be renewed right now and will be tried again).</summary>
    Active,

    /// <summary>The session is gone: it ran out, was signed out, or the API refused to renew it. The person must sign in again.</summary>
    Ended,
}

/// <summary>
/// The signed-in session: starts it when the API accepts a sign-in, renews the access token before it runs out, and ends it on
/// sign-out or when the API will not renew it. The tokens stay in the <see cref="ISessionStore"/>; nothing here writes one to a log.
/// </summary>
public sealed class SessionService(
    ISessionStore store,
    AuthApiClient api,
    TimeProvider time,
    IOptions<SessionOptions> options,
    ILogger<SessionService> logger)
{
    private readonly SessionOptions _options = options.Value;

    /// <summary>Starts a session for a sign-in the API accepted, or returns null when its token does not name a person.</summary>
    public async Task<UserSession?> StartAsync(LoginResponse login, string email, bool remember, CancellationToken cancellationToken)
    {
        if (AccessTokenClaims.ReadUserId(login.AccessToken) is not { } userId)
        {
            return null;
        }

        var now = time.GetUtcNow();
        var session = new UserSession(
            NewId(),
            userId,
            email,
            login.AccessToken,
            now.AddSeconds(login.ExpiresIn),
            login.RefreshToken,
            remember ? now.AddDays(_options.RememberedDays) : now.AddHours(_options.DefaultHours));
        await store.AddAsync(session, cancellationToken);
        return session;
    }

    /// <summary>
    /// Checks a session and renews its access token when it is about to run out. Many requests can ask at once (a page and its
    /// assets), so one renews and the rest use the result: a refresh token works once, and using one twice would end the session.
    /// </summary>
    public async Task<SessionState> EnsureFreshAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await store.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionState.Ended;
        }

        if (session.ExpiresAt <= time.GetUtcNow())
        {
            await store.RemoveAsync(sessionId, cancellationToken);
            return SessionState.Ended;
        }

        if (!NeedsRefresh(session))
        {
            return SessionState.Active;
        }

        await using var held = await store.LockAsync(sessionId, cancellationToken);

        // Someone else may have renewed it, or ended it, while this request waited.
        session = await store.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return SessionState.Ended;
        }

        if (!NeedsRefresh(session))
        {
            return SessionState.Active;
        }

        var result = await api.PostAsync<RefreshRequest, RefreshResponse>(
            "/api/auth/refresh", new RefreshRequest(session.RefreshToken), cancellationToken);

        if (result.IsSuccess)
        {
            var renewed = result.Value!;
            await store.SaveAsync(
                session with
                {
                    AccessToken = renewed.AccessToken,
                    AccessTokenExpiresAt = time.GetUtcNow().AddSeconds(renewed.ExpiresIn),
                    RefreshToken = renewed.RefreshToken,
                },
                cancellationToken);
            return SessionState.Active;
        }

        if (result.Problem!.Status is StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden)
        {
            // Expired, revoked, reused or the account was disabled: this session is over.
            // The session id is the cookie secret, so logs name the person instead.
            logger.LogInformation("A session of user {UserId} ended: the API would not renew it ({Code}).", session.UserId, result.Problem.Code);
            await store.RemoveAsync(sessionId, cancellationToken);
            return SessionState.Ended;
        }

        // The API could not be reached or had a problem. That is not the person's fault, so they stay signed in and it is tried again.
        logger.LogWarning("Could not renew a session of user {UserId} now ({Code}); it will be tried again.", session.UserId, result.Problem.Code);
        return SessionState.Active;
    }

    /// <summary>The access token to call the API as this person, renewed first when needed. Null when the session has ended.</summary>
    public async Task<string?> GetAccessTokenAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (await EnsureFreshAsync(sessionId, cancellationToken) == SessionState.Ended)
        {
            return null;
        }

        return (await store.FindAsync(sessionId, cancellationToken))?.AccessToken;
    }

    /// <summary>Ends a session here, then tells the API so its refresh token stops working. The session is gone even if the API is down.</summary>
    public async Task EndAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = await store.FindAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return;
        }

        await store.RemoveAsync(sessionId, cancellationToken);

        var result = await api.PostNoContentAsync("/api/auth/logout", new LogoutRequest(session.RefreshToken), cancellationToken);
        if (!result.IsSuccess)
        {
            logger.LogWarning("A session of user {UserId} ended here, but the API could not be told ({Code}).", session.UserId, result.Problem!.Code);
        }
    }

    private bool NeedsRefresh(UserSession session) =>
        session.AccessTokenExpiresAt - time.GetUtcNow() <= TimeSpan.FromSeconds(_options.RefreshBeforeSeconds);

    private static string NewId() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
