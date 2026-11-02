using System.Text.Json;

namespace AuthService.Web.Api;

/// <summary>
/// An error from the API, reduced to what a page needs: the status, the stable <see cref="Code"/> (for example
/// <c>rate_limited</c> or <c>email_not_verified</c>) that screens turn into wording, per-field validation messages, and how long to
/// wait when the API said so. Raw exception text and stack traces never come from the API, and are never kept here.
/// </summary>
public sealed record ApiProblem(
    int Status,
    string Code,
    string? Title = null,
    string? Detail = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    TimeSpan? RetryAfter = null)
{
    /// <summary>The API could not be reached at all (network failure or timeout).</summary>
    public const string Unreachable = "api_unreachable";

    /// <summary>The API answered with something that was not a problem document.</summary>
    public const string Unexpected = "unexpected_response";

    public static ApiProblem FromUnreachable() => new(StatusCodes.Status503ServiceUnavailable, Unreachable);

    /// <summary>Reads an <c>application/problem+json</c> body; anything else becomes a plain problem with the status only.</summary>
    public static ApiProblem Parse(int status, string? body, TimeSpan? retryAfter)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ApiProblem(status, Unexpected, RetryAfter: retryAfter);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ApiProblem(status, Unexpected, RetryAfter: retryAfter);
            }

            var code = root.TryGetProperty("code", out var codeElement) && codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()!
                : Unexpected;

            Dictionary<string, string[]>? errors = null;
            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
            {
                errors = [];
                foreach (var field in errorsElement.EnumerateObject())
                {
                    errors[field.Name] = field.Value.ValueKind == JsonValueKind.Array
                        ? [.. field.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
                        : [];
                }
            }

            return new ApiProblem(status, code, ReadString(root, "title"), ReadString(root, "detail"), errors, retryAfter);
        }
        catch (JsonException)
        {
            return new ApiProblem(status, Unexpected, RetryAfter: retryAfter);
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
