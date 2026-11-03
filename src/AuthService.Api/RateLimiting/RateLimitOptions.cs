namespace AuthService.Api.RateLimiting;

/// <summary>Where the rate-limit counters are kept (PLT-04), bound from <c>Auth:RateLimit</c>.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "Auth:RateLimit";

    public const string Postgres = "Postgres";

    public const string Memory = "Memory";

    /// <summary>
    /// <c>Postgres</c> (the default): counters shared by every instance, in the service's own database. <c>Memory</c>: counters in
    /// each process, for development, tests and a single instance.
    /// </summary>
    public string Store { get; set; } = Postgres;

    /// <summary>How often counters whose windows are over are removed.</summary>
    public int CleanupSeconds { get; set; } = 60;

    public bool UsesPostgres => string.Equals(Store, Postgres, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reasons the settings are unusable, so a typo stops the deployment instead of quietly using the wrong store.</summary>
    public IEnumerable<string> Problems()
    {
        if (!UsesPostgres && !string.Equals(Store, Memory, StringComparison.OrdinalIgnoreCase))
        {
            yield return $"Auth:RateLimit:Store is '{Store}', which must be '{Postgres}' or '{Memory}'.";
        }

        if (CleanupSeconds < 1)
        {
            yield return "Auth:RateLimit:CleanupSeconds must be at least 1.";
        }
    }
}
