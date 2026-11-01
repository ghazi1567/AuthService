using System.Text;
using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Webhooks;

/// <summary>
/// Delivers the webhooks that are due (SUB-06). Each is signed with the product's secret and posted to its callback URL. A failure
/// is retried with growing delays until the event's deadline (24 hours), where one last attempt is made, and then it is marked
/// Failed for a platform admin to see and resend. Delivery is at-least-once: a product that answered after the service gave up
/// waiting may get the same event again, so it must deduplicate on the event id. Logs name only ids, never the URL or a body.
/// </summary>
public sealed class WebhookDeliveryService(
    AuthDbContext db,
    IHttpClientFactory httpClientFactory,
    KeyEncryption keyEncryption,
    IOptions<WebhookOptions> options,
    TimeProvider timeProvider,
    ILogger<WebhookDeliveryService> logger)
{
    private const int BatchLimit = 50;

    /// <summary>Delivers every event that is due now, up to a batch limit, and returns how many it handled.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken cancellationToken)
    {
        var handled = 0;
        while (handled < BatchLimit && await DeliverNextAsync(cancellationToken))
        {
            handled++;
        }

        return handled;
    }

    private async Task<bool> DeliverNextAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // SKIP LOCKED: if several instances run, each takes a different event, so none is delivered twice at the same moment.
        var message = (await db.WebhookOutbox
                .FromSql($"""
                    SELECT * FROM webhook_outbox
                    WHERE status = 'Pending' AND next_attempt_at <= {now}
                    ORDER BY created_at, id
                    LIMIT 1
                    FOR UPDATE SKIP LOCKED
                    """)
                .ToListAsync(cancellationToken))
            .SingleOrDefault();

        if (message is null)
        {
            return false;
        }

        var application = await db.Applications.AsNoTracking()
            .Where(candidate => candidate.Id == message.ApplicationId)
            .Select(candidate => new { candidate.WebhookUrl, candidate.WebhookSecretProtected })
            .SingleAsync(cancellationToken);

        if (application.WebhookUrl is null || application.WebhookSecretProtected is null)
        {
            // Nowhere to send it yet. Not an attempt: look again later, and give up only when the 24 hours are over.
            if (now >= message.DeadlineAt)
            {
                message.Status = WebhookStatus.Failed;
                message.LastError = "No callback URL was registered in time.";
                logger.LogError("Webhook {EventId} ({EventType}) failed: its product registered no callback URL in time.", message.Id, message.EventType);
            }
            else
            {
                message.NextAttemptAt = Earlier(now.AddSeconds(Math.Max(1, settings.NoUrlRecheckSeconds)), message.DeadlineAt);
            }
        }
        else
        {
            var error = await TrySendAsync(message, application.WebhookUrl, application.WebhookSecretProtected, now, cancellationToken);
            message.Attempts++;

            if (error is null)
            {
                message.Status = WebhookStatus.Delivered;
                message.DeliveredAt = timeProvider.GetUtcNow();
                message.LastError = null;
                logger.LogInformation("Webhook {EventId} ({EventType}) delivered after {Attempts} attempt(s).", message.Id, message.EventType, message.Attempts);
            }
            else
            {
                message.LastError = error;
                var finishedAt = timeProvider.GetUtcNow();
                if (finishedAt >= message.DeadlineAt)
                {
                    message.Status = WebhookStatus.Failed;
                    logger.LogError(
                        "Webhook {EventId} ({EventType}) failed for good after {Attempts} attempt(s): {Error}",
                        message.Id, message.EventType, message.Attempts, error);
                }
                else
                {
                    var delays = settings.RetryDelaySeconds ?? WebhookOptions.DefaultRetryDelaySeconds;
                    var delay = delays.Length == 0 ? 0 : delays[Math.Min(message.Attempts - 1, delays.Length - 1)];

                    // The last attempt is made at the deadline itself, never after it.
                    message.NextAttemptAt = Earlier(finishedAt.AddSeconds(delay), message.DeadlineAt);
                    logger.LogWarning(
                        "Webhook {EventId} ({EventType}) attempt {Attempts} failed, will retry in {DelaySeconds}s: {Error}",
                        message.Id, message.EventType, message.Attempts, delay, error);
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>Posts the event. Returns null on success (any 2xx), otherwise a short code for what went wrong.</summary>
    private async Task<string?> TrySendAsync(
        WebhookOutboxMessage message, string url, string protectedSecret, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            var secret = Encoding.UTF8.GetString(keyEncryption.Decrypt(protectedSecret));
            var timestamp = now.ToUnixTimeSeconds();

            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(message.PayloadJson, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("User-Agent", "AuthService-Webhooks/1");
            request.Headers.TryAddWithoutValidation("X-Auth-Event-Id", message.Id.ToString());
            request.Headers.TryAddWithoutValidation("X-Auth-Event-Type", message.EventType);
            request.Headers.TryAddWithoutValidation("X-Auth-Timestamp", timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("X-Auth-Signature", WebhookSigner.Sign(secret, timestamp, message.PayloadJson));

            using var client = httpClientFactory.CreateClient(WebhookHttp.ClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return response.IsSuccessStatusCode ? null : $"HTTP {(int)response.StatusCode}";
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the kind of failure is kept: an exception message can contain the URL.
            return exception is TaskCanceledException ? "Timed out" : exception.GetType().Name;
        }
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
