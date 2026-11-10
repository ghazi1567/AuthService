using System.Globalization;

namespace AuthService.Web.Api;

/// <summary>A product's limit as the platform admin reads it (<c>GET /api/auth/admin/applications/{id}/limits</c>). A null default is unlimited.</summary>
public sealed record LimitItem(string Key, string Label, string? Description, string? Unit, long? DefaultValue, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    /// <summary>An on/off feature: its values can only be 0 (off) or 1 (on).</summary>
    public bool IsFlag => string.Equals(Unit, EntitlementText.FlagUnit, StringComparison.OrdinalIgnoreCase);
}

/// <summary>One limit to add or change. The default must always be sent: null is unlimited.</summary>
public sealed record LimitToRegister(string Key, string Label, string? Description, string? Unit, long? DefaultValue);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/limits</c>.</summary>
public sealed record RegisterLimitsRequest(IReadOnlyList<LimitToRegister> Limits);

/// <summary>What registering limits did.</summary>
public sealed record RegisterLimitsResult(Guid ApplicationId, int Created, int Updated, int Unchanged);

/// <summary>A plan as the platform admin reads it. <c>Values</c> holds only the limits the plan sets; null is unlimited.</summary>
public sealed record PlanItem(
    string Key,
    string Name,
    string? Description,
    bool IsActive,
    bool CanDelete,
    Dictionary<string, long?> Values,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Body of <c>POST /api/auth/admin/applications/{id}/plans</c>.</summary>
public sealed record CreatePlanRequest(string Key, string Name, string? Description, bool IsActive, Dictionary<string, long?> Values);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/plans/{key}</c> and of its <c>/preview</c>.</summary>
public sealed record ReplacePlanRequest(string Name, string? Description, Dictionary<string, long?> Values);

/// <summary>What saving a plan's values would do: subscriptions on the plan, and how many would get new limits.</summary>
public sealed record PlanPreview(int Subscriptions, int Changed);

/// <summary>Body of <c>PUT /api/auth/admin/applications/{id}/plans/{key}/active</c>.</summary>
public sealed record SetPlanActiveRequest(bool IsActive);

/// <summary>One of an organisation's subscriptions as the platform admin lists them.</summary>
public sealed record AdminTenantSubscriptionItem(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    string Status,
    string? PlanKey,
    string? PlanName,
    int? MaxSeats,
    long EntitlementsVersion,
    DateTimeOffset CreatedAt);

/// <summary>One effective limit with where it comes from: <c>Default</c>, <c>Plan</c> or <c>Override</c>.</summary>
public sealed record EffectiveLimitItem(string Key, string Label, string? Unit, long? Value, string Source);

/// <summary>An override as the platform admin reads it.</summary>
public sealed record OverrideItem(string LimitKey, long? Value, string Reason, string SetByType, Guid? SetById, DateTimeOffset SetAt);

/// <summary>A tenant's entitlements for one product, as the platform admin reads them.</summary>
public sealed record SubscriptionEntitlements(
    Guid TenantId,
    Guid ApplicationId,
    string? PlanKey,
    string? PlanName,
    long Version,
    int? MaxSeats,
    List<EffectiveLimitItem> Limits,
    List<OverrideItem> Overrides);

/// <summary>Body of <c>PUT .../plan</c>: null takes the subscription off its plan.</summary>
public sealed record AssignPlanRequest(string? PlanKey);

/// <summary>Body of <c>PUT .../overrides/{limitKey}</c>: the value must be sent (null is unlimited) and the reason is required.</summary>
public sealed record SetOverrideRequest(long? Value, string Reason);

/// <summary>One row of the entitlement history. Old and new values are JSON as stored.</summary>
public sealed record EntitlementHistoryItem(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid ApplicationId,
    Guid? TenantId,
    string? PlanKey,
    string Change,
    string? LimitKey,
    System.Text.Json.JsonElement? OldValue,
    System.Text.Json.JsonElement? NewValue,
    string? Reason,
    string ActorType,
    Guid? ActorId);

/// <summary>Where the next page of the history starts.</summary>
public sealed record HistoryCursor(DateTimeOffset Before, Guid BeforeId);

/// <summary>One page of the history.</summary>
public sealed record HistoryPage(List<EntitlementHistoryItem> Items, HistoryCursor? Next);

/// <summary>A limit as the organisation's own admins see it, read-only.</summary>
public sealed record TenantLimitItem(string Key, string Label, string? Description, string? Unit, long? Value, bool Overridden);

/// <summary>The organisation's own plan and limits for one product.</summary>
public sealed record TenantEntitlements(
    Guid ApplicationId,
    string ApplicationKey,
    string ApplicationName,
    string Status,
    string? PlanKey,
    string? PlanName,
    long Version,
    List<TenantLimitItem> Limits);

/// <summary>How limit values are written and read in the portal: a whole number, or unlimited.</summary>
public static class EntitlementText
{
    public const string FlagUnit = "flag";
    public const string SeatsKey = "seats";

    /// <summary>A value as people read it: "Unlimited", "On"/"Off" for a feature, or the number with its unit.</summary>
    public static string Describe(long? value, string? unit)
    {
        if (value is null)
        {
            return "Unlimited";
        }

        if (string.Equals(unit, FlagUnit, StringComparison.OrdinalIgnoreCase))
        {
            return value == 0 ? "Off" : "On";
        }

        var number = value.Value.ToString("N0", CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(unit) ? number : $"{number} {unit}";
    }

    /// <summary>
    /// Reads a value typed in a form. Empty is "not given" (<paramref name="given"/> false). Otherwise a whole number of at least 0, with
    /// thousands separators allowed.
    /// </summary>
    public static bool TryRead(string? text, out bool given, out long value)
    {
        value = 0;
        var trimmed = (text ?? "").Trim().Replace(",", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        given = trimmed.Length > 0;
        return !given || (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0);
    }
}
