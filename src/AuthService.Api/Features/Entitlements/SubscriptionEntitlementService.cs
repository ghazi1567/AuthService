using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Subscriptions;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>Body of <c>PUT /api/auth/admin/tenants/{tenantId}/subscriptions/{applicationId}/plan</c>: a plan key, or null for no plan.</summary>
public sealed record AssignPlanRequest(string? PlanKey);

/// <summary>Body of <c>PUT .../overrides/{limitKey}</c>. <c>Value</c> is a whole number or null (unlimited) and must be sent; the reason is required.</summary>
public sealed record SetOverrideRequest(JsonElement Value, string? Reason);

/// <summary>One limit as a tenant has it, with where the value comes from (<c>Default</c>, <c>Plan</c> or <c>Override</c>).</summary>
public sealed record EntitlementItem(string Key, string Label, string? Unit, long? Value, string Source);

/// <summary>An override as the platform admin reads it: the value, why, who set it (a person or the product) and when.</summary>
public sealed record OverrideItem(string LimitKey, long? Value, string Reason, string SetByType, Guid? SetById, DateTimeOffset SetAt);

/// <summary>A tenant's entitlements for one product, as the platform admin reads them.</summary>
public sealed record SubscriptionEntitlementsResponse(
    Guid TenantId,
    Guid ApplicationId,
    string? PlanKey,
    string? PlanName,
    long Version,
    int? MaxSeats,
    IReadOnlyList<EntitlementItem> Limits,
    IReadOnlyList<OverrideItem> Overrides);

public abstract record EntitlementResult
{
    public sealed record Done(SubscriptionEntitlementsResponse Entitlements) : EntitlementResult;

    public sealed record SubscriptionNotFound : EntitlementResult;

    public sealed record PlanNotFound : EntitlementResult;

    public sealed record PlanInactive : EntitlementResult;

    public sealed record LimitUnknown(string Key) : EntitlementResult;

    public sealed record ValueInvalid(string Message) : EntitlementResult;

    public sealed record OverrideNotFound : EntitlementResult;

    public sealed record TooManyOverrides : EntitlementResult;
}

/// <summary>
/// ENT-03: which plan a tenant's subscription is on, and its per-tenant overrides. Every change is written to the entitlement history with who,
/// when, before, after and (for overrides) why; then the subscription's effective limits are recomputed and, if they changed, its version rises.
/// </summary>
public sealed class SubscriptionEntitlementService(
    AuthDbContext db, AuditLog audit, EntitlementHistory history, EntitlementState state, TimeProvider timeProvider)
{
    public const int MaxOverridesPerSubscription = 100;
    public const int MaxReasonLength = 200;

    public async Task<EntitlementResult> AssignPlanAsync(Guid adminId, Guid tenantId, Guid applicationId, string? planKey, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);

        var subscription = await FindAsync(tenantId, applicationId, cancellationToken);
        if (subscription is null)
        {
            return new EntitlementResult.SubscriptionNotFound();
        }

        Plan? plan = null;
        if (planKey is not null)
        {
            plan = await db.Plans.SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == planKey, cancellationToken);
            if (plan is null)
            {
                return new EntitlementResult.PlanNotFound();
            }

            // A deactivated plan stays on the tenants that have it, but nobody new gets it.
            if (!plan.IsActive && subscription.PlanId != plan.Id)
            {
                return new EntitlementResult.PlanInactive();
            }
        }

        if (subscription.PlanId != plan?.Id)
        {
            var before = await state.SnapshotAsync(applicationId, [subscription.Id], cancellationToken);
            var oldPlanKey = subscription.PlanId is { } oldId
                ? await db.Plans.Where(candidate => candidate.Id == oldId).Select(candidate => candidate.Key).SingleAsync(cancellationToken)
                : null;

            subscription.PlanId = plan?.Id;
            subscription.UpdatedAt = timeProvider.GetUtcNow();
            if (plan is not null)
            {
                plan.EverAssigned = true;
            }

            var actor = AuditActor.User(adminId);
            history.Record(actor, applicationId, EntitlementChanges.PlanAssigned, tenantId, planKey: plan?.Key,
                oldValue: oldPlanKey, newValue: plan?.Key, hadOldValue: true, hasNewValue: true);
            audit.Record(AuditActions.SubscriptionPlanAssigned, actor, tenantId, await ApplicationKeyAsync(applicationId, cancellationToken));

            await db.SaveChangesAsync(cancellationToken);
            await state.CommitAsync(applicationId, [subscription.Id], before, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new EntitlementResult.Done(await ReadAsync(tenantId, applicationId, cancellationToken)
            ?? throw new InvalidOperationException("The subscription was found a moment ago."));
    }

    public async Task<EntitlementResult> SetOverrideAsync(
        AuditActor actor, Guid tenantId, Guid applicationId, string limitKey, long? value, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);

        var subscription = await FindAsync(tenantId, applicationId, cancellationToken);
        if (subscription is null)
        {
            return new EntitlementResult.SubscriptionNotFound();
        }

        var refused = await ApplyOverrideAsync(actor, subscription, limitKey, value, reason, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        await transaction.CommitAsync(cancellationToken);
        return new EntitlementResult.Done(await ReadAsync(tenantId, applicationId, cancellationToken)
            ?? throw new InvalidOperationException("The subscription was found a moment ago."));
    }

    /// <summary>
    /// Sets one override on a subscription the caller has locked, inside the caller's transaction: checks it, saves it with its history and audit
    /// entries, and recomputes the subscription. Returns why it was refused, or null when it was applied (or changed nothing). Also used by the
    /// product's seat-limit endpoint, which writes a <c>seats</c> override when the product has that limit.
    /// </summary>
    public async Task<EntitlementResult?> ApplyOverrideAsync(
        AuditActor actor, TenantApplicationSubscription subscription, string limitKey, long? value, string reason, CancellationToken cancellationToken)
    {
        var limit = await db.LimitDefinitions.SingleOrDefaultAsync(
            candidate => candidate.ApplicationId == subscription.ApplicationId && candidate.Key == limitKey, cancellationToken);
        if (limit is null)
        {
            return new EntitlementResult.LimitUnknown(limitKey);
        }

        if (LimitDefinitionValidator.IsFlag(limit.Unit) && value is not (0 or 1))
        {
            return new EntitlementResult.ValueInvalid($"'{limitKey}' is an on/off feature: its value must be 0 (off) or 1 (on).");
        }

        if (limit.Key == LimitDefinitionValidator.SeatsKey && value == 0)
        {
            return new EntitlementResult.ValueInvalid(LimitDefinitionValidator.SeatsRule);
        }

        var existing = await db.SubscriptionOverrides.SingleOrDefaultAsync(
            entry => entry.SubscriptionId == subscription.Id && entry.LimitDefinitionId == limit.Id, cancellationToken);
        if (existing is not null && existing.Value == value && existing.Reason == reason)
        {
            return null;
        }

        if (existing is null
            && await db.SubscriptionOverrides.CountAsync(entry => entry.SubscriptionId == subscription.Id, cancellationToken) >= MaxOverridesPerSubscription)
        {
            return new EntitlementResult.TooManyOverrides();
        }

        var before = await state.SnapshotAsync(subscription.ApplicationId, [subscription.Id], cancellationToken);
        var now = timeProvider.GetUtcNow();
        var hadOld = existing is not null;
        var oldValue = existing?.Value;
        if (existing is null)
        {
            existing = new SubscriptionOverride { Id = Guid.CreateVersion7(), SubscriptionId = subscription.Id, LimitDefinitionId = limit.Id, Reason = reason };
            db.SubscriptionOverrides.Add(existing);
        }

        existing.Value = value;
        existing.Reason = reason;
        existing.SetByType = actor.Type;
        existing.SetById = actor.Id;
        existing.SetAt = now;

        history.Record(actor, subscription.ApplicationId, EntitlementChanges.OverrideSet, subscription.TenantId, limitKey: limitKey,
            oldValue: oldValue, newValue: value, reason: reason, hadOldValue: hadOld, hasNewValue: true);
        audit.Record(AuditActions.SubscriptionOverrideSet, actor, subscription.TenantId, await ApplicationKeyAsync(subscription.ApplicationId, cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        await state.CommitAsync(subscription.ApplicationId, [subscription.Id], before, cancellationToken);
        return null;
    }

    public async Task<EntitlementResult> ClearOverrideAsync(Guid adminId, Guid tenantId, Guid applicationId, string limitKey, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);

        var subscription = await FindAsync(tenantId, applicationId, cancellationToken);
        if (subscription is null)
        {
            return new EntitlementResult.SubscriptionNotFound();
        }

        var existing = await (
            from entry in db.SubscriptionOverrides
            join limit in db.LimitDefinitions on entry.LimitDefinitionId equals limit.Id
            where entry.SubscriptionId == subscription.Id && limit.Key == limitKey
            select entry).SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return new EntitlementResult.OverrideNotFound();
        }

        var before = await state.SnapshotAsync(applicationId, [subscription.Id], cancellationToken);
        db.SubscriptionOverrides.Remove(existing);

        var actor = AuditActor.User(adminId);
        history.Record(actor, applicationId, EntitlementChanges.OverrideCleared, tenantId, limitKey: limitKey,
            oldValue: existing.Value, hadOldValue: true, reason: existing.Reason);
        audit.Record(AuditActions.SubscriptionOverrideCleared, actor, tenantId, await ApplicationKeyAsync(applicationId, cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        await state.CommitAsync(applicationId, [subscription.Id], before, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new EntitlementResult.Done(await ReadAsync(tenantId, applicationId, cancellationToken)
            ?? throw new InvalidOperationException("The subscription was found a moment ago."));
    }

    /// <summary>A tenant's effective limits for one product with where each comes from, its overrides and its version; null if not subscribed.</summary>
    public async Task<SubscriptionEntitlementsResponse?> ReadAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        var subscription = await db.Subscriptions.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId, cancellationToken);
        if (subscription is null)
        {
            return null;
        }

        var snapshot = (await state.SnapshotAsync(applicationId, [subscription.Id], cancellationToken))[subscription.Id];
        var limits = await db.LimitDefinitions.AsNoTracking()
            .Where(limit => limit.ApplicationId == applicationId)
            .ToDictionaryAsync(limit => limit.Key, cancellationToken);
        var plan = subscription.PlanId is { } planId
            ? await db.Plans.AsNoTracking().Where(candidate => candidate.Id == planId).Select(candidate => new { candidate.Key, candidate.Name }).SingleAsync(cancellationToken)
            : null;
        var overrides = await (
            from entry in db.SubscriptionOverrides.AsNoTracking()
            join limit in db.LimitDefinitions.AsNoTracking() on entry.LimitDefinitionId equals limit.Id
            where entry.SubscriptionId == subscription.Id
            orderby limit.Key
            select new OverrideItem(limit.Key, entry.Value, entry.Reason, entry.SetByType.ToString(), entry.SetById, entry.SetAt))
            .ToListAsync(cancellationToken);

        return new SubscriptionEntitlementsResponse(
            tenantId,
            applicationId,
            plan?.Key,
            plan?.Name,
            subscription.EntitlementsVersion,
            subscription.MaxSeats,
            snapshot.Limits.Select(pair => new EntitlementItem(pair.Key, limits[pair.Key].Label, limits[pair.Key].Unit, pair.Value.Value, pair.Value.Source.ToString())).ToList(),
            overrides);
    }

    private Task<TenantApplicationSubscription?> FindAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
        db.Subscriptions.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId, cancellationToken);

    private Task<string> ApplicationKeyAsync(Guid applicationId, CancellationToken cancellationToken) =>
        db.Applications.AsNoTracking().Where(application => application.Id == applicationId).Select(application => application.Key).SingleAsync(cancellationToken);
}
