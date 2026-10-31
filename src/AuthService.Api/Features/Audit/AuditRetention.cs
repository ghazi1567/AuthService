using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Audit;

public sealed class AuditOptions
{
    public const string SectionName = "Auth:Audit";

    /// <summary>How long audit entries are kept before they are deleted. One year (AUD-01).</summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>When false the retention worker does not run, so nothing is deleted automatically. For tools and tests.</summary>
    public bool RetentionEnabled { get; set; } = true;

    /// <summary>How often the retention job runs. Daily by default.</summary>
    public int RetentionCheckSeconds { get; set; } = 86_400;
}

/// <summary>Deletes audit entries older than the retention period: the only deletion the database allows.</summary>
public sealed class AuditRetention(AuthDbContext db, IOptions<AuditOptions> options)
{
    /// <summary>Deletes every entry older than the retention period, and nothing newer. Returns how many it removed.</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-options.Value.RetentionDays);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lets the append-only trigger allow a DELETE. "true" makes it last only until this transaction ends.
        await db.Database.ExecuteSqlRawAsync("SELECT set_config('auth.audit_purge', 'on', true)", cancellationToken);

        var removed = await db.AuditEntries
            .Where(entry => entry.OccurredAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return removed;
    }
}

/// <summary>Background worker: runs the retention job once a day, so old audit entries disappear without anyone asking.</summary>
public sealed class AuditRetentionWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<AuditOptions> options,
    TimeProvider timeProvider,
    ILogger<AuditRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read here, not at registration, so configuration added after startup (test overrides) still applies.
        var settings = options.Value;
        if (!settings.RetentionEnabled)
        {
            logger.LogInformation("The audit retention worker is disabled; old entries will not be deleted automatically.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.RetentionCheckSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<AuditRetention>()
                    .PurgeAsync(timeProvider.GetUtcNow(), stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation("Audit retention removed {Removed} entries older than {RetentionDays} days.", removed, options.Value.RetentionDays);
                }

                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // For example the database is briefly unreachable. Nothing is lost: the job runs again.
                logger.LogError(exception, "The audit retention job failed; trying again later.");

                try
                {
                    await Task.Delay(interval < TimeSpan.FromHours(1) ? interval : TimeSpan.FromHours(1), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
