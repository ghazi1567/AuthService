namespace AuthService.Api.Email;

/// <summary>Settings for sending email, bound from the <c>Email</c> configuration section.</summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>
    /// Which provider sends mail: <c>Brevo</c>, <c>File</c> (writes each email to a file) or <c>InMemory</c> (tests).
    /// Switching provider is a configuration change only. When unset, Development uses File and Testing uses InMemory;
    /// every other environment must set it.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>The sender address used when none is configured. Fine for File and InMemory, never for a real provider.</summary>
    public const string DefaultFromAddress = "no-reply@localhost";

    public string? FromAddress { get; set; } = DefaultFromAddress;

    public string FromName { get; set; } = "Auth Service";

    /// <summary>The address links in emails point at, for example the web app that handles <c>/verify-email</c>.</summary>
    public string PublicBaseUrl { get; set; } = "http://localhost:5080";

    /// <summary>Brevo API key. A secret: pass it as <c>Email__BrevoApiKey</c>, never in a committed file.</summary>
    public string? BrevoApiKey { get; set; }

    /// <summary>Where the File provider writes emails. Defaults to <c>artifacts/emails</c> under the content root.</summary>
    public string? FileDirectory { get; set; }

    /// <summary>When false the background worker does not run, so queued emails wait. For tools such as migrations.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>How often the worker looks for emails to send.</summary>
    public int DispatchIntervalMilliseconds { get; set; } = 2000;

    /// <summary>How many times to try before giving up on an email.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>The delays used when <see cref="RetryDelaySeconds"/> is not set.</summary>
    public static readonly int[] DefaultRetryDelaySeconds = [30, 120, 600, 3600];

    /// <summary>
    /// Seconds to wait before the next try, indexed by how many attempts have failed so far. The last value repeats.
    /// Null means <see cref="DefaultRetryDelaySeconds"/>. The default lives in code, not here, because configuration
    /// binding appends to an array's existing values instead of replacing them.
    /// </summary>
    public int[]? RetryDelaySeconds { get; set; }
}
