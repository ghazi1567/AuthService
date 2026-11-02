using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AuthService.Web.Security;

/// <summary>
/// Holds a secret that may be shown exactly once, such as a new API key. The page that created it redirects to a page that takes it
/// back out; the redirect carries only a random reference, never the secret, and a reload finds nothing. A secret is only handed to the
/// session that made it, expires after two minutes, and is never logged.
/// </summary>
public sealed class OneTimeSecrets(TimeProvider time)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Keeps <paramref name="secret"/> for the session and returns the reference to take it back with.</summary>
    public string Put(string sessionId, string secret)
    {
        var now = time.GetUtcNow();
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }

        var reference = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _entries[reference] = new Entry(sessionId, secret, now + Lifetime);
        return reference;
    }

    /// <summary>The secret, once. Null when there is none, it ran out, it was already taken, or it belongs to another session.</summary>
    public string? Take(string sessionId, string reference)
    {
        if (!_entries.TryGetValue(reference, out var entry) || entry.SessionId != sessionId)
        {
            return null;
        }

        _entries.TryRemove(reference, out _);
        return entry.ExpiresAt > time.GetUtcNow() ? entry.Secret : null;
    }

    private sealed record Entry(string SessionId, string Secret, DateTimeOffset ExpiresAt)
    {
        public override string ToString() => "OneTimeSecret";
    }
}
