using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Api.Features.Entitlements;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

public abstract record SetSeatLimitResult
{
    public sealed record Updated(SubscriptionResponse Subscription) : SetSeatLimitResult;

    public sealed record SubscriptionNotFound : SetSeatLimitResult;

    /// <summary>The organisation is suspended, and a suspended organisation's subscriptions are not changed (AUTH-268).</summary>
    public sealed record TenantSuspended : SetSeatLimitResult;
}

public static class SeatLimitValidator
{
    /// <summary>
    /// The body must say <c>maxSeats</c> outright: a number of at least 1, or null for unlimited. Leaving it out is an error,
    /// not "unlimited", so a mistyped field name cannot silently remove a tenant's limit.
    /// </summary>
    public static bool TryRead(JsonElement body, out int? maxSeats, out string? error)
    {
        maxSeats = null;
        error = null;

        if (body.ValueKind != JsonValueKind.Object || !TryGetProperty(body, "maxSeats", out var value))
        {
            error = "maxSeats is required: a whole number of at least 1, or null for unlimited.";
            return false;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var seats) && seats >= 1)
        {
            maxSeats = seats;
            return true;
        }

        error = "maxSeats must be a whole number of at least 1, or null for unlimited.";
        return false;
    }

    private static bool TryGetProperty(JsonElement body, string name, out JsonElement value)
    {
        foreach (var property in body.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

/// <summary>SUB-04: a product sets the number of seats a tenant's plan allows. Billing stays in the product; only the number crosses.</summary>
public sealed class SeatLimitService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider, SubscriptionEntitlementService overrides)
{
    /// <summary>The reason written on a <c>seats</c> override that came from the product's own seat-limit call (ENT-03).</summary>
    public const string SeatsOverrideReason = "Seat limit set by the product.";

    public async Task<SetSeatLimitResult> SetAsync(Guid applicationId, Guid tenantId, int? maxSeats, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The product can only reach its own subscriptions: a tenant subscribed to another product is simply not found.
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.ApplicationId == applicationId, cancellationToken);
        if (subscription is null)
        {
            return new SetSeatLimitResult.SubscriptionNotFound();
        }

        if (await db.Tenants.AsNoTracking().AnyAsync(tenant => tenant.Id == tenantId && tenant.Status == TenantStatus.Suspended, cancellationToken))
        {
            return new SetSeatLimitResult.TenantSuspended();
        }

        // Accepted whatever the current usage: people who already have access keep it, and only new grants are blocked (SUB-05).
        // When the product has a seats limit (ENT-03) the number is kept as a seats override, so there is one source for it and maxSeats follows.
        var hasSeatsLimit = await db.LimitDefinitions.AnyAsync(
            limit => limit.ApplicationId == applicationId && limit.Key == LimitDefinitionValidator.SeatsKey, cancellationToken);
        if (hasSeatsLimit)
        {
            await db.LockSubscriptionAsync(tenantId, applicationId, cancellationToken);
            await overrides.ApplyOverrideAsync(AuditActor.Product(applicationId), subscription, LimitDefinitionValidator.SeatsKey, maxSeats, SeatsOverrideReason, cancellationToken);
            await db.Entry(subscription).ReloadAsync(cancellationToken);
        }
        else
        {
            subscription.MaxSeats = maxSeats;
        }

        subscription.UpdatedAt = timeProvider.GetUtcNow();

        var applicationKey = await db.Applications.AsNoTracking()
            .Where(application => application.Id == applicationId)
            .Select(application => application.Key)
            .SingleAsync(cancellationToken);
        audit.Record(AuditActions.SeatLimitChanged, AuditActor.Product(applicationId), tenantId, applicationKey);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SetSeatLimitResult.Updated(await db.ToResponseAsync(subscription, timeProvider.GetUtcNow(), cancellationToken));
    }
}
