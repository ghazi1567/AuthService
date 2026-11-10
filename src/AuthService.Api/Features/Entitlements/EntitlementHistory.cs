using System.Text.Json;
using AuthService.Api.Features.Audit;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;

namespace AuthService.Api.Features.Entitlements;

/// <summary>The kinds of change the entitlement history records, as stored in <c>entitlement_changes.change</c>.</summary>
public static class EntitlementChanges
{
    public const string LimitAdded = "limit_added";
    public const string LimitChanged = "limit_changed";
    public const string PlanCreated = "plan_created";
    public const string PlanUpdated = "plan_updated";
    public const string PlanActivated = "plan_activated";
    public const string PlanDeactivated = "plan_deactivated";
    public const string PlanDeleted = "plan_deleted";
    public const string PlanAssigned = "plan_assigned";
    public const string OverrideSet = "override_set";
    public const string OverrideCleared = "override_cleared";
}

/// <summary>
/// ENT-03: writes the platform admin's history of plans and entitlements: who changed what, when, from what to what, and why. Rows are added to
/// the caller's context, so they are saved, or not, with the change itself.
/// </summary>
public sealed class EntitlementHistory(AuthDbContext db, TimeProvider timeProvider)
{
    public void Record(
        AuditActor actor, Guid applicationId, string change,
        Guid? tenantId = null, string? planKey = null, string? limitKey = null,
        object? oldValue = null, object? newValue = null, string? reason = null, bool hadOldValue = false, bool hasNewValue = false)
    {
        db.EntitlementChanges.Add(new EntitlementChange
        {
            Id = Guid.CreateVersion7(),
            OccurredAt = timeProvider.GetUtcNow(),
            ApplicationId = applicationId,
            TenantId = tenantId,
            PlanKey = planKey,
            Change = change,
            LimitKey = limitKey,
            OldValue = oldValue is not null || hadOldValue ? JsonSerializer.Serialize(oldValue) : null,
            NewValue = newValue is not null || hasNewValue ? JsonSerializer.Serialize(newValue) : null,
            Reason = reason,
            ActorType = actor.Type,
            ActorId = actor.Id,
        });
    }
}
