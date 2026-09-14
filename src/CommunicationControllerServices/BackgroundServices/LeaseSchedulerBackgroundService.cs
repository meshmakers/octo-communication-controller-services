using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
///     Drains the adapter pool queues (AB#4924 §9, concept §5).
/// </summary>
/// <remarks>
///     <para>
///         Ticks on <c>LeaseSchedulerIntervalSeconds</c> — seconds, not minutes, because this
///         interval <i>is</i> the latency a borrower's work waits before it starts, and the
///         <c>Interactive</c> execution class is meaningless if the rotation turns on the cadence of
///         the cleanup service.
///     </para>
///     <para>
///         🔴 <b>The TTL reaper is not here.</b> It runs on
///         <c>ExecutionCleanupBackgroundService</c>'s cadence, next to the AB#4280 stuck reaper,
///         because it is the same kind of work — reclaiming something that should have been handed
///         back — and because a lease TTL is measured in minutes. Running it every few seconds would
///         walk every member's lease for nothing.
///     </para>
/// </remarks>
internal class LeaseSchedulerBackgroundService(
    ILeaseSchedulerService leaseScheduler,
    IShutdownState shutdownState,
    IOptions<CommunicationControllerOptions> options)
    : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaseSchedulerIntervalSeconds));

        Logger.Info(
            "Adapter pool lease scheduler starting with an interval of {IntervalSeconds}s and a topology refresh " +
            "of {TopologySeconds}s",
            interval.TotalSeconds, options.Value.LeaseTopologyRefreshSeconds);

        try
        {
            // Startup grace: pool members reconnect to this pod after a (re)start, and a round with
            // no members connected grants nothing and only produces noise.
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseTopologyRefreshSeconds)),
                stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Same rolling-upgrade stance the hubs take: a pod that is going away must not
                    // hand out leases it cannot supervise. The members reconnect to a surviving pod,
                    // and the work items stay Queued in the meantime — which is exactly what a queue
                    // is for.
                    if (!shutdownState.IsShuttingDown)
                    {
                        await leaseScheduler.RunSchedulingRoundAsync(stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Error in the adapter pool lease scheduling round");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        Logger.Info("Adapter pool lease scheduler stopped");
    }
}
