using System.Collections.Concurrent;

namespace AuthService.Web.Security;

/// <summary>Keeps sessions in the memory of this process. A restart signs everyone out.</summary>
public sealed class MemorySessionStore(TimeProvider time) : ISessionStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public Task AddAsync(UserSession session, CancellationToken cancellationToken)
    {
        // Sessions nobody came back to would pile up, so every new one clears the ones that have run out.
        var now = time.GetUtcNow();
        foreach (var (id, entry) in _entries)
        {
            if (entry.Session.ExpiresAt <= now)
            {
                _entries.TryRemove(id, out _);
            }
        }

        _entries[session.Id] = new Entry(session);
        return Task.CompletedTask;
    }

    public Task<UserSession?> FindAsync(string id, CancellationToken cancellationToken) =>
        Task.FromResult(_entries.TryGetValue(id, out var entry) ? entry.Session : null);

    public Task SaveAsync(UserSession session, CancellationToken cancellationToken)
    {
        if (_entries.TryGetValue(session.Id, out var entry))
        {
            entry.Session = session;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string id, CancellationToken cancellationToken)
    {
        _entries.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public async Task<IAsyncDisposable> LockAsync(string id, CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            return NoLock.Instance;
        }

        await entry.Gate.WaitAsync(cancellationToken);
        return new Held(entry.Gate);
    }

    private sealed class Entry(UserSession session)
    {
        public volatile UserSession Session = session;

        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private sealed class Held(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoLock : IAsyncDisposable
    {
        public static readonly NoLock Instance = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
