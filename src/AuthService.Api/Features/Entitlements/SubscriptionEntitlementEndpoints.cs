using System.Security.Claims;
using System.Text.Json;
using AuthService.Api.Common;
using AuthService.Api.Features.Audit;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Features.Entitlements;

/// <summary>One row of the entitlement history. Values are JSON as stored: a number, null, a plan key, or a plan's values object.</summary>
public sealed record EntitlementHistoryItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid ApplicationId,
    Guid? TenantId,
    string? PlanKey,
    string Change,
    string? LimitKey,
    JsonElement? OldValue,
    JsonElement? NewValue,
    string? Reason,
    string ActorType,
    Guid? ActorId);

/// <summary>Filters of the history read, beside the usual newest-first paging.</summary>
public sealed record EntitlementHistoryFilter(Guid? ApplicationId, Guid? TenantId, string? PlanKey);

/// <summary>
/// ENT-03: the platform admin's view of a tenant's plan and overrides for one product, and the history of every change to plans and
/// entitlements. All under the platform-admin group.
/// </summary>
public static class SubscriptionEntitlementEndpoints
{
    private const string SubscriptionRoute = "/tenants/{tenantId:guid}/subscriptions/{applicationId:guid}";

    public static RouteGroupBuilder MapSubscriptionEntitlementsAdmin(this RouteGroupBuilder admin)
    {
        admin.MapGet(SubscriptionRoute + "/entitlements", GetAsync);
        admin.MapPut(SubscriptionRoute + "/plan", AssignPlanAsync);
        admin.MapPut(SubscriptionRoute + "/overrides/{limitKey}", SetOverrideAsync);
        admin.MapDelete(SubscriptionRoute + "/overrides/{limitKey}", ClearOverrideAsync);
        admin.MapGet("/entitlement-history", HistoryAsync);
        return admin;
    }

    private static async Task<IResult> GetAsync(Guid tenantId, Guid applicationId, SubscriptionEntitlementService entitlements, CancellationToken cancellationToken) =>
        await entitlements.ReadAsync(tenantId, applicationId, cancellationToken) is { } response ? Results.Ok(response) : SubscriptionNotFound();

    /// <summary>Puts the subscription on a plan (an active one), or takes it off with <c>{"planKey": null}</c>: then every limit takes its default.</summary>
    private static async Task<IResult> AssignPlanAsync(
        Guid tenantId, Guid applicationId, AssignPlanRequest request, ClaimsPrincipal caller, SubscriptionEntitlementService entitlements, CancellationToken cancellationToken)
    {
        var planKey = string.IsNullOrWhiteSpace(request.PlanKey) ? null : request.PlanKey.Trim().ToLowerInvariant();
        return ToResult(await entitlements.AssignPlanAsync(AdminId(caller), tenantId, applicationId, planKey, cancellationToken));
    }

    /// <summary>Sets one per-tenant override. The value must be sent (null is unlimited) and the reason is required, up to 200 characters.</summary>
    private static async Task<IResult> SetOverrideAsync(
        Guid tenantId, Guid applicationId, string limitKey, SetOverrideRequest request, ClaimsPrincipal caller,
        SubscriptionEntitlementService entitlements, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!LimitDefinitionValidator.TryReadValue(request.Value, out var value, out var valueError))
        {
            errors["Value"] = [valueError!.Replace("DefaultValue", "Value", StringComparison.Ordinal)];
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > SubscriptionEntitlementService.MaxReasonLength)
        {
            errors["Reason"] = [$"Reason is required: say why this tenant gets a different value, in at most {SubscriptionEntitlementService.MaxReasonLength} characters."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        return ToResult(await entitlements.SetOverrideAsync(
            AuditActor.User(AdminId(caller)), tenantId, applicationId, limitKey.Trim().ToLowerInvariant(), value, reason!, cancellationToken));
    }

    private static async Task<IResult> ClearOverrideAsync(
        Guid tenantId, Guid applicationId, string limitKey, ClaimsPrincipal caller, SubscriptionEntitlementService entitlements, CancellationToken cancellationToken) =>
        ToResult(await entitlements.ClearOverrideAsync(AdminId(caller), tenantId, applicationId, limitKey.Trim().ToLowerInvariant(), cancellationToken));

    /// <summary>Every change to limits, plans, assignments and overrides, newest first, optionally for one product, tenant or plan.</summary>
    private static async Task<IResult> HistoryAsync(
        [AsParameters] EntitlementHistoryFilter filter, [AsParameters] PageQuery page, AuthDbContext db, CancellationToken cancellationToken)
    {
        if (page.Problem(byName: false) is { } problem)
        {
            return problem;
        }

        var query = db.EntitlementChanges.AsNoTracking();
        if (filter.ApplicationId is { } applicationId)
        {
            query = query.Where(change => change.ApplicationId == applicationId);
        }

        if (filter.TenantId is { } tenantId)
        {
            query = query.Where(change => change.TenantId == tenantId);
        }

        if (!string.IsNullOrWhiteSpace(filter.PlanKey))
        {
            var planKey = filter.PlanKey.Trim().ToLowerInvariant();
            query = query.Where(change => change.PlanKey == planKey);
        }

        var rows = await page.NewestFirstAsync(query, change => change.OccurredAt, change => change.Id, cancellationToken);
        return Results.Ok(rows.Map(items => items.Select(change => new EntitlementHistoryItem(
            change.Id, change.OccurredAt, change.ApplicationId, change.TenantId, change.PlanKey, change.Change, change.LimitKey,
            Parse(change.OldValue), Parse(change.NewValue), change.Reason, change.ActorType.ToString(), change.ActorId)).ToList()));
    }

    private static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static IResult ToResult(EntitlementResult result) => result switch
    {
        EntitlementResult.Done done => Results.Ok(done.Entitlements),
        EntitlementResult.PlanNotFound => ApiProblem.Create(StatusCodes.Status404NotFound, "plan_not_found", "Plan not found", "This product has no plan with this key."),
        EntitlementResult.PlanInactive => ApiProblem.Create(StatusCodes.Status409Conflict, "plan_inactive",
            "Plan is not active", "This plan is deactivated, so it cannot be newly assigned. Activate it first."),
        EntitlementResult.LimitUnknown unknown => ApiProblem.Create(StatusCodes.Status404NotFound, "limit_unknown",
            "Unknown limit", $"'{unknown.Key}' is not a limit of this product."),
        EntitlementResult.ValueInvalid invalid => ApiProblem.Create(StatusCodes.Status400BadRequest, "value_invalid", "Value not allowed", invalid.Message),
        EntitlementResult.OverrideNotFound => ApiProblem.Create(StatusCodes.Status404NotFound, "override_not_found",
            "No override", "This tenant has no override for this limit."),
        EntitlementResult.TooManyOverrides => ApiProblem.Create(StatusCodes.Status409Conflict, "too_many_overrides",
            "Too many overrides", $"A tenant can have at most {SubscriptionEntitlementService.MaxOverridesPerSubscription} overrides per product."),
        _ => SubscriptionNotFound(),
    };

    private static Guid AdminId(ClaimsPrincipal caller) =>
        caller.GetUserId() ?? throw new InvalidOperationException("The platform-admin policy guarantees a user id.");

    private static IResult SubscriptionNotFound() =>
        ApiProblem.Create(StatusCodes.Status404NotFound, "subscription_not_found", "Subscription not found", "This tenant is not subscribed to this product.");
}
