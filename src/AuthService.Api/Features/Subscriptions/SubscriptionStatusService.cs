using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Webhooks;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Subscriptions;

/// <summary>Body of <c>PATCH /api/auth/tenants/{tenantId}/subscriptions/{applicationId}</c>.</summary>
public sealed record SetSubscriptionStatusRequest(string? Status);

public abstract record SetSubscriptionStatusResult
{
    public sealed record Updated(SubscriptionResponse Subscription) : SetSubscriptionStatusResult;

    /// <summary>The subscription already had this status: nothing changed and no event was queued.</summary>
    public sealed record Unchanged(SubscriptionResponse Subscription) : SetSubscriptionStatusResult;

    public sealed record SubscriptionNotFound : SetSubscriptionStatusResult;

    /// <summary>A cancelled subscription ends there. Only subscribing again (SUB-01) brings it back.</summary>
    public sealed record AlreadyCancelled : SetSubscriptionStatusResult;
}

public static class SubscriptionStatusValidator
{
    /// <summary>Only Suspended and Cancelled can be set here. Becoming Active again is subscribing again (SUB-01).</summary>
    public static bool TryRead(SetSubscriptionStatusRequest? request, out SubscriptionStatus status)
    {
        status = default;
        if (!Enum.TryParse(request?.Status?.Trim(), ignoreCase: true, out SubscriptionStatus parsed) || parsed == SubscriptionStatus.Active)
        {
            return false;
        }

        // Rules out numbers, which Enum.TryParse would otherwise accept as underlying values.
        if (!Enum.IsDefined(parsed) || request!.Status!.Trim().All(char.IsDigit))
        {
            return false;
        }

        status = parsed;
        return true;
    }
}

/// <summary>
/// SUB-02: a product suspends or cancels a tenant's access to it. New access stops at the next token exchange (TOK-01); tokens
/// already issued are not revoked and run out within their short life. Who has a seat is kept, so reinstating restores everyone.
/// </summary>
public sealed class SubscriptionStatusService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider, IOptions<WebhookOptions> webhookOptions)
{
    public const string ApplicationUnsubscribedEvent = WebhookEvents.ApplicationUnsubscribed;

    public async Task<SetSubscriptionStatusResult> SetAsync(
        Guid applicationId, Guid tenantId, SubscriptionStatus status, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId, cancellationToken);
        if (subscription is null)
        {
            return new SetSubscriptionStatusResult.SubscriptionNotFound();
        }

        if (subscription.Status == status)
        {
            return new SetSubscriptionStatusResult.Unchanged(await db.ToResponseAsync(subscription, timeProvider.GetUtcNow(), cancellationToken));
        }

        if (subscription.Status == SubscriptionStatus.Cancelled)
        {
            return new SetSubscriptionStatusResult.AlreadyCancelled();
        }

        var now = timeProvider.GetUtcNow();
        subscription.Status = status;
        subscription.UpdatedAt = now;

        // The event is written with the change, so the product is told if and only if the change happened.
        db.WebhookOutbox.Add(WebhookEvents.Create(applicationId, ApplicationUnsubscribedEvent, now, webhookOptions.Value.RetryWindowHours, eventId => new
        {
            eventId,
            eventType = ApplicationUnsubscribedEvent,
            occurredAt = now,
            tenantId,
            applicationId,
            status = status.ToString(),
        }));

        var applicationKey = await db.Applications.AsNoTracking()
            .Where(application => application.Id == applicationId)
            .Select(application => application.Key)
            .SingleAsync(cancellationToken);
        audit.Record(
            status == SubscriptionStatus.Suspended ? AuditActions.SubscriptionSuspended : AuditActions.SubscriptionCancelled,
            AuditActor.Product(applicationId), tenantId, applicationKey);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SetSubscriptionStatusResult.Updated(await db.ToResponseAsync(subscription, now, cancellationToken));
    }
}
