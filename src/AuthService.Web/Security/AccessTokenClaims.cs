using System.Text.Json;

namespace AuthService.Web.Security;

/// <summary>
/// Reads who an access token names. The web app got the token straight from the API over its own connection, so it does not check the
/// signature here (the API does on every call); it only needs the person's id to put on the session.
/// </summary>
public static class AccessTokenClaims
{
    /// <summary>The <c>sub</c> claim as an id, or null when the token is not shaped like a token with one.</summary>
    public static Guid? ReadUserId(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var padded = parts[1].Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            using var payload = JsonDocument.Parse(Convert.FromBase64String(padded));
            return payload.RootElement.ValueKind == JsonValueKind.Object
                && payload.RootElement.TryGetProperty("sub", out var sub)
                && sub.ValueKind == JsonValueKind.String
                && Guid.TryParse(sub.GetString(), out var userId)
                    ? userId
                    : null;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }
}
