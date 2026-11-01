namespace AuthService.Api.Features.Webhooks;

/// <summary>Settings for webhook delivery (SUB-06), bound from <c>Auth:Webhooks</c>.</summary>
public sealed class WebhookOptions
{
    public const string SectionName = "Auth:Webhooks";

    /// <summary>How long delivery of one event is retried before it is given up on as Failed. 24 hours.</summary>
    public int RetryWindowHours { get; set; } = 24;

    /// <summary>The delays used when <see cref="RetryDelaySeconds"/> is not set: 1 min, 5 min, 30 min, 2 h, 6 h, then 12 h.</summary>
    public static readonly int[] DefaultRetryDelaySeconds = [60, 300, 1800, 7200, 21600, 43200];

    /// <summary>
    /// Seconds to wait before the next try, indexed by how many attempts have failed so far; the last value repeats. Null means
    /// <see cref="DefaultRetryDelaySeconds"/>. The default lives in code because configuration binding appends to arrays.
    /// </summary>
    public int[]? RetryDelaySeconds { get; set; }

    /// <summary>How long to wait for a product to answer one request.</summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>When false the background worker does not run, so events wait. For tools such as migrations.</summary>
    public bool DispatcherEnabled { get; set; } = true;

    /// <summary>How often the worker looks for events to deliver.</summary>
    public int DispatchIntervalMilliseconds { get; set; } = 2000;

    /// <summary>How long to wait before looking again at an event whose product has registered no callback URL yet.</summary>
    public int NoUrlRecheckSeconds { get; set; } = 300;

    /// <summary>
    /// Allows callback URLs that are plain <c>http</c> or point at private, loopback or link-local addresses. Off in production,
    /// where a callback must be a public https address, so a product cannot make the service call into its own network.
    /// </summary>
    public bool AllowPrivateAddresses { get; set; }
}
