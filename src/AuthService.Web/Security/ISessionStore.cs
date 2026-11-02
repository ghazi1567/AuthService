namespace AuthService.Web.Security;

/// <summary>
/// Where signed-in sessions live. The in-memory one is the default; running more than one copy of the web app needs a shared one
/// (and shared data-protection keys), see decision 0006.
/// </summary>
public interface ISessionStore
{
    Task AddAsync(UserSession session, CancellationToken cancellationToken);

    /// <summary>The session, or null when there is none with that id.</summary>
    Task<UserSession?> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>Replaces a session that is already stored (after its tokens were renewed). Does nothing if it was removed meanwhile.</summary>
    Task SaveAsync(UserSession session, CancellationToken cancellationToken);

    Task RemoveAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until no one else holds the session, then holds it until disposed. Renewing the tokens needs it: a refresh token works
    /// once, and the API ends the whole session if one is used twice.
    /// </summary>
    Task<IAsyncDisposable> LockAsync(string id, CancellationToken cancellationToken);
}
