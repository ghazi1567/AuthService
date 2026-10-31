using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Configurations;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

/// <summary>Body of <c>POST /api/auth/tenants/{tenantId}/subscriptions</c>. The whole body is optional: no body means unlimited seats.</summary>
public sealed record SubscribeRequest(int? MaxSeats);

public sealed record SubscriptionResponse(
    Guid TenantId,
    Guid ApplicationId,
    string Status,
    int? MaxSeats,
    int SeatsUsed,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public abstract record SubscribeResult
{
    /// <summary>A new subscription, or one that was suspended or cancelled and is Active again.</summary>
    public sealed record Subscribed(SubscriptionResponse Subscription, bool IsNew) : SubscribeResult;

    /// <summary>The tenant already has an Active subscription: nothing changed.</summary>
    public sealed record AlreadySubscribed(SubscriptionResponse Subscription) : SubscribeResult;

    public sealed record TenantNotFound : SubscribeResult;

    public sealed record TenantSuspended : SubscribeResult;

    public sealed record ApplicationNotActive : SubscribeResult;
}

/// <summary>SUB-01: subscribes a tenant to the product that is calling, and tells that product about it.</summary>
public sealed class SubscribeTenantService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public const string ApplicationSubscribedEvent = "ApplicationSubscribed";

    public async Task<SubscribeResult> SubscribeAsync(Guid applicationId, Guid tenantId, int? maxSeats, CancellationToken cancellationToken)
    {
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
            return new SubscribeResult.AlreadySubscribed(await ToResponseAsync(subscription, cancellationToken));
        }

        var isNew = subscription is null;
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

        await GrantOwnersAccessAsync(tenantId, applicationId, now, cancellationToken);

        // The event is written with the change, so it exists if and only if the subscription does.
        var eventId = Guid.CreateVersion7();
        db.WebhookOutbox.Add(new WebhookOutboxMessage
        {
            Id = eventId,
            ApplicationId = applicationId,
            EventType = ApplicationSubscribedEvent,
            PayloadJson = JsonSerializer.Serialize(new
            {
                eventId,
                eventType = ApplicationSubscribedEvent,
                occurredAt = now,
                tenantId,
                applicationId,
                maxSeats,
            }),
            Status = WebhookStatus.Pending,
            NextAttemptAt = now,
            CreatedAt = now,
        });

        audit.Record(AuditActions.TenantSubscribed, AuditActor.Product(applicationId), tenantId, application.Key);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SubscribeResult.Subscribed(await ToResponseAsync(subscription, cancellationToken), isNew);
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

    private Task<SubscriptionResponse> ToResponseAsync(TenantApplicationSubscription subscription, CancellationToken cancellationToken) =>
        db.ToResponseAsync(subscription, cancellationToken);
}
