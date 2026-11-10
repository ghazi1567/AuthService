namespace AuthService.Api.Features.Entitlements;

/// <summary>Where a tenant's value for one limit comes from.</summary>
public enum EntitlementSource
{
    Default,
    Plan,
    Override,
}

/// <summary>One limit as a tenant has it: the value (null = unlimited) and where it came from.</summary>
public sealed record EffectiveLimit(long? Value, EntitlementSource Source);

/// <summary>
/// ENT-03: a tenant's effective limits are its override if one is set, otherwise its plan's value, otherwise the limit's default. Every limit of
/// the product is always in the set, so a product never has to guess what a missing key means.
/// </summary>
public static class EffectiveEntitlements
{
    public static SortedDictionary<string, EffectiveLimit> Compute(
        IReadOnlyDictionary<string, long?> defaults,
        IReadOnlyDictionary<string, long?>? planValues,
        IReadOnlyDictionary<string, long?> overrides)
    {
        var result = new SortedDictionary<string, EffectiveLimit>(StringComparer.Ordinal);
        foreach (var (key, defaultValue) in defaults)
        {
            if (overrides.TryGetValue(key, out var overridden))
            {
                result[key] = new EffectiveLimit(overridden, EntitlementSource.Override);
            }
            else if (planValues is not null && planValues.TryGetValue(key, out var planValue))
            {
                result[key] = new EffectiveLimit(planValue, EntitlementSource.Plan);
            }
            else
            {
                result[key] = new EffectiveLimit(defaultValue, EntitlementSource.Default);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether two sets tell the product something different: a value, or which limits are overridden (the product shows those as customised).
    /// A plan value changing underneath an override is not a change.
    /// </summary>
    public static bool Differ(IReadOnlyDictionary<string, EffectiveLimit> before, IReadOnlyDictionary<string, EffectiveLimit> after) =>
        before.Count != after.Count
        || after.Any(pair => !before.TryGetValue(pair.Key, out var old)
            || old.Value != pair.Value.Value
            || (old.Source == EntitlementSource.Override) != (pair.Value.Source == EntitlementSource.Override));
}
