using System.Text.Json;

namespace AuthService.Api.Features.Entitlements;

/// <summary>
/// Body of <c>POST /api/auth/admin/applications/{id}/plans</c>. <paramref name="Values"/> maps limit keys to a whole number or <c>null</c>
/// (unlimited); a limit left out takes its default. <paramref name="IsActive"/> defaults to true.
/// </summary>
public sealed record CreatePlanRequest(string? Key, string? Name, string? Description, bool? IsActive, Dictionary<string, JsonElement>? Values);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/plans/{planKey}</c>: the whole plan except its key and whether it is active.</summary>
public sealed record ReplacePlanRequest(string? Name, string? Description, Dictionary<string, JsonElement>? Values);

/// <summary>
/// Answer of <c>POST /api/auth/admin/applications/{id}/plans/{planKey}/preview</c> (ENT-06): how many subscriptions are on the plan, and how many
/// would get new limits (and an <c>EntitlementsChanged</c> event) if the values sent were saved.
/// </summary>
public sealed record PlanPreviewDto(int Subscriptions, int Changed);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/plans/{planKey}/active</c>.</summary>
public sealed record SetPlanActiveRequest(bool? IsActive);

/// <summary>
/// A plan as the platform admin and the product read it. <c>Values</c> holds only the limits the plan sets, by key; null is unlimited.
/// <c>CanDelete</c> is false once the plan has ever been assigned to a subscription: from then on it can only be deactivated.
/// </summary>
public sealed record PlanDto(
    string Key,
    string Name,
    string? Description,
    bool IsActive,
    bool CanDelete,
    IReadOnlyDictionary<string, long?> Values,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
