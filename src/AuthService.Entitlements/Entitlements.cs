using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthService.Entitlements;

/// <summary>Answers "what may this organisation do?" from the store, reading from the Auth Service only when the store has nothing yet.</summary>
public interface IEntitlements
{
    /// <summary>
    /// The organisation's limit for <paramref name="key"/>. A limit missing from its snapshot (added after the snapshot was taken) is the
    /// limit's default, never unlimited.
    /// </summary>
    /// <exception cref="KeyNotFoundException"><paramref name="key"/> is not a limit of the product.</exception>
    /// <exception cref="InvalidOperationException">The organisation never subscribed to the product.</exception>
    Task<LimitValue> GetLimitAsync(Guid tenantId, string key, CancellationToken cancellationToken = default);

    /// <summary>The organisation's snapshot from the store, read once from the Auth Service when the store has none; null when it never subscribed.</summary>
    Task<EntitlementSnapshot?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default);
}

internal sealed class Entitlements(
    IEntitlementStore store, IServiceProvider services, IOptions<AuthServiceEntitlementsOptions> options, TimeProvider time) : IEntitlements
{
    // Resolved only when the store has nothing: a product whose store is filled by webhooks and its own catch-up never needs the reads here.
    private IEntitlementsClient Client => services.GetRequiredService<IEntitlementsClient>();

    private readonly SemaphoreSlim _limitsGate = new(1, 1);
    private IReadOnlyDictionary<string, LimitDefinition>? _limits;
    private DateTimeOffset _limitsReadAt;

    public async Task<EntitlementSnapshot?> GetAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        if (await store.GetAsync(tenantId, cancellationToken) is { } kept)
        {
            return kept;
        }

        var read = await Client.GetTenantAsync(tenantId, cancellationToken);
        if (read is null)
        {
            return null;
        }

        await store.ApplyAsync(read, cancellationToken);
        return await store.GetAsync(tenantId, cancellationToken) ?? read;
    }

    public async Task<LimitValue> GetLimitAsync(Guid tenantId, string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var snapshot = await GetAsync(tenantId, cancellationToken)
            ?? throw new InvalidOperationException($"The organisation {tenantId} is not subscribed to this product.");
        if (snapshot.Get(key) is { } value)
        {
            return value;
        }

        // A limit added after this snapshot: its default applies until the organisation's next change arrives.
        var limits = await LimitsAsync(refresh: false, cancellationToken);
        if (!limits.ContainsKey(key))
        {
            limits = await LimitsAsync(refresh: true, cancellationToken);
        }

        return limits.TryGetValue(key, out var limit)
            ? limit.Default
            : throw new KeyNotFoundException($"'{key}' is not a limit of this product.");
    }

    private async Task<IReadOnlyDictionary<string, LimitDefinition>> LimitsAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _limits is { } cached && time.GetUtcNow() - _limitsReadAt < options.Value.LimitCacheDuration)
        {
            return cached;
        }

        await _limitsGate.WaitAsync(cancellationToken);
        try
        {
            var list = await Client.GetLimitsAsync(cancellationToken);
            _limits = list.ToDictionary(limit => limit.Key, StringComparer.Ordinal);
            _limitsReadAt = time.GetUtcNow();
            return _limits;
        }
        finally
        {
            _limitsGate.Release();
        }
    }
}
