namespace AuthService.Api.Email;

/// <summary>
/// Says how long a link in an email works, in the words the email uses. The three emails with a link (verification, password reset,
/// invitation) state the lifetime that was configured when the link was made, so what a person reads is what the service will do.
/// </summary>
public static class EmailLifetime
{
    /// <summary>The key under which the lifetime travels with the queued email.</summary>
    public const string DataKey = "validFor";

    /// <summary>The largest unit that divides the lifetime exactly: 48 hours is "2 days", 90 minutes stays "90 minutes", one hour is "1 hour".</summary>
    public static string Describe(TimeSpan lifetime)
    {
        var minutes = (long)Math.Round(lifetime.TotalMinutes);
        if (minutes > 0 && minutes % (24 * 60) == 0)
        {
            return Plural(minutes / (24 * 60), "day");
        }

        if (minutes > 0 && minutes % 60 == 0)
        {
            return Plural(minutes / 60, "hour");
        }

        return Plural(Math.Max(1, minutes), "minute");
    }

    private static string Plural(long count, string unit) => count == 1 ? $"1 {unit}" : $"{count} {unit}s";
}
