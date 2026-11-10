using System.Collections.Concurrent;

namespace AuthService.Entitlements;

/// <summary>
/// Where the product keeps each organisation's newest entitlements. Replace the in-memory one with a store in the product's own database
/// for more than one instance or to survive a restart: register your <see cref="IEntitlementStore"/> before <c>AddAuthServiceEntitlements</c>.
/// </summary>
public interface IEntitlementStore
{
    /// <summary>The organisation's snapshot, or null when none is kept.</summary>
    Task<EntitlementSnapshot?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps <paramref name="snapshot"/> only when its version is higher than the one kept (or none is kept), and says whether it did. An
    /// equal version is a duplicate and a lower one is old news; never let either replace what is kept.
    /// </summary>
    Task<bool> ApplyAsync(EntitlementSnapshot snapshot, CancellationToken cancellationToken = default);

    /// <summary>Records a new subscription status without touching the limits (a suspension or cancellation does not change the version).</summary>
    Task SetStatusAsync(Guid tenantId, string status, CancellationToken cancellationToken = default);
}

/// <summary>Keeps snapshots in memory, for one instance of the product. Gone on restart: the catch-up read fills it again.</summary>
public sealed class InMemoryEntitlementStore : IEntitlementStore
{
    private readonly ConcurrentDictionary<Guid, EntitlementSnapshot> _snapshots = new();

    /// <inheritdoc />
    public Task<EntitlementSnapshot?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_snapshots.TryGetValue(tenantId, out var snapshot) ? snapshot : null);

    /// <inheritdoc />
    public Task<bool> ApplyAsync(EntitlementSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        while (true)
        {
            if (!_snapshots.TryGetValue(snapshot.TenantId, out var kept))
            {
                if (_snapshots.TryAdd(snapshot.TenantId, snapshot))
                {
                    return Task.FromResult(true);
                }

                continue;
            }

            if (snapshot.Version <= kept.Version)
            {
                return Task.FromResult(false);
            }

            // An event carries no status; keep the one already known.
            var next = snapshot.Status is null ? snapshot with { Status = kept.Status } : snapshot;
            if (_snapshots.TryUpdate(snapshot.TenantId, next, kept))
            {
                return Task.FromResult(true);
            }
        }
    }

    /// <inheritdoc />
    public Task SetStatusAsync(Guid tenantId, string status, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        while (_snapshots.TryGetValue(tenantId, out var kept) && !_snapshots.TryUpdate(tenantId, kept with { Status = status }, kept))
        {
        }

        return Task.CompletedTask;
    }
}
