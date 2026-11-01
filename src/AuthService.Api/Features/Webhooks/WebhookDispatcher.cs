using Microsoft.Extensions.Options;

namespace AuthService.Api.Features.Webhooks;

/// <summary>Background worker: every few seconds, delivers the webhooks that are due. Runs outside any request.</summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopeFactory,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Read here, not at registration, so configuration added after startup (test overrides) still applies.
        var settings = options.Value;
        if (!settings.DispatcherEnabled)
        {
            logger.LogInformation("The webhook dispatcher is disabled; queued webhooks will wait.");
            return;
        }

        var interval = TimeSpan.FromMilliseconds(Math.Max(10, settings.DispatchIntervalMilliseconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<WebhookDeliveryService>().DeliverDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // For example the database is briefly unreachable. Nothing is lost: the events stay queued.
                logger.LogError(exception, "Delivering queued webhooks failed; trying again shortly.");
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
