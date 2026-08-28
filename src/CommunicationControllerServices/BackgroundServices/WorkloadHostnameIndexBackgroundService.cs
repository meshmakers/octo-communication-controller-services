using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Keeps the HTTP activator's hostname index fresh (AB#4923).
///
/// The index is what lets the activator attribute an inbound request to a workload from nothing but
/// its Host header. It is built once at startup and rebuilt on this interval, which covers workloads
/// deployed on another controller pod and hostname edits made directly on the entity.
///
/// Runs only while the activator is enabled — with the feature off the index would be built for
/// nobody. The first build happens immediately rather than after a grace period: a controller that
/// restarts while a workload is hibernated would otherwise answer its wake-up requests with 404
/// until the first tick.
/// </summary>
internal class WorkloadHostnameIndexBackgroundService(
    IWorkloadHostnameIndex hostnameIndex,
    IOptions<CommunicationControllerOptions> options) : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ActivatorEnabled)
        {
            Logger.Info("HTTP activator is disabled; not indexing workload hostnames");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ActivatorHostnameRefreshIntervalMinutes));
        Logger.Info("HTTP activator hostname index starting with a refresh interval of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await hostnameIndex.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // RefreshAsync already contains per-tenant error handling; anything reaching here is
                // unexpected. Keep the loop alive — the previous index stays served.
                Logger.Error(e, "Failed to refresh the activator hostname index");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
