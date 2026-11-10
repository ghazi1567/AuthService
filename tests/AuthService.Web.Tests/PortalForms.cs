using System.Net;
using System.Text.RegularExpressions;

namespace AuthService.Web.Tests;

/// <summary>
/// Posts a Blazor form the way a browser does: every input with its current value, ticked checkboxes only, textareas with their text, then
/// the changes a test makes by field name (null removes a field, such as unticking a box). Used by the ENT-06 pages, whose forms start filled in.
/// </summary>
public static partial class PortalForms
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string path, string formName, IReadOnlyDictionary<string, string?>? changes = null)
    {
        var html = await client.GetStringAsync(path, Token);
        return await PostFromHtmlAsync(client, path, html, formName, changes);
    }

    public static async Task<HttpResponseMessage> PostFromHtmlAsync(
        HttpClient client, string path, string html, string formName, IReadOnlyDictionary<string, string?>? changes = null)
    {
        foreach (Match form in FormTag().Matches(html))
        {
            var body = form.Groups["body"].Value;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match input in InputTag().Matches(body))
            {
                var tag = input.Value;
                var name = Attribute(tag, "name");
                if (name is null || tag.Contains("disabled", StringComparison.Ordinal))
                {
                    continue;
                }

                if (tag.Contains("type=\"checkbox\"", StringComparison.Ordinal))
                {
                    if (tag.Contains(" checked", StringComparison.Ordinal))
                    {
                        fields[name] = Attribute(tag, "value") ?? "on";
                    }

                    continue;
                }

                fields[name] = Attribute(tag, "value") ?? "";
            }

            foreach (Match textarea in TextareaTag().Matches(body))
            {
                fields[WebUtility.HtmlDecode(textarea.Groups["name"].Value)] = WebUtility.HtmlDecode(textarea.Groups["text"].Value);
            }

            if (!fields.TryGetValue("_handler", out var handler) || handler != formName)
            {
                continue;
            }

            foreach (var (name, value) in changes ?? new Dictionary<string, string?>())
            {
                if (value is null)
                {
                    fields.Remove(name);
                }
                else
                {
                    fields[name] = value;
                }
            }

            return await client.PostAsync(path, new FormUrlEncodedContent(fields), Token);
        }

        throw new InvalidOperationException($"The page has no form named \"{formName}\".");
    }

    /// <summary>Where a redirect sends the browser, as a path with its query.</summary>
    public static string Where(HttpResponseMessage response) => new Uri(new Uri("http://localhost"), response.Headers.Location!).PathAndQuery;

    private static string? Attribute(string tag, string name) =>
        Regex.Match(tag, $"\\b{name}=\"([^\"]*)\"") is { Success: true } found ? WebUtility.HtmlDecode(found.Groups[1].Value) : null;

    [GeneratedRegex("<form[^>]*>(?<body>.*?)</form>", RegexOptions.Singleline)]
    private static partial Regex FormTag();

    [GeneratedRegex("<input[^>]*>")]
    private static partial Regex InputTag();

    [GeneratedRegex("<textarea[^>]*\\bname=\"(?<name>[^\"]+)\"[^>]*>(?<text>.*?)</textarea>", RegexOptions.Singleline)]
    private static partial Regex TextareaTag();
}
