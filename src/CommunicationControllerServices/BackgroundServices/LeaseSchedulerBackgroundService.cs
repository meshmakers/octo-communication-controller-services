using System.Diagnostics;
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
///         🔴 <b>The tick is a floor, not the cadence (AB#4924 §9.6).</b> It used to be both, and
///         that made it the throughput ceiling: grants came exactly one interval apart — 5.11–5.14 s
///         measured, for runs of 0.43–1.36 s — so a member sat idle 75–92 % of the time and could not
///         exceed roughly twelve executions a minute however small the work was. A lease release and
///         a work enqueue now pull the next round forward through
///         <see cref="ILeaseSchedulerWakeSignal" />; the tick remains as the safety net for
///         everything no signal reaches (TTL expiry, topology changes, and an event that happened on
///         a different controller pod).
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
    ILeaseSchedulerWakeSignal wakeSignal,
    IShutdownState shutdownState,
    IOptions<CommunicationControllerOptions> options)
    : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.LeaseSchedulerIntervalSeconds));
        var minimumGap =
            TimeSpan.FromMilliseconds(Math.Max(0, options.Value.LeaseSchedulerWakeMinIntervalMilliseconds));

        Logger.Info(
            "Adapter pool lease scheduler starting with an interval of {IntervalSeconds}s, a minimum gap of " +
            "{MinimumGapMs}ms between requested rounds and a topology refresh of {TopologySeconds}s",
            interval.TotalSeconds, minimumGap.TotalMilliseconds, options.Value.LeaseTopologyRefreshSeconds);

        try
        {
            // Startup grace: pool members reconnect to this pod after a (re)start, and a round with
            // no members connected grants nothing and only produces noise.
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, options.Value.LeaseTopologyRefreshSeconds)),
                stoppingToken);

            // Stopwatch, not the wall clock and not an injected TimeProvider: this measures a gap,
            // so it must be monotonic — a clock step during an NTP correction would otherwise either
            // skip the floor or stall the scheduler for the length of the jump. TimeProvider is not
            // registered anywhere in this service, and injecting it would be a startup failure the
            // compiler cannot see.
            var lastRoundStarted = Stopwatch.GetTimestamp();

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
                        lastRoundStarted = Stopwatch.GetTimestamp();
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

                // Interval OR a requested round, whichever comes first. A request that arrives while
                // the round above is still running is not lost: the signal is a pending permit, not
                // an event, so it is taken here immediately.
                var requested = await wakeSignal.WaitForRoundAsync(interval, stoppingToken);
                if (!requested)
                {
                    continue;
                }

                // 🔴 The floor a requested round has to respect. A round reads the queue of every
                // borrower of every pool, so without it a burst of enqueues — or a pool whose members
                // release continuously — would run rounds back to back and make the scheduler the
                // most expensive thing in the controller, which is the outcome the interval was
                // chosen to avoid in the first place. Measured from the START of the previous round,
                // so a long round is never followed by an additional wait it has already served.
                var sinceLastRound = Stopwatch.GetElapsedTime(lastRoundStarted);
                if (sinceLastRound < minimumGap)
                {
                    await Task.Delay(minimumGap - sinceLastRound, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        Logger.Info("Adapter pool lease scheduler stopped");
    }
}
