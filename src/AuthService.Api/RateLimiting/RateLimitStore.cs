using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace AuthService.Api.RateLimiting;

/// <summary>What one use of a limited endpoint did to its counter: how many uses there have been in the window, and how long it has left.</summary>
public readonly record struct RateLimitHit(long Hits, TimeSpan WindowRemaining);

/// <summary>
/// Counts uses of the limited endpoints in fixed windows (PLT-04). A window starts at the first use and ends <c>window</c> later,
/// then the count starts again. Which store is used is the <c>Auth:RateLimit:Store</c> setting: <see cref="PostgresRateLimitStore"/>
/// shares the counts between every instance of the service; <see cref="InMemoryRateLimitStore"/> keeps them in the one process.
/// </summary>
public interface IRateLimitStore
{
    /// <summary>Counts one use of <paramref name="key"/> and says where its window stands. Throws if the store cannot be reached.</summary>
    Task<RateLimitHit> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken);

    /// <summary>Removes the counters whose windows are over, so the store does not grow without bound. Returns how many went.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The counters in the service's own PostgreSQL database. One statement does everything, so it is atomic: simultaneous uses, from
/// any instance, are counted one by one. The time is the database's, so instances whose clocks differ still agree on a window. The
/// key is hashed before it is stored, so no address or id is kept.
/// </summary>
public sealed class PostgresRateLimitStore(IServiceScopeFactory scopeFactory) : IRateLimitStore
{
    // A window that is over is replaced in the same statement that counts the next use, so the count restarts without a separate reset.
    private const string HitSql = """
        INSERT INTO rate_limit_counters AS counter (key_hash, hits, expires_at)
        VALUES (@key, 1, clock_timestamp() + make_interval(secs => @seconds))
        ON CONFLICT (key_hash) DO UPDATE SET
            hits = CASE WHEN counter.expires_at <= clock_timestamp() THEN 1 ELSE counter.hits + 1 END,
            expires_at = CASE WHEN counter.expires_at <= clock_timestamp() THEN clock_timestamp() + make_interval(secs => @seconds) ELSE counter.expires_at END
        RETURNING counter.hits, EXTRACT(EPOCH FROM (counter.expires_at - clock_timestamp()))::float8
        """;

    // Kept for a minute after the window, so a counter that was just read is never deleted under the reader.
    private const string PurgeSql = "DELETE FROM rate_limit_counters WHERE expires_at < clock_timestamp() - interval '1 minute'";

    public async Task<RateLimitHit> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var connection = (NpgsqlConnection)scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(HitSql, connection);
            command.Parameters.Add(new NpgsqlParameter<string>("key", HashKey(key)) { NpgsqlDbType = NpgsqlDbType.Varchar });
            command.Parameters.Add(new NpgsqlParameter<double>("seconds", window.TotalSeconds));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new RateLimitHit(reader.GetInt64(0), TimeSpan.FromSeconds(Math.Max(0, reader.GetDouble(1))));
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.ExecuteSqlRawAsync(PurgeSql, cancellationToken);
    }

    /// <summary>SHA-256, base64url. The key names a policy and a caller, such as an IP address; only the hash is kept.</summary>
    internal static string HashKey(string key) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(key)))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// The counters in this process only. Right for development, tests and a single instance; with several instances each counts on its
/// own, so a limit is the setting times the number of instances, and a restart clears the counts. Uses the machine's steady clock, so
/// it does not follow a test clock.
/// </summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public Task<RateLimitHit> HitAsync(string key, TimeSpan window, CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var counter = _counters.GetOrAdd(key, _ => new Counter());

        lock (counter)
        {
            if (counter.EndsAt <= now)
            {
                counter.Hits = 0;
                counter.EndsAt = now + (long)(window.TotalSeconds * Stopwatch.Frequency);
            }

            counter.Hits++;
            return Task.FromResult(new RateLimitHit(counter.Hits, Stopwatch.GetElapsedTime(now, counter.EndsAt)));
        }
    }

    public Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var removed = 0;
        foreach (var (key, counter) in _counters)
        {
            bool expired;
            lock (counter)
            {
                expired = counter.EndsAt <= now;
            }

            if (expired && _counters.TryRemove(key, out var gone))
            {
                // Someone may have started a new window between the check and the removal; put that count back.
                lock (gone)
                {
                    if (gone.EndsAt > now)
                    {
                        _counters.TryAdd(key, gone);
                        continue;
                    }
                }

                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    private sealed class Counter
    {
        public long Hits { get; set; }

        public long EndsAt { get; set; }
    }
}
