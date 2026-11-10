using AuthService.Api.Features.Applications;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Entitlements;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Subscriptions;

/// <summary>
/// Body of <c>POST /api/auth/tenants/{tenantId}/subscriptions</c>. The whole body is optional: no body means unlimited seats and no plan.
/// <c>PlanKey</c> (ENT-04) starts the subscription on one of the product's active plans.
/// </summary>
public sealed record SubscribeRequest(int? MaxSeats, string? PlanKey = null);

public sealed record SubscriptionResponse(
    Guid TenantId,
    Guid ApplicationId,
    string Status,
    int? MaxSeats,
    int SeatsUsed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? PlanKey = null,
    long? EntitlementsVersion = null,
    IReadOnlyDictionary<string, long?>? Entitlements = null);

public abstract record SubscribeResult
{
    /// <summary>A new subscription, or one that was suspended or cancelled and is Active again.</summary>
    public sealed record Subscribed(SubscriptionResponse Subscription, bool IsNew) : SubscribeResult;

    /// <summary>The tenant already has an Active subscription: nothing changed.</summary>
    public sealed record AlreadySubscribed(SubscriptionResponse Subscription) : SubscribeResult;

    public sealed record TenantNotFound : SubscribeResult;

    public sealed record TenantSuspended : SubscribeResult;

    public sealed record ApplicationNotActive : SubscribeResult;

    /// <summary>The plan asked for is not one of the product's plans.</summary>
    public sealed record PlanNotFound : SubscribeResult;

    /// <summary>The plan asked for is deactivated, so it cannot be newly assigned.</summary>
    public sealed record PlanInactive : SubscribeResult;
}

/// <summary>SUB-01: subscribes a tenant to the product that is calling, and tells that product about it.</summary>
public sealed class SubscribeTenantService(
    AuthDbContext db, AuditLog audit, TimeProvider timeProvider, IOptions<WebhookOptions> webhookOptions,
    EntitlementState entitlements, SubscriptionEntitlementService overrides, EntitlementHistory history)
{
    public const string ApplicationSubscribedEvent = WebhookEvents.ApplicationSubscribed;

    /// <param name="actor">Who is subscribing the tenant for the audit log. By default the product itself (SUB-01); a platform admin
    /// subscribing a tenant from the console (ONB-02) passes themselves.</param>
    public async Task<SubscribeResult> SubscribeAsync(
        Guid applicationId, Guid tenantId, int? maxSeats, CancellationToken cancellationToken, AuditActor? actor = null, string? planKey = null)
    {
        var who = actor ?? AuditActor.Product(applicationId);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Requests for the same tenant and product, such as a retry arriving while the first is still running, take turns.
        // The unique index would stop a duplicate row anyway, but this makes the second request see the first one's result.
        await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);

        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == applicationId)
            .Select(candidate => new { candidate.Key, candidate.IsActive })
            .SingleOrDefaultAsync(cancellationToken);

        // A product that is not active (no catalog yet, or withdrawn) has nothing to subscribe to.
        if (application is not { IsActive: true })
        {
            return new SubscribeResult.ApplicationNotActive();
        }

        var tenant = await db.Tenants.AsNoTracking()
            .Where(candidate => candidate.Id == tenantId)
            .Select(candidate => new { candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (tenant is null)
        {
            return new SubscribeResult.TenantNotFound();
        }

        if (tenant.Status == TenantStatus.Suspended)
        {
            return new SubscribeResult.TenantSuspended();
        }

        var now = timeProvider.GetUtcNow();
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId, cancellationToken);

        if (subscription is { Status: SubscriptionStatus.Active })
        {
            // Idempotent: nothing is written, no event is sent, and the seat limit is left to SUB-04.
            return new SubscribeResult.AlreadySubscribed(await db.ToResponseAsync(subscription, now, cancellationToken));
        }

        // A plan asked for must be one of the product's active plans, checked before anything is written.
        Plan? plan = null;
        if (planKey is not null)
        {
            plan = await db.Plans.SingleOrDefaultAsync(candidate => candidate.ApplicationId == applicationId && candidate.Key == planKey, cancellationToken);
            if (plan is null)
            {
                return new SubscribeResult.PlanNotFound();
            }

            if (!plan.IsActive)
            {
                return new SubscribeResult.PlanInactive();
            }
        }

        // A subscription coming back keeps one version history; one whose entitlements were never computed (from before ENT-03) starts it now.
        var isNew = subscription is null;
        var before = subscription is { EntitlementsVersion: > 0 }
            ? await entitlements.SnapshotAsync(applicationId, [subscription.Id], cancellationToken)
            : new Dictionary<Guid, EntitlementSnapshot>();
        if (subscription is null)
        {
            subscription = new TenantApplicationSubscription
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                ApplicationId = applicationId,
                CreatedAt = now,
            };
            db.Subscriptions.Add(subscription);
        }

        // A tenant that was suspended or cancelled and subscribes again is Active again, on the limit it asks for now.
        subscription.Status = SubscriptionStatus.Active;
        subscription.MaxSeats = maxSeats;
        subscription.UpdatedAt = now;

        if (plan is not null && subscription.PlanId != plan.Id)
        {
            var oldPlanKey = subscription.PlanId is { } oldId
                ? await db.Plans.Where(candidate => candidate.Id == oldId).Select(candidate => candidate.Key).SingleAsync(cancellationToken)
                : null;
            subscription.PlanId = plan.Id;
            plan.EverAssigned = true;
            history.Record(who, applicationId, EntitlementChanges.PlanAssigned, tenantId, planKey: plan.Key,
                oldValue: oldPlanKey, newValue: plan.Key, hadOldValue: true, hasNewValue: true);
        }

        await GrantOwnersAccessAsync(tenantId, applicationId, now, cancellationToken);

        // The roles the product ships with become ordinary roles in the organisation the first time it subscribes (ONB-04), never again: a
        // role its admins deleted or changed since is theirs, and a role that already has the name is left exactly as it is.
        if (isNew)
        {
            var copied = await RoleTemplateCopier.CopyToTenantAsync(db, tenantId, applicationId, now, cancellationToken);
            if (copied.Created > 0)
            {
                audit.Record(AuditActions.RoleTemplatesApplied, who, tenantId, application.Key);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await ApplyEntitlementsAsync(subscription, before, maxSeats, who, cancellationToken);

        // The event carries the subscription's entitlements as they now stand (ENT-04), so the product needs no second call and gets no separate
        // EntitlementsChanged for the same change. It is written with the change, so it exists if and only if the subscription does.
        var snapshot = (await entitlements.SnapshotAsync(applicationId, [subscription.Id], cancellationToken))[subscription.Id];
        var planNames = await entitlements.PlanNamesAsync([subscription.PlanId], cancellationToken);
        var current = subscription.PlanId is { } planId ? planNames[planId] : default((string Key, string Name)?);
        var payload = EntitlementState.Payload(snapshot, subscription.EntitlementsVersion, current?.Key, current?.Name);
        db.WebhookOutbox.Add(WebhookEvents.Create(applicationId, ApplicationSubscribedEvent, now, webhookOptions.Value.RetryWindowHours, eventId => new
        {
            eventId,
            eventType = ApplicationSubscribedEvent,
            occurredAt = now,
            schemaVersion = WebhookEvents.SchemaVersion,
            tenantId,
            applicationId,
            maxSeats = subscription.MaxSeats,
            planKey = payload.PlanKey,
            planName = payload.PlanName,
            version = payload.Version,
            entitlements = payload.Entitlements,
            overridden = payload.Overridden,
        }));

        audit.Record(AuditActions.TenantSubscribed, who, tenantId, application.Key);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var response = await db.ToResponseAsync(subscription, now, cancellationToken) with
        {
            PlanKey = payload.PlanKey,
            EntitlementsVersion = payload.Version,
            Entitlements = payload.Entitlements,
        };
        return new SubscribeResult.Subscribed(response, isNew);
    }

    /// <summary>
    /// ENT-03: a new subscription gets its first entitlements (version 1), and one coming back gets a new version if its plan changed. When the
    /// product has a <c>seats</c> limit, a seat limit asked for while subscribing becomes a <c>seats</c> override, and <c>maxSeats</c> follows the
    /// effective <c>seats</c> so the two never disagree. No EntitlementsChanged events: ApplicationSubscribed carries the result.
    /// </summary>
    private async Task ApplyEntitlementsAsync(
        TenantApplicationSubscription subscription, IReadOnlyDictionary<Guid, EntitlementSnapshot> before, int? maxSeats, AuditActor actor,
        CancellationToken cancellationToken)
    {
        await entitlements.CommitAsync(subscription.ApplicationId, [subscription.Id], before, cancellationToken, sendEvents: false);

        var hasSeatsLimit = await db.LimitDefinitions.AnyAsync(
            limit => limit.ApplicationId == subscription.ApplicationId && limit.Key == LimitDefinitionValidator.SeatsKey, cancellationToken);
        if (hasSeatsLimit)
        {
            if (maxSeats is not null)
            {
                await overrides.ApplyOverrideAsync(actor, subscription, LimitDefinitionValidator.SeatsKey, maxSeats, SeatLimitService.SeatsOverrideReason,
                    cancellationToken, sendEvents: false);
            }

            var snapshot = await entitlements.SnapshotAsync(subscription.ApplicationId, [subscription.Id], cancellationToken);
            await entitlements.SyncSeatsAsync(snapshot[subscription.Id], cancellationToken);
        }

        // The version and seat limit were changed by statements that bypass the tracked entity, so read them back.
        await db.Entry(subscription).ReloadAsync(cancellationToken);
    }

    /// <summary>The tenant's Owner counts as the first seat: they get access to the product the moment the tenant subscribes.</summary>
    private async Task GrantOwnersAccessAsync(Guid tenantId, Guid applicationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ownerMembershipIds = await (
            from assignment in db.MembershipRoles
            join role in db.Roles on new { assignment.TenantId, assignment.RoleId } equals new { role.TenantId, RoleId = role.Id }
            join membership in db.TenantMemberships on new { assignment.TenantId, assignment.MembershipId } equals new { membership.TenantId, MembershipId = membership.Id }
            where assignment.TenantId == tenantId && role.IsBuiltIn && role.Name == Role.OwnerName
                && membership.Status != MembershipStatus.Disabled
            select assignment.MembershipId)
            .ToListAsync(cancellationToken);

        var alreadyHaveAccess = await db.ApplicationAccess
            .Where(access => access.TenantId == tenantId && access.ApplicationId == applicationId)
            .Select(access => access.MembershipId)
            .ToListAsync(cancellationToken);

        foreach (var membershipId in ownerMembershipIds.Except(alreadyHaveAccess))
        {
            db.ApplicationAccess.Add(new MembershipApplicationAccess
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                MembershipId = membershipId,
                ApplicationId = applicationId,
                GrantedAt = now,
            });
        }
    }
}
