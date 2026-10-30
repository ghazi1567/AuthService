namespace AuthService.Api.Common;

public static class EmailNormalizer
{
    /// <summary>Emails are stored trimmed and lowercased; this is the only place that rule lives.</summary>
    public static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
