using System.Globalization;
using System.Text;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Observability;

/// <summary>
/// PLT-05: <c>/health/live</c> (the process is up), <c>/health/ready</c> (it can serve traffic) and <c>/metrics</c> (Prometheus text).
/// None of them needs a login, as the platform's probes cannot send one. The two health endpoints say nothing beyond ok or failed; the
/// metrics are restricted to the operator's networks (see <see cref="ObservabilityOptions"/>).
/// </summary>
public static class ObservabilityEndpoints
{
    public const string LiveRoute = "/health/live";
    public const string ReadyRoute = "/health/ready";
    public const string MetricsRoute = "/metrics";

    public static IEndpointRouteBuilder MapObservability(this IEndpointRouteBuilder app)
    {
        app.MapGet(LiveRoute, GetLive).AllowAnonymous();
        app.MapGet(ReadyRoute, GetReadyAsync).AllowAnonymous();
        app.MapGet(MetricsRoute, GetMetricsAsync).AllowAnonymous().AddEndpointFilter<InternalNetworkFilter>();
        return app;
    }

    private static IResult GetLive(HttpContext httpContext)
    {
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { status = "alive" });
    }

    private static async Task<IResult> GetReadyAsync(ReadinessService readiness, HttpContext httpContext, CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        var report = await readiness.CheckAsync(cancellationToken);
        var body = new
        {
            status = report.IsReady ? "ready" : "unavailable",
            checks = new { database = Word(report.Database), signingKey = Word(report.SigningKey) },
        };

        return Results.Json(body, statusCode: report.IsReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);

        static string Word(bool passed) => passed ? "ok" : "failed";
    }

    private static async Task<IResult> GetMetricsAsync(
        AuthMetrics metrics,
        AuthDbContext db,
        SigningKeyService signingKeys,
        TimeProvider clock,
        ILogger<AuthMetrics> logger,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        var text = new StringBuilder();
        metrics.WriteTo(text);

        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            WriteGauge(text, "auth_process_start_time_seconds", "When the process started, in Unix time.",
                [new(null, new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeSeconds())]);
        }

        WriteGauge(text, "auth_signing_keys_published", "Signing keys in the JWKS right now.", [new(null, signingKeys.GetPublishedKeys().Count)]);

        if (signingKeys.CurrentKeyAge is { } age)
        {
            WriteGauge(text, "auth_signing_key_age_seconds", "How long the key that signs now has been signing.", [new(null, age.TotalSeconds)]);
        }

        // What is waiting in the outboxes comes from the database. If it can't be read the rest of the metrics are still worth sending.
        try
        {
            var now = clock.GetUtcNow();

            var emails = await db.EmailOutbox.AsNoTracking()
                .Where(message => message.Status == EmailOutboxStatus.Pending || message.Status == EmailOutboxStatus.Failed)
                .GroupBy(message => message.Status)
                .Select(group => new { Status = group.Key, Count = group.Count(), Oldest = group.Min(message => message.CreatedAt) })
                .ToListAsync(cancellationToken);

            var webhooks = await db.WebhookOutbox.AsNoTracking()
                .Where(message => message.Status == WebhookStatus.Pending || message.Status == WebhookStatus.Failed)
                .GroupBy(message => message.Status)
                .Select(group => new { Status = group.Key, Count = group.Count(), Oldest = group.Min(message => message.CreatedAt) })
                .ToListAsync(cancellationToken);

            WriteGauge(text, "auth_email_outbox_messages", "Emails waiting to be sent (pending) or given up on (failed).",
            [
                new("pending", emails.FirstOrDefault(row => row.Status == EmailOutboxStatus.Pending)?.Count ?? 0),
                new("failed", emails.FirstOrDefault(row => row.Status == EmailOutboxStatus.Failed)?.Count ?? 0),
            ]);
            WriteGauge(text, "auth_email_outbox_oldest_pending_age_seconds", "How long the oldest unsent email has waited.",
                [new(null, Age(emails.FirstOrDefault(row => row.Status == EmailOutboxStatus.Pending)?.Oldest, now))]);

            WriteGauge(text, "auth_webhook_outbox_messages", "Webhook events waiting to be delivered (pending) or given up on (failed).",
            [
                new("pending", webhooks.FirstOrDefault(row => row.Status == WebhookStatus.Pending)?.Count ?? 0),
                new("failed", webhooks.FirstOrDefault(row => row.Status == WebhookStatus.Failed)?.Count ?? 0),
            ]);
            WriteGauge(text, "auth_webhook_outbox_oldest_pending_age_seconds", "How long the oldest undelivered webhook event has waited.",
                [new(null, Age(webhooks.FirstOrDefault(row => row.Status == WebhookStatus.Pending)?.Oldest, now))]);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The outbox metrics could not be read from the database.");
        }

        return Results.Text(text.ToString(), "text/plain; version=0.0.4; charset=utf-8");
    }

    private static double Age(DateTimeOffset? since, DateTimeOffset now) => since is { } start ? Math.Max(0, (now - start).TotalSeconds) : 0;

    private static void WriteGauge(StringBuilder text, string name, string help, IReadOnlyList<Sample> samples)
    {
        text.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        text.Append("# TYPE ").Append(name).AppendLine(" gauge");
        foreach (var sample in samples)
        {
            text.Append(name);
            if (sample.Status is not null)
            {
                text.Append("{status=\"").Append(sample.Status).Append("\"}");
            }

            text.Append(' ').AppendLine(sample.Value.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private sealed record Sample(string? Status, double Value);

    /// <summary>Answers a plain 404 to anyone outside the networks allowed to read the metrics, so the endpoint does not show itself.</summary>
    private sealed class InternalNetworkFilter(IOptions<ObservabilityOptions> options) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
            options.Value.IsAllowed(context.HttpContext.Connection.RemoteIpAddress)
                ? next(context)
                : ValueTask.FromResult<object?>(Results.NotFound());
    }
}
