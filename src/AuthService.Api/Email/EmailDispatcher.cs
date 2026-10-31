using Microsoft.Extensions.Options;

namespace AuthService.Api.Email;

/// <summary>Background worker: every few seconds, sends the emails that are due. Runs outside any request.</summary>
public sealed class EmailDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<EmailOptions> options,
    ILogger<EmailDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read here, not at registration, so configuration added after startup (test overrides) still applies.
        var settings = options.Value;
        if (!settings.DispatcherEnabled)
        {
            logger.LogInformation("The email dispatcher is disabled; queued emails will wait.");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(10, settings.DispatchIntervalMilliseconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EmailDeliveryService>().DeliverDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // For example the database is briefly unreachable. Nothing is lost: the emails stay queued.
                logger.LogError(exception, "Delivering queued email failed; trying again shortly.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
