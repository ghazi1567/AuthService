using System.Net.Http.Json;
using System.Text.Json;

namespace AuthService.IntegrationTests.Infrastructure;

/// <summary>A page of a list as the API returns it (PLT-08): the items, and where the next page starts (null on the last page).</summary>
public sealed record PageBody<T>(List<T> Items, JsonElement? Next);

public static class PagedJson
{
    /// <summary>Reads the whole page of a list response.</summary>
    public static async Task<PageBody<T>> ReadPageAsync<T>(this HttpContent content, JsonSerializerOptions json, CancellationToken cancellationToken)
    {
        var text = await content.ReadAsStringAsync(cancellationToken);
        var page = JsonSerializer.Deserialize<PageBody<T>>(text, json);

        // An error body deserialises into an empty page; say what came back instead.
        return page is { Items: not null } ? page : throw new InvalidOperationException($"Expected a page of items but got: {text}");
    }

    /// <summary>Reads just the items of a list response, for the tests that are not about paging.</summary>
    public static async Task<List<T>> ReadPageItemsAsync<T>(this HttpContent content, JsonSerializerOptions json, CancellationToken cancellationToken) =>
        (await content.ReadPageAsync<T>(json, cancellationToken)).Items;
}
