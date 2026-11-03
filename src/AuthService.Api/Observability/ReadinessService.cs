using AuthService.Api.Tokens;
using AuthService.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Api.Observability;

/// <summary>What the readiness check found: one flag per check, and nothing about why a check failed.</summary>
public sealed record ReadinessReport(bool Database, bool SigningKey)
{
    public bool IsReady => Database && SigningKey;
}

/// <summary>
/// PLT-05: can this instance serve traffic? It needs a reachable database that has all its migrations (they are applied by hand, so a
/// new version started before its migration would otherwise fail on the first request), and a signing key that signs right now
/// (without one no login can succeed). A failed check is logged in full here; the response says only which check failed.
/// </summary>
public sealed class ReadinessService(AuthDbContext db, SigningKeyService signingKeys, ILogger<ReadinessService> logger)
{
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(5);

    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken)
    {
        var database = await DatabaseIsReadyAsync(cancellationToken);
        var signingKey = signingKeys.HasCurrentKey;

        if (!signingKey)
        {
            logger.LogWarning("Not ready: there is no signing key that signs right now.");
        }

        return new ReadinessReport(database, signingKey);
    }

    private async Task<bool> DatabaseIsReadyAsync(CancellationToken cancellationToken)
    {
        // A database that accepts the connection and then never answers must not hold the probe open.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DatabaseTimeout);

        try
        {
            var pending = (await db.Database.GetPendingMigrationsAsync(timeout.Token)).Count();
            if (pending > 0)
            {
                logger.LogWarning("Not ready: the database is missing {Count} migrations.", pending);
                return false;
            }

            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Not ready: the database could not be reached.");
            return false;
        }
    }
}
