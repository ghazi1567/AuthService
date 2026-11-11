using System.Globalization;

namespace AuthService.Entitlements;

/// <summary>A limit's value: a whole number of 0 or more, or unlimited.</summary>
/// <param name="Value">The number, or null for unlimited.</param>
public readonly record struct LimitValue(long? Value)
{
    /// <summary>No limit at all.</summary>
    public static LimitValue Unlimited => new(null);

    /// <summary>Whether there is no limit.</summary>
    public bool IsUnlimited => Value is null;

    /// <summary>Whether an on/off feature (a limit with the unit <c>flag</c>) is on: unlimited or any number above 0.</summary>
    public bool IsOn => Value is null or > 0;

    /// <summary>
    /// Whether <paramref name="current"/> is allowed: at most the limit, or anything when unlimited. Check the amount after the action,
    /// such as <c>IsWithin(contacts + imported)</c>.
    /// </summary>
    public bool IsWithin(long current) => Value is not { } limit || current <= limit;

    /// <summary>How much is left above <paramref name="current"/>, never below 0; null when unlimited.</summary>
    public long? Remaining(long current) => Value is { } limit ? Math.Max(0, limit - current) : null;

    /// <inheritdoc />
    public override string ToString() => Value?.ToString(CultureInfo.InvariantCulture) ?? "unlimited";
}

/// <summary>A limit the product has, as the Auth Service lists it.</summary>
/// <param name="Key">The key the product checks, such as <c>contacts.reachable</c>.</param>
/// <param name="Label">What people read.</param>
/// <param name="Description">A longer explanation, if any.</param>
/// <param name="Unit">The unit shown after the number; <c>flag</c> marks an on/off feature.</param>
/// <param name="Default">What an organisation gets when neither its plan nor an override sets the limit.</param>
public sealed record LimitDefinition(string Key, string Label, string? Description, string? Unit, LimitValue Default);

/// <summary>One of the product's plans.</summary>
/// <param name="Key">The plan's key, such as <c>growth</c>.</param>
/// <param name="Name">What people read.</param>
/// <param name="Description">A longer explanation, if any.</param>
/// <param name="IsActive">Whether it can be given to more organisations.</param>
/// <param name="Values">The limits the plan sets; a limit left out takes its default.</param>
public sealed record PlanSummary(string Key, string Name, string? Description, bool IsActive, IReadOnlyDictionary<string, LimitValue> Values);

/// <summary>
/// One organisation's entitlements for the product at one version: always every limit of the product, never a difference. Keep the
/// snapshot with the highest <see cref="Version"/>; events and reads may arrive more than once and out of order.
/// </summary>
/// <param name="TenantId">The organisation.</param>
/// <param name="ApplicationId">The product.</param>
/// <param name="PlanKey">The plan's key, or null for no plan.</param>
/// <param name="PlanName">The plan's name, or null for no plan.</param>
/// <param name="Version">Rises each time the limits change.</param>
/// <param name="Limits">Every limit by key.</param>
/// <param name="Overridden">The keys an override sets for this organisation.</param>
/// <param name="Status">The subscription's status (<c>Active</c>, <c>Suspended</c>, <c>Cancelled</c>) when it was known, else null.</param>
/// <param name="ChangeNumber">The Auth Service's change number for this version when it came from a read, else null.</param>
public sealed record EntitlementSnapshot(
    Guid TenantId,
    Guid ApplicationId,
    string? PlanKey,
    string? PlanName,
    long Version,
    IReadOnlyDictionary<string, LimitValue> Limits,
    IReadOnlyCollection<string> Overridden,
    string? Status = null,
    long? ChangeNumber = null)
{
    /// <summary>The limit with this key, or null when the snapshot does not have it (a limit added after it was taken).</summary>
    public LimitValue? Get(string key) => Limits.TryGetValue(key, out var value) ? value : null;

    /// <summary>Whether an override sets this limit for the organisation.</summary>
    public bool IsOverridden(string key) => Overridden.Contains(key);
}

/// <summary>One page of the product's organisations in the order their entitlements changed.</summary>
/// <param name="Items">The organisations on this page, oldest change first.</param>
/// <param name="Next">The change number to pass as <c>after</c> for the next page, or null on the last page.</param>
public sealed record EntitlementChangesPage(IReadOnlyList<EntitlementSnapshot> Items, long? Next)
{
    /// <summary>The change number of the last item, to keep and pass as <c>after</c> next time; null on an empty page.</summary>
    public long? LastChangeNumber => Items.Count == 0 ? null : Items[^1].ChangeNumber;
}
