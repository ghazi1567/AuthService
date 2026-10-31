using System.Text.Json;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Email;

/// <summary>
/// Sends the emails that are due. A failed email is retried with growing delays and logged; after the last attempt it is
/// marked failed. Logs mention only the message id, template and attempt number, never the address, a token or a link.
/// </summary>
public sealed class EmailDeliveryService(
    AuthDbContext db,
    IEmailSender sender,
    IOptions<EmailOptions> options,
    TimeProvider timeProvider,
    ILogger<EmailDeliveryService> logger)
{
    private const int BatchLimit = 50;

    /// <summary>Sends every email that is due now, up to a batch limit, and returns how many it handled.</summary>
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

        // SKIP LOCKED: if several instances run, each takes a different message, so none is sent twice.
        var message = (await db.EmailOutbox
                .FromSql($"""
                    SELECT * FROM email_outbox
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

        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(message.PayloadJson ?? "{}")!;
            await sender.SendAsync(EmailTemplates.Render(message.Template, message.ToAddress, data, settings), cancellationToken);

            message.Attempts++;
            message.Status = EmailOutboxStatus.Sent;
            message.SentAt = timeProvider.GetUtcNow();
            message.LastError = null;
            message.PayloadJson = null;
            logger.LogInformation("Email {OutboxId} ({Template}) sent after {Attempts} attempt(s).", message.Id, message.Template, message.Attempts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            message.Attempts++;
            message.LastError = exception is EmailSendException ? exception.Message : exception.GetType().Name;

            if (message.Attempts >= settings.MaxAttempts)
            {
                message.Status = EmailOutboxStatus.Failed;
                message.PayloadJson = null;
                logger.LogError(
                    "Email {OutboxId} ({Template}) failed for good after {Attempts} attempt(s): {Error}",
                    message.Id, message.Template, message.Attempts, message.LastError);
            }
            else
            {
                var delays = settings.RetryDelaySeconds ?? EmailOptions.DefaultRetryDelaySeconds;
                var delay = delays.Length == 0 ? 0 : delays[Math.Min(message.Attempts - 1, delays.Length - 1)];
                message.NextAttemptAt = timeProvider.GetUtcNow().AddSeconds(delay);
                logger.LogWarning(
                    "Email {OutboxId} ({Template}) attempt {Attempts} failed, will retry in {DelaySeconds}s: {Error}",
                    message.Id, message.Template, message.Attempts, delay, message.LastError);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return true;
    }
}
