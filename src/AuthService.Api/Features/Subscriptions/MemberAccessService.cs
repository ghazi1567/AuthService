using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Subscriptions;

/// <summary>A member's access to one product after a grant, and how full the product's seats are.</summary>
public sealed record MemberAccessResponse(Guid MembershipId, Guid ApplicationId, bool HasAccess, int SeatsUsed, int? MaxSeats);

public abstract record GrantAccessResult
{
    /// <summary>A new seat was taken.</summary>
    public sealed record Granted(MemberAccessResponse Access) : GrantAccessResult;

    /// <summary>The member already had access: nothing changed.</summary>
    public sealed record AlreadyGranted(MemberAccessResponse Access) : GrantAccessResult;

    /// <summary>The membership is not in the caller's tenant, or does not exist. Never a 403, so ids are not confirmed.</summary>
    public sealed record MembershipNotFound : GrantAccessResult;

    /// <summary>The tenant has no Active subscription to the product, so there is nothing to grant.</summary>
    public sealed record NotSubscribed : GrantAccessResult;

    /// <summary>A Disabled membership cannot be given access: it would use no seat now and might not fit when re-enabled.</summary>
    public sealed record MembershipDisabled : GrantAccessResult;

    public sealed record NoSeatAvailable : GrantAccessResult;
}

public abstract record RevokeAccessResult
{
    public sealed record Revoked : RevokeAccessResult;

    /// <summary>The member had no access to this product: nothing changed.</summary>
    public sealed record NothingToRevoke : RevokeAccessResult;

    public sealed record MembershipNotFound : RevokeAccessResult;
}

/// <summary>
/// SUB-05: gives a member of the caller's tenant access to a subscribed product, which takes a seat, or takes it away, which frees
/// it. Access is what "can use the product" means; roles separately decide what they can do there. A change applies at the member's
/// next token exchange.
/// </summary>
public sealed class MemberAccessService(AuthDbContext db, AuditLog audit, TimeProvider timeProvider)
{
    public async Task<GrantAccessResult> GrantAsync(
        Guid callerUserId, Guid tenantId, Guid membershipId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var membership = await db.TenantMemberships.AsNoTracking()
            .Where(candidate => candidate.Id == membershipId && candidate.TenantId == tenantId)
            .Select(candidate => new { candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (membership is null)
        {
            return new GrantAccessResult.MembershipNotFound();
        }

        // Locking the subscription row is what makes the seat count safe: grants for one tenant and product, and a change of its
        // limit, take turns, so two requests for the last seat cannot both see it free.
        var subscription = await db.Subscriptions
            .FromSqlInterpolated($"SELECT * FROM subscriptions WHERE tenant_id = {tenantId} AND application_id = {applicationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (subscription is not { Status: SubscriptionStatus.Active })
        {
            return new GrantAccessResult.NotSubscribed();
        }

        if (membership.Status == MembershipStatus.Disabled)
        {
            return new GrantAccessResult.MembershipDisabled();
        }

        var seatsUsed = await db.SeatsUsedAsync(tenantId, applicationId, cancellationToken);
        var alreadyHasAccess = await db.ApplicationAccess.AnyAsync(
            access => access.MembershipId == membershipId && access.ApplicationId == applicationId, cancellationToken);
        if (alreadyHasAccess)
        {
            return new GrantAccessResult.AlreadyGranted(new MemberAccessResponse(membershipId, applicationId, true, seatsUsed, subscription.MaxSeats));
        }

        if (subscription.MaxSeats is { } maxSeats && seatsUsed >= maxSeats)
        {
            return new GrantAccessResult.NoSeatAvailable();
        }

        db.ApplicationAccess.Add(new MembershipApplicationAccess
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            MembershipId = membershipId,
            ApplicationId = applicationId,
            GrantedAt = timeProvider.GetUtcNow(),
        });

        audit.Record(AuditActions.MemberAccessGranted, AuditActor.User(callerUserId), tenantId, await AuditDetailAsync(membershipId, applicationId, cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new GrantAccessResult.Granted(new MemberAccessResponse(membershipId, applicationId, true, seatsUsed + 1, subscription.MaxSeats));
    }

    public async Task<RevokeAccessResult> RevokeAsync(
        Guid callerUserId, Guid tenantId, Guid membershipId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var membershipExists = await db.TenantMemberships.AnyAsync(
            candidate => candidate.Id == membershipId && candidate.TenantId == tenantId, cancellationToken);
        if (!membershipExists)
        {
            return new RevokeAccessResult.MembershipNotFound();
        }

        // Taken for the same reason as when granting, so a revoke and a grant for the freed seat cannot cross.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM subscriptions WHERE tenant_id = {tenantId} AND application_id = {applicationId} FOR UPDATE", cancellationToken);

        var access = await db.ApplicationAccess.SingleOrDefaultAsync(
            candidate => candidate.MembershipId == membershipId && candidate.ApplicationId == applicationId, cancellationToken);
        if (access is null)
        {
            return new RevokeAccessResult.NothingToRevoke();
        }

        // Allowed whatever the subscription's status, so seats can always be freed.
        db.ApplicationAccess.Remove(access);
        audit.Record(AuditActions.MemberAccessRevoked, AuditActor.User(callerUserId), tenantId, await AuditDetailAsync(membershipId, applicationId, cancellationToken));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RevokeAccessResult.Revoked();
    }

    /// <summary>The membership and the product, as one safe code: <c>membership-id.product-key</c>.</summary>
    private async Task<string> AuditDetailAsync(Guid membershipId, Guid applicationId, CancellationToken cancellationToken)
    {
        var key = await db.Applications.AsNoTracking()
            .Where(application => application.Id == applicationId)
            .Select(application => application.Key)
            .SingleAsync(cancellationToken);
        return $"{membershipId}.{key}";
    }
}
