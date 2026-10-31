using Microsoft.Extensions.Options;

namespace AuthService.Api.Tokens;

/// <summary>
/// Background worker: every minute or so, reloads the signing keys and, when it is time, schedules the next one. Keeps every
/// instance in step with the database, so a key published by one instance is known to all of them long before it signs.
/// </summary>
public sealed class SigningKeyWorker(
    SigningKeyService keys,
    IOptions<IdentityTokenOptions> options,
    ILogger<SigningKeyWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SigningKeyRefreshSeconds));

        // The first load happened at startup, so wait before the next one.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                await keys.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // For example the database is briefly unreachable. The keys already in memory keep working meanwhile.
                logger.LogError(exception, "Refreshing the signing keys failed; trying again shortly.");
            }
        }
    }
}
