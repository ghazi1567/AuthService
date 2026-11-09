using System.Text.Json;

namespace AuthService.Web.Api;

/// <summary>
/// Reads a permission list a platform admin typed or pasted (ONB-01): either one permission per line as
/// <c>key | Label | optional description</c>, or the JSON a product's team keeps next to its code, either an object with a
/// <c>permissions</c> list or the list on its own. Other properties in the JSON (such as roles, later) are ignored here. The API checks
/// the keys; this only turns text into entries and says, in plain words, which line or entry it could not read.
/// </summary>
public static class PermissionText
{
    public static (List<PermissionToAdd> Permissions, List<string> Errors) Parse(string? text)
    {
        var trimmed = (text ?? "").Trim();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[') ? ParseJson(trimmed) : ParseLines(text ?? "");
    }

    private static (List<PermissionToAdd>, List<string>) ParseLines(string text)
    {
        var permissions = new List<PermissionToAdd>();
        var errors = new List<string>();
        var lineNumber = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNumber++;
            var entry = line.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var parts = entry.Split('|', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            {
                errors.Add($"Line {lineNumber}: write a key and a label separated by a | sign.");
                continue;
            }

            permissions.Add(new PermissionToAdd(parts[0], parts[1], parts.Length == 3 && parts[2].Length > 0 ? parts[2] : null));
        }

        return (permissions, errors);
    }

    private static (List<PermissionToAdd>, List<string>) ParseJson(string text)
    {
        var permissions = new List<PermissionToAdd>();
        var errors = new List<string>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            errors.Add($"That isn't valid JSON. Check it near line {(exception.LineNumber ?? 0) + 1}.");
            return (permissions, errors);
        }

        using (document)
        {
            var list = document.RootElement;
            if (list.ValueKind == JsonValueKind.Object)
            {
                list = Property(list, "permissions") is { ValueKind: JsonValueKind.Array } found ? found : default;
            }

            if (list.ValueKind != JsonValueKind.Array)
            {
                errors.Add("The JSON needs a \"permissions\" list, like {\"permissions\": [{\"key\": \"ticket.reply\", \"label\": \"Answer tickets\"}]}.");
                return (permissions, errors);
            }

            var number = 0;
            foreach (var item in list.EnumerateArray())
            {
                number++;
                var key = item.ValueKind == JsonValueKind.Object ? Text(Property(item, "key")) : null;
                var label = item.ValueKind == JsonValueKind.Object ? Text(Property(item, "label")) : null;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(label))
                {
                    errors.Add($"Permission {number}: write a key and a label.");
                    continue;
                }

                var description = Text(Property(item, "description"));
                permissions.Add(new PermissionToAdd(key, label, string.IsNullOrEmpty(description) ? null : description));
            }
        }

        return (permissions, errors);
    }

    // Names are matched without regard to case, like the API reads them.
    private static JsonElement Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return default;
    }

    private static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()?.Trim() : null;
}
