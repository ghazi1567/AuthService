using System.Linq.Expressions;
using AuthService.Api.Observability;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Cleanup;

/// <summary>
/// Settings of the cleanup (AUTH-270), section <c>Auth:Cleanup</c>. Each retention is counted from the moment a row stopped being usable
/// (used, expired, sent, delivered), never from when it was created, so nothing that still works is ever removed.
/// </summary>
public sealed class CleanupOptions
{
    public const string SectionName = "Auth:Cleanup";

    /// <summary>When false the worker does not run. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often a run starts. Hourly.</summary>
    public int IntervalSeconds { get; set; } = 3_600;

    /// <summary>Rows removed per statement, so no statement holds locks for long. 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>The most batches of one kind in one run, so a large backlog is worked off over several runs. 200 (100,000 rows).</summary>
    public int MaxBatchesPerKind { get; set; } = 200;

    /// <summary>
    /// Refresh tokens are removed this many days after they <b>expire</b>. A revoked token is kept until then too: if a stolen, already
    /// rotated token is replayed, finding it is what revokes the whole sign-in (reuse detection), so it must not vanish early.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>Email verification links are removed this many days after they were used or expired.</summary>
    public int VerificationTokenDays { get; set; } = 7;

    /// <summary>Password reset links are removed this many days after they were used or expired.</summary>
    public int ResetTokenDays { get; set; } = 7;

    /// <summary>Sent emails are removed this many days after they were sent, and emails given up on this many days after they were written.</summary>
    public int EmailOutboxDays { get; set; } = 14;

    /// <summary>Delivered webhook events are removed this many days after delivery.</summary>
    public int DeliveredWebhookDays { get; set; } = 14;

    /// <summary>
    /// Failed webhook events are removed this many days after their delivery deadline. Longer than delivered ones, because a platform
    /// admin can still send a failed event again from the console.
    /// </summary>
    public int FailedWebhookDays { get; set; } = 30;

    /// <summary>A product's previous client secret is removed this many days after it stopped working. The current secret never expires.</summary>
    public int ExpiredSecretDays { get; set; } = 7;
}

/// <summary>The kinds of row the cleanup removes, as they appear in logs and in the <c>auth_cleanup_rows_removed_total</c> metric.</summary>
public static class CleanupKinds
{
    public const string RefreshTokens = "refresh_tokens";
    public const string VerificationTokens = "email_verification_tokens";
    public const string ResetTokens = "password_reset_tokens";
    public const string EmailOutbox = "email_outbox";
    public const string DeliveredWebhooks = "webhook_outbox_delivered";
    public const string FailedWebhooks = "webhook_outbox_failed";
    public const string ExpiredSecrets = "application_secrets";
}

/// <summary>
/// Removes rows that are finished and older than their retention, in small batches (AUTH-270). Never the audit log, which has its own
/// retention (<c>AuditRetention</c>), and never a row that can still be used. Each batch runs in its own short transaction holding a
/// transaction-level advisory lock, so when several instances run at once only one deletes at a time and the others stop for this round.
/// </summary>
public sealed class DataCleanup(AuthDbContext db, IOptions<CleanupOptions> options, AuthMetrics metrics, ILogger<DataCleanup> logger)
{
    /// <summary>Any constant shared by every instance; only the cleanup takes it.</summary>
    private const long LockKey = 0x41_55_54_48_43_4C_4E; // "AUTHCLN"

    /// <summary>Runs every kind once. Returns how many rows each kind lost; a kind another instance was cleaning at the time is left out.</summary>
    public async Task<IReadOnlyDictionary<string, int>> RunAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var removed = new Dictionary<string, int>(StringComparer.Ordinal);

        var refreshCutoff = now.AddDays(-settings.RefreshTokenDays);
        var verificationCutoff = now.AddDays(-settings.VerificationTokenDays);
        var resetCutoff = now.AddDays(-settings.ResetTokenDays);
        var emailCutoff = now.AddDays(-settings.EmailOutboxDays);
        var deliveredCutoff = now.AddDays(-settings.DeliveredWebhookDays);
        var failedCutoff = now.AddDays(-settings.FailedWebhookDays);
        var secretCutoff = now.AddDays(-settings.ExpiredSecretDays);

        var kinds = new (string Kind, Func<int, CancellationToken, Task<int>> DeleteBatch)[]
        {
            (CleanupKinds.RefreshTokens, (size, ct) => DeleteAsync(db.RefreshTokens, token => token.ExpiresAt < refreshCutoff, token => token.Id, size, ct)),
            (CleanupKinds.VerificationTokens, (size, ct) => DeleteAsync(db.EmailVerificationTokens,
                token => token.ExpiresAt < verificationCutoff || token.UsedAt < verificationCutoff, token => token.Id, size, ct)),
            (CleanupKinds.ResetTokens, (size, ct) => DeleteAsync(db.PasswordResetTokens,
                token => token.ExpiresAt < resetCutoff || token.UsedAt < resetCutoff, token => token.Id, size, ct)),
            (CleanupKinds.EmailOutbox, (size, ct) => DeleteAsync(db.EmailOutbox,
                message => (message.Status == EmailOutboxStatus.Sent && message.SentAt < emailCutoff)
                    || (message.Status == EmailOutboxStatus.Failed && message.CreatedAt < emailCutoff),
                message => message.Id, size, ct)),
            (CleanupKinds.DeliveredWebhooks, (size, ct) => DeleteAsync(db.WebhookOutbox,
                message => message.Status == WebhookStatus.Delivered && message.DeliveredAt < deliveredCutoff, message => message.Id, size, ct)),
            (CleanupKinds.FailedWebhooks, (size, ct) => DeleteAsync(db.WebhookOutbox,
                message => message.Status == WebhookStatus.Failed && message.DeadlineAt < failedCutoff, message => message.Id, size, ct)),
            (CleanupKinds.ExpiredSecrets, (size, ct) => DeleteAsync(db.ApplicationSecrets,
                secret => secret.ExpiresAt != null && secret.ExpiresAt < secretCutoff, secret => secret.Id, size, ct)),
        };

        var batchSize = Math.Clamp(settings.BatchSize, 1, 10_000);
        foreach (var (kind, deleteBatch) in kinds)
        {
            var total = 0;
            for (var batch = 0; batch < Math.Max(1, settings.MaxBatchesPerKind); batch++)
            {
                var deleted = await InLockedTransactionAsync(ct => deleteBatch(batchSize, ct), cancellationToken);
                if (deleted is null)
                {
                    logger.LogInformation("Another instance is cleaning up; this one stops for this round.");
                    return removed;
                }

                total += deleted.Value;
                if (deleted.Value < batchSize)
                {
                    break;
                }
            }

            removed[kind] = total;
            if (total > 0)
            {
                metrics.RecordCleanup(kind, total);
                logger.LogInformation("Cleanup removed {Removed} {Kind} rows.", total, kind);
            }
        }

        return removed;
    }

    /// <summary>Runs <paramref name="work"/> in a transaction that holds the cleanup lock; null when another instance holds it.</summary>
    private async Task<int?> InLockedTransactionAsync(Func<CancellationToken, Task<int>> work, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({LockKey}) AS \"Value\"").SingleAsync(cancellationToken);
        if (!locked)
        {
            return null;
        }

        var deleted = await work(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <summary>Deletes at most <paramref name="batchSize"/> rows matching <paramref name="finished"/>, oldest ids first, in one statement.</summary>
    private static Task<int> DeleteAsync<T>(
        DbSet<T> rows, Expression<Func<T, bool>> finished, Expression<Func<T, Guid>> id, int batchSize, CancellationToken cancellationToken)
        where T : class =>
        rows.Where(finished).OrderBy(id).Take(batchSize).ExecuteDeleteAsync(cancellationToken);
}

/// <summary>Background worker: runs the cleanup once an interval, so finished rows disappear without anyone asking.</summary>
public sealed class DataCleanupWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<CleanupOptions> options,
    TimeProvider timeProvider,
    ILogger<DataCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read here, not at registration, so configuration added after startup (test overrides) still applies.
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The cleanup worker is disabled; finished tokens and outbox rows will not be removed automatically.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<DataCleanup>().RunAsync(timeProvider.GetUtcNow(), stoppingToken);
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // For example the database is briefly unreachable. Nothing is lost: the next run removes what this one did not.
                logger.LogError(exception, "The cleanup run failed; trying again later.");
                try
                {
                    await Task.Delay(interval < TimeSpan.FromMinutes(10) ? interval : TimeSpan.FromMinutes(10), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
