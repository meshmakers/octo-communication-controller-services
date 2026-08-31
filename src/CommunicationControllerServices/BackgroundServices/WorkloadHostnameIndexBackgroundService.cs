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
///
/// That immediate build usually finds nothing, because tenants reach the adapter cache a moment
/// after the host starts (observed on test-2: "rebuilt with 0 entries" at startup). So the service
/// polls every <see cref="WarmupInterval"/> for the first <see cref="WarmupWindow"/> before settling
/// on the configured interval — otherwise every controller restart leaves hibernated workloads
/// unreachable for a full refresh interval.
/// </summary>
internal class WorkloadHostnameIndexBackgroundService(
    IWorkloadHostnameIndex hostnameIndex,
    IOptions<CommunicationControllerOptions> options) : BackgroundService
{
    /// <summary>Refresh cadence while the host is still starting up.</summary>
    internal static readonly TimeSpan WarmupInterval = TimeSpan.FromSeconds(15);

    /// <summary>How long the faster cadence lasts before the configured interval takes over.</summary>
    internal static readonly TimeSpan WarmupWindow = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound for a single refresh; a blocked one must not wedge the loop.</summary>
    internal static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(60);

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ActivatorEnabled)
        {
            Logger.Info("HTTP activator is disabled; not indexing workload hostnames");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.ActivatorHostnameRefreshIntervalMinutes));
        var warmupUntil = DateTime.UtcNow + WarmupWindow;
        Logger.Info("HTTP activator hostname index starting with a refresh interval of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Bounded: a refresh that blocks — a repository call waiting on a lock held by
                // tenant startup, say — would otherwise wedge this loop for the process lifetime
                // and leave the activator permanently blind, with nothing in the log to say so.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(RefreshTimeout);
                await hostnameIndex.RefreshAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("Refreshing the activator hostname index timed out after {Seconds}s; retrying",
                    RefreshTimeout.TotalSeconds);
            }
            catch (Exception e)
            {
                // RefreshAsync already contains per-tenant error handling; anything reaching here is
                // unexpected. Keep the loop alive — the previous index stays served.
                Logger.Error(e, "Failed to refresh the activator hostname index");
            }

            try
            {
                await Task.Delay(DateTime.UtcNow < warmupUntil ? WarmupInterval : interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
