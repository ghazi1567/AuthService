using System.Globalization;
using System.Net;
using System.Text.Json;
using AuthService.Authorization;

namespace AuthService.Entitlements;

/// <summary>
/// The Auth Service's entitlement reads, made as the product through <see cref="IAuthServiceClient"/> (the same cached service token).
/// Use them for a first sync, to catch up after missed events, and to show plans.
/// </summary>
public interface IEntitlementsClient
{
    /// <summary>The product's limits.</summary>
    Task<IReadOnlyList<LimitDefinition>> GetLimitsAsync(CancellationToken cancellationToken = default);

    /// <summary>The product's plans.</summary>
    Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken = default);

    /// <summary>One organisation's entitlements, whatever its status, or null when it never subscribed to the product.</summary>
    Task<EntitlementSnapshot?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of every organisation of the product, oldest change first. Pass null for a first sync, then the change number of the last
    /// item already read to get only the organisations whose entitlements changed since. <paramref name="limit"/> is 1 to 500.
    /// </summary>
    Task<EntitlementChangesPage> GetChangesAsync(long? after = null, int limit = 500, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every page after <paramref name="after"/> and applies each snapshot to <paramref name="store"/> (only newer versions are kept).
    /// Returns the change number to keep for next time, or <paramref name="after"/> when nothing changed.
    /// </summary>
    Task<long?> CatchUpAsync(IEntitlementStore store, long? after = null, CancellationToken cancellationToken = default);
}

internal sealed class EntitlementsClient(IAuthServiceClient client) : IEntitlementsClient
{
    private const int MaxPages = 10_000;

    public async Task<IReadOnlyList<LimitDefinition>> GetLimitsAsync(CancellationToken cancellationToken = default)
    {
        var limits = new List<LimitDefinition>();
        await ForEachPageAsync("/api/auth/applications/me/limits", item => limits.Add(new LimitDefinition(
            item.GetProperty("key").GetString()!,
            item.GetProperty("label").GetString()!,
            OptionalString(item, "description"),
            OptionalString(item, "unit"),
            ReadValue(item.GetProperty("defaultValue")))), cancellationToken);
        return limits;
    }

    public async Task<IReadOnlyList<PlanSummary>> GetPlansAsync(CancellationToken cancellationToken = default)
    {
        var plans = new List<PlanSummary>();
        await ForEachPageAsync("/api/auth/applications/me/plans", item => plans.Add(new PlanSummary(
            item.GetProperty("key").GetString()!,
            item.GetProperty("name").GetString()!,
            OptionalString(item, "description"),
            item.GetProperty("isActive").GetBoolean(),
            item.GetProperty("values").EnumerateObject().ToDictionary(value => value.Name, value => ReadValue(value.Value), StringComparer.Ordinal))), cancellationToken);
        return plans;
    }

    public async Task<EntitlementSnapshot?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        using var response = await client.SendAsync(HttpMethod.Get, $"/api/auth/tenants/{tenantId}/subscriptions/me/entitlements", cancellationToken: cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        using var document = await ReadAsync(response, cancellationToken);
        return ReadItem(document.RootElement);
    }

    public async Task<EntitlementChangesPage> GetChangesAsync(long? after = null, int limit = 500, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 500);
        var path = $"/api/auth/applications/me/entitlements?limit={limit}" + (after is { } from ? $"&after={from.ToString(CultureInfo.InvariantCulture)}" : "");
        using var response = await client.SendAsync(HttpMethod.Get, path, cancellationToken: cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var root = document.RootElement;
        var items = root.GetProperty("items").EnumerateArray().Select(ReadItem).ToList();
        long? next = root.TryGetProperty("next", out var cursor) && cursor.ValueKind == JsonValueKind.Object ? cursor.GetProperty("after").GetInt64() : null;
        return new EntitlementChangesPage(items, next);
    }

    public async Task<long?> CatchUpAsync(IEntitlementStore store, long? after = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var position = after;
        for (var page = 0; page < MaxPages; page++)
        {
            var changes = await GetChangesAsync(position, 500, cancellationToken);
            foreach (var snapshot in changes.Items)
            {
                await store.ApplyAsync(snapshot, cancellationToken);
                if (snapshot.Status is { } status)
                {
                    await store.SetStatusAsync(snapshot.TenantId, status, cancellationToken);
                }
            }

            position = changes.LastChangeNumber ?? position;
            if (changes.Next is null)
            {
                break;
            }
        }

        return position;
    }

    private async Task ForEachPageAsync(string path, Action<JsonElement> read, CancellationToken cancellationToken)
    {
        var query = "";
        for (var page = 0; page < MaxPages; page++)
        {
            using var response = await client.SendAsync(HttpMethod.Get, $"{path}?limit=200{query}", cancellationToken: cancellationToken);
            using var document = await ReadAsync(response, cancellationToken);
            foreach (var item in document.RootElement.GetProperty("items").EnumerateArray())
            {
                read(item);
            }

            if (!document.RootElement.TryGetProperty("next", out var next) || next.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            query = string.Concat(next.EnumerateObject().Select(property => $"&{property.Name}={Uri.EscapeDataString(property.Value.ToString())}"));
        }
    }

    private static EntitlementSnapshot ReadItem(JsonElement item)
    {
        var tenantId = item.GetProperty("tenantId").GetGuid();
        var applicationId = item.GetProperty("applicationId").GetGuid();
        long? changeNumber = item.TryGetProperty("changeNumber", out var number) && number.TryGetInt64(out var value) ? value : null;
        return AuthWebhookParser.ReadSnapshot(item, tenantId, applicationId, changeNumber: changeNumber)
            ?? throw new AuthServiceException(null, null, "The Auth Service sent entitlements without their limits.");
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ProblemAsync(response, cancellationToken);
        }

        try
        {
            return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new AuthServiceException(response.StatusCode, null, "The Auth Service sent an answer that could not be read.", exception);
        }
    }

    private static async Task<AuthServiceException> ProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null, detail = null;
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            code = OptionalString(problem.RootElement, "code");
            detail = OptionalString(problem.RootElement, "detail") ?? OptionalString(problem.RootElement, "title");
        }
        catch (JsonException)
        {
        }

        return new AuthServiceException(response.StatusCode, code, detail ?? $"The Auth Service answered {(int)response.StatusCode}.");
    }

    private static LimitValue ReadValue(JsonElement value) => value.ValueKind == JsonValueKind.Null ? LimitValue.Unlimited : new LimitValue(value.GetInt64());

    private static string? OptionalString(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
