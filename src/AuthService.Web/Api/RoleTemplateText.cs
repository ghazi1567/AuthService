using System.Text.Json;

namespace AuthService.Web.Api;

/// <summary>
/// Reads the roles a product ships with from the JSON a product's team keeps next to its permissions (ONB-04):
/// <c>{ "roles": [ { "name": "Marketer", "description": "…", "permissions": [ { "key": "campaign.view", "level": "ReadOnly" } ] } ] }</c>.
/// Other properties (the permissions) are ignored here, and so is anything that is not JSON. The API checks the names, keys and levels;
/// this only turns text into entries and says, in plain words, which entry it could not read.
/// </summary>
public static class RoleTemplateText
{
    /// <summary>
    /// The roles in the text, or null when it holds no <c>roles</c> list at all (not JSON, no such property, or something else there). An
    /// empty list is a list: it means "no roles". A problem with an entry is reported and the others are still read.
    /// </summary>
    public static (List<ProductRoleToSet>? Roles, List<string> Errors) Parse(string? text)
    {
        var errors = new List<string>();
        var trimmed = (text ?? "").Trim();
        if (!trimmed.StartsWith('{'))
        {
            return (null, errors);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            errors.Add($"That isn't valid JSON. Check it near line {(exception.LineNumber ?? 0) + 1}.");
            return (null, errors);
        }

        using (document)
        {
            if (Property(document.RootElement, "roles") is not { ValueKind: JsonValueKind.Array } list)
            {
                return (null, errors);
            }

            var roles = new List<ProductRoleToSet>();
            var number = 0;
            foreach (var item in list.EnumerateArray())
            {
                number++;
                var name = item.ValueKind == JsonValueKind.Object ? Text(Property(item, "name")) : null;
                if (string.IsNullOrEmpty(name))
                {
                    errors.Add($"Role {number}: write a name.");
                    continue;
                }

                var description = Text(Property(item, "description"));
                var grants = new List<ProductRoleGrant>();
                if (Property(item, "permissions") is { ValueKind: JsonValueKind.Array } permissions)
                {
                    var grantNumber = 0;
                    foreach (var grant in permissions.EnumerateArray())
                    {
                        grantNumber++;
                        var key = grant.ValueKind == JsonValueKind.Object ? Text(Property(grant, "key")) : null;
                        var level = grant.ValueKind == JsonValueKind.Object ? Text(Property(grant, "level")) : null;
                        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(level))
                        {
                            errors.Add($"Role {number}, permission {grantNumber}: write a key and a level.");
                            continue;
                        }

                        grants.Add(new ProductRoleGrant(key, level));
                    }
                }

                roles.Add(new ProductRoleToSet(name, string.IsNullOrEmpty(description) ? null : description, grants));
            }

            return (roles, errors);
        }
    }

    /// <summary>The roles as pretty JSON an admin can edit and send back, written the way the API reads them.</summary>
    public static string ToJson(IEnumerable<ProductRoleTemplate> roles) =>
        JsonSerializer.Serialize(
            new
            {
                roles = roles.Select(role => new
                {
                    name = role.Name,
                    description = role.Description,
                    permissions = role.Permissions.Select(grant => new { key = grant.Key, level = grant.Level }),
                }),
            },
            new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

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
