using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Entitlements;

/// <summary>A subscription's effective limits at one moment, with what they were computed from.</summary>
public sealed record EntitlementSnapshot(Guid SubscriptionId, Guid TenantId, Guid? PlanId, SortedDictionary<string, EffectiveLimit> Limits);

/// <summary>A tenant's entitlements as the events carry them. <c>Entitlements</c> is the complete set; null is unlimited.</summary>
public sealed record EntitlementPayload(string? PlanKey, string? PlanName, long Version, SortedDictionary<string, long?> Entitlements, IReadOnlyList<string> Overridden);

/// <summary>A subscription whose effective limits changed, after its version was raised.</summary>
public sealed record ChangedEntitlements(Guid SubscriptionId, Guid TenantId, long Version, long ChangeSeq, SortedDictionary<string, EffectiveLimit> Limits);

/// <summary>
/// ENT-03: computes subscriptions' effective limits and raises the version of the ones that changed. A caller takes a snapshot of the
/// subscriptions its change can affect, saves the change inside its transaction, then calls <see cref="CommitAsync"/>, which compares, raises
/// versions, copies <c>seats</c> to <c>maxSeats</c>, writes one <c>EntitlementsChanged</c> event for each (ENT-04) and returns what changed.
/// </summary>
public sealed class EntitlementState(AuthDbContext db, TimeProvider timeProvider, IOptions<WebhookOptions> webhookOptions)
{
    private const string ChangeNumberLockPrefix = "entitlement-change:";

    /// <param name="subscriptionIds">The subscriptions to look at, or null for every subscription of the product.</param>
    public async Task<Dictionary<Guid, EntitlementSnapshot>> SnapshotAsync(
        Guid applicationId, IReadOnlyCollection<Guid>? subscriptionIds, CancellationToken cancellationToken)
    {
        var defaults = await db.LimitDefinitions.AsNoTracking()
            .Where(limit => limit.ApplicationId == applicationId)
            .ToDictionaryAsync(limit => limit.Key, limit => limit.DefaultValue, cancellationToken);

        var query = db.Subscriptions.AsNoTracking().Where(subscription => subscription.ApplicationId == applicationId);
        if (subscriptionIds is not null)
        {
            query = query.Where(subscription => subscriptionIds.Contains(subscription.Id));
        }

        var subscriptions = await query
            .Select(subscription => new { subscription.Id, subscription.TenantId, subscription.PlanId })
            .ToListAsync(cancellationToken);
        var ids = subscriptions.Select(subscription => subscription.Id).ToList();
        var planIds = subscriptions.Where(subscription => subscription.PlanId != null).Select(subscription => subscription.PlanId!.Value).Distinct().ToList();

        var planValues = (await (
                from value in db.PlanValues.AsNoTracking()
                join limit in db.LimitDefinitions.AsNoTracking() on value.LimitDefinitionId equals limit.Id
                where planIds.Contains(value.PlanId)
                select new { value.PlanId, limit.Key, value.Value })
            .ToListAsync(cancellationToken))
            .GroupBy(row => row.PlanId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal));

        var overrides = (await (
                from entry in db.SubscriptionOverrides.AsNoTracking()
                join limit in db.LimitDefinitions.AsNoTracking() on entry.LimitDefinitionId equals limit.Id
                where ids.Contains(entry.SubscriptionId)
                select new { entry.SubscriptionId, limit.Key, entry.Value })
            .ToListAsync(cancellationToken))
            .GroupBy(row => row.SubscriptionId)
            .ToDictionary(group => group.Key, group => group.ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal));

        var none = new Dictionary<string, long?>();
        return subscriptions.ToDictionary(
            subscription => subscription.Id,
            subscription => new EntitlementSnapshot(
                subscription.Id,
                subscription.TenantId,
                subscription.PlanId,
                EffectiveEntitlements.Compute(
                    defaults,
                    subscription.PlanId is { } planId ? planValues.GetValueOrDefault(planId) ?? none : null,
                    overrides.GetValueOrDefault(subscription.Id) ?? none)));
    }

    /// <summary>
    /// Compares each subscription in <paramref name="subscriptionIds"/> with its snapshot in <paramref name="before"/> (one missing from it, such
    /// as a new subscription, counts as changed), raises the version of each that differs with a fresh change number in one statement, and keeps
    /// <c>maxSeats</c> equal to the effective <c>seats</c>. Call it after saving the change, inside the same transaction.
    /// </summary>
    /// <param name="sendEvents">Whether to tell the product with an <c>EntitlementsChanged</c> event for each subscription that changed, written in
    /// the same transaction. Subscribing passes false: its <c>ApplicationSubscribed</c> event already carries the entitlements.</param>
    public async Task<IReadOnlyList<ChangedEntitlements>> CommitAsync(
        Guid applicationId, IReadOnlyCollection<Guid> subscriptionIds, IReadOnlyDictionary<Guid, EntitlementSnapshot> before, CancellationToken cancellationToken,
        bool sendEvents = true)
    {
        if (subscriptionIds.Count == 0)
        {
            return [];
        }

        var after = await SnapshotAsync(applicationId, subscriptionIds, cancellationToken);
        var changed = after.Values
            .Where(now => !before.TryGetValue(now.SubscriptionId, out var old) || EffectiveEntitlements.Differ(old.Limits, now.Limits))
            .ToList();
        if (changed.Count == 0)
        {
            return [];
        }

        var changedIds = changed.Select(snapshot => snapshot.SubscriptionId).OrderBy(id => id).ToArray();

        // Change numbers must become visible in the order they were given out, or a product reading "what changed since 10" could see 11
        // while 10 is still uncommitted, and then never see 10 (ENT-05). So the rows are locked first (never waiting for them while holding the
        // product's lock), then the product's lock is taken and held until this transaction ends: a second change to the same product gets its
        // number only after this one is committed.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM subscriptions WHERE id = ANY({changedIds}) ORDER BY id FOR UPDATE", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({ChangeNumberLockPrefix + applicationId}, 0))", cancellationToken);

        var versions = await db.Database.SqlQuery<VersionRow>($"""
            UPDATE subscriptions
            SET entitlements_version = entitlements_version + 1, entitlements_change_seq = nextval('entitlement_change_seq')
            WHERE id = ANY({changedIds})
            RETURNING id, entitlements_version AS version, entitlements_change_seq AS change_seq
            """).ToListAsync(cancellationToken);
        var byId = versions.ToDictionary(row => row.Id);

        foreach (var snapshot in changed)
        {
            await SyncSeatsAsync(snapshot, cancellationToken);
        }

        var result = changed.Select(snapshot => new ChangedEntitlements(
            snapshot.SubscriptionId, snapshot.TenantId, byId[snapshot.SubscriptionId].Version, byId[snapshot.SubscriptionId].ChangeSeq, snapshot.Limits)).ToList();

        if (sendEvents)
        {
            await EnqueueChangedEventsAsync(applicationId, changed, byId, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// What the events carry about a tenant's entitlements (ENT-04): its plan, the version, the complete effective set (null is unlimited, never a
    /// difference) and which limits are overridden. Shared by <c>EntitlementsChanged</c> and <c>ApplicationSubscribed</c>.
    /// </summary>
    public static EntitlementPayload Payload(EntitlementSnapshot snapshot, long version, string? planKey, string? planName) =>
        new(planKey, planName, version,
            new SortedDictionary<string, long?>(snapshot.Limits.ToDictionary(pair => pair.Key, pair => pair.Value.Value), StringComparer.Ordinal),
            snapshot.Limits.Where(pair => pair.Value.Source == EntitlementSource.Override).Select(pair => pair.Key).ToList());

    /// <summary>The key and name of each plan asked about.</summary>
    public async Task<Dictionary<Guid, (string Key, string Name)>> PlanNamesAsync(IEnumerable<Guid?> planIds, CancellationToken cancellationToken)
    {
        var ids = planIds.Where(id => id != null).Select(id => id!.Value).Distinct().ToList();
        var plans = await db.Plans.AsNoTracking().Where(plan => ids.Contains(plan.Id))
            .Select(plan => new { plan.Id, plan.Key, plan.Name })
            .ToListAsync(cancellationToken);
        return plans.ToDictionary(plan => plan.Id, plan => (plan.Key, plan.Name));
    }

    private async Task EnqueueChangedEventsAsync(
        Guid applicationId, IReadOnlyList<EntitlementSnapshot> changed, Dictionary<Guid, VersionRow> versions, CancellationToken cancellationToken)
    {
        var plans = await PlanNamesAsync(changed.Select(snapshot => snapshot.PlanId), cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var snapshot in changed)
        {
            var plan = snapshot.PlanId is { } planId ? plans[planId] : default((string Key, string Name)?);
            var payload = Payload(snapshot, versions[snapshot.SubscriptionId].Version, plan?.Key, plan?.Name);
            db.WebhookOutbox.Add(WebhookEvents.Create(applicationId, WebhookEvents.EntitlementsChanged, now, webhookOptions.Value.RetryWindowHours, eventId => new
            {
                eventId,
                eventType = WebhookEvents.EntitlementsChanged,
                occurredAt = now,
                schemaVersion = WebhookEvents.SchemaVersion,
                tenantId = snapshot.TenantId,
                applicationId,
                planKey = payload.PlanKey,
                planName = payload.PlanName,
                version = payload.Version,
                entitlements = payload.Entitlements,
                overridden = payload.Overridden,
            }));
        }

        // Saved here, in the caller's transaction, so an event exists if and only if the change it describes does.
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// When the product has a <c>seats</c> limit, makes the subscription's <c>maxSeats</c> (what token exchange and access grants check) equal
    /// to its effective <c>seats</c>, so the two numbers can never disagree. A product without that limit keeps setting <c>maxSeats</c> itself.
    /// </summary>
    public async Task SyncSeatsAsync(EntitlementSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (!snapshot.Limits.TryGetValue(LimitDefinitionValidator.SeatsKey, out var seats))
        {
            return;
        }

        int? maxSeats = seats.Value is null ? null : (int)Math.Min(seats.Value.Value, int.MaxValue);
        await db.Subscriptions.Where(subscription => subscription.Id == snapshot.SubscriptionId && subscription.MaxSeats != maxSeats)
            .ExecuteUpdateAsync(setters => setters.SetProperty(subscription => subscription.MaxSeats, maxSeats), cancellationToken);
    }

    private sealed record VersionRow(Guid Id, long Version, long ChangeSeq);
}
