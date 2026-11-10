using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Tenants;

/// <summary>An organisation as the platform admin's list shows it: where it stands and how big it is, nothing about its people.</summary>
public sealed record AdminTenantItem(Guid Id, string Name, string Status, int MemberCount, DateTimeOffset CreatedAt);

/// <summary>Body of <c>PUT /api/auth/admin/tenants/{id}/status</c>.</summary>
public sealed record SetTenantStatusRequest(string? Status, string? Reason);

public abstract record SetTenantStatusResult
{
    public sealed record Changed(AdminTenantItem Tenant) : SetTenantStatusResult;

    /// <summary>The organisation already had this status: nothing changed and nothing was audited.</summary>
    public sealed record Unchanged(AdminTenantItem Tenant) : SetTenantStatusResult;

    public sealed record NotFound : SetTenantStatusResult;
}

public static class TenantStatusValidator
{
    /// <summary>
    /// The reasons an admin can give, for suspending or for reactivating. They are codes, not sentences: the audit log keeps only short
    /// codes in its detail column (never free text, which could hold a person's name or an address), so the reason is one of these.
    /// </summary>
    public static readonly IReadOnlyList<string> Reasons = ["non_payment", "abuse", "security", "customer_request", "resolved", "other"];

    /// <summary>Only Active and Suspended can be set, with one of <see cref="Reasons"/>; a number or any other word is refused.</summary>
    public static bool TryValidate(SetTenantStatusRequest? request, out TenantStatus status, out string reason, out Dictionary<string, string[]> errors)
    {
        errors = [];
        status = default;
        reason = request?.Reason?.Trim().ToLowerInvariant() ?? "";

        var text = request?.Status?.Trim();
        if (string.IsNullOrEmpty(text) || text.All(char.IsDigit) || !Enum.TryParse(text, ignoreCase: true, out status) || !Enum.IsDefined(status))
        {
            errors[nameof(SetTenantStatusRequest.Status)] = ["Status must be Active or Suspended."];
        }

        if (!Reasons.Contains(reason))
        {
            errors[nameof(SetTenantStatusRequest.Reason)] = [$"Reason is required, and must be one of: {string.Join(", ", Reasons)}. It goes into the audit log."];
        }

        return errors.Count == 0;
    }
}

/// <summary>
/// AUTH-268: a platform admin suspends an organisation or makes it active again. Until now this was a hand edit in the database, with no
/// trace. Every real change is audited with the admin, the organisation and the reason. What a suspension stops is enforced where the
/// organisation is used (token exchange, tenant-admin calls, API keys, subscribing, accepting invitations), all of which read this status.
/// </summary>
public sealed class TenantStatusService(AuthDbContext db, AuditLog audit)
{
    public async Task<SetTenantStatusResult> SetAsync(Guid adminId, Guid tenantId, TenantStatus status, string reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Changes to one organisation take turns, as for its members, so two admins acting at once leave one clear last word and one audit entry each.
        var tenant = await db.Tenants
            .FromSqlInterpolated($"SELECT * FROM tenants WHERE id = {tenantId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (tenant is null)
        {
            return new SetTenantStatusResult.NotFound();
        }

        if (tenant.Status == status)
        {
            return new SetTenantStatusResult.Unchanged(await ToItemAsync(tenant, cancellationToken));
        }

        tenant.Status = status;
        audit.Record(
            status == TenantStatus.Suspended ? AuditActions.TenantSuspended : AuditActions.TenantReactivated,
            AuditActor.User(adminId), tenantId, reason);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SetTenantStatusResult.Changed(await ToItemAsync(tenant, cancellationToken));
    }

    private async Task<AdminTenantItem> ToItemAsync(Tenant tenant, CancellationToken cancellationToken) =>
        new(tenant.Id, tenant.Name, tenant.Status.ToString(),
            await db.TenantMemberships.CountAsync(membership => membership.TenantId == tenant.Id, cancellationToken), tenant.CreatedAt);
}
