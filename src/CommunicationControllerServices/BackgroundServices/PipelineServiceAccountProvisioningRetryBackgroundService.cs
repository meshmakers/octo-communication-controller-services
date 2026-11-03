using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Drives the background retry of adapter pipeline-service-account provisioning (AB#8145). The
/// startup sweep is single-shot; when identity restarts together with the controller (a release) the
/// adapters whose request timed out are remembered by
/// <see cref="IPipelineServiceAccountProvisioningService" /> and retried here with backoff. The
/// backoff itself lives in the service; this loop only wakes it up every few seconds, and does
/// nothing while no adapter is pending.
/// </summary>
internal class PipelineServiceAccountProvisioningRetryBackgroundService(
    IPipelineServiceAccountProvisioningService provisioningService) : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>How often the service is asked whether an adapter's backoff has elapsed.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    if (provisioningService.PendingRetryCount > 0)
                    {
                        await provisioningService.RetryPendingAsync(stoppingToken);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Logger.Error(e, "Error retrying pipeline service account provisioning");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }
}
