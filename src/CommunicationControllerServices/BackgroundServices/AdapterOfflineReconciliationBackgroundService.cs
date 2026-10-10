using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Periodically reconciles adapters that are persisted as <c>Online</c> but have no live SignalR
/// connection on this pod, marking them <c>Offline</c> (AB#4699).
///
/// The normal disconnect path (<c>AdapterHub.OnDisconnectedAsync</c>) already writes <c>Offline</c>
/// within SignalR's client-timeout window — except when the controller pod is shutting down, where
/// the rolling-upgrade race guard deliberately skips the write on the assumption that the adapter
/// reconnects to the surviving pod. If it never reconnects, the entity is stuck at a stale
/// <c>Online</c> with no live connection, which blocks config pushes and shows green in Studio.
/// This sweep closes that gap.
///
/// Safety: liveness is judged against <see cref="IAdapterConnectionTracker"/>, which — unlike the
/// config <see cref="IAdapterCache"/> — is not flushed by a tenant pre/post-update, so a
/// tracker-miss reliably means "no live connection". The first sweep waits a startup grace equal to
/// the configured interval so adapters can (re)connect after a controller (re)start or rolling
/// upgrade before any of them is judged orphaned — without it a fresh pod (empty tracker) would
/// offline every genuinely-connected adapter that has not yet reconnected. That grace is not a fixed
/// delay from the controller start but <see cref="ReconciliationGrace"/>: one interval after the last
/// adapter connected to this pod (at the latest three intervals after the start). A fixed 5 minutes was shorter than the
/// adapters' reconnect time on test-2 (AB#6418), so the cloud Mesh Adapters were written Offline
/// shortly before they registered again.
/// </summary>
internal class AdapterOfflineReconciliationBackgroundService : BackgroundService
{
    private readonly IAdapterCache _adapterCache;
    private readonly IAdapterService _adapterService;
    private readonly IAdapterConnectionTracker _connectionTracker;
    private readonly TimeProvider _timeProvider;
    private readonly CommunicationControllerOptions _options;
    private readonly DateTimeOffset _startedAt;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public AdapterOfflineReconciliationBackgroundService(
        IAdapterCache adapterCache,
        IAdapterService adapterService,
        IAdapterConnectionTracker connectionTracker,
        TimeProvider timeProvider,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _adapterService = adapterService;
        _connectionTracker = connectionTracker;
        _timeProvider = timeProvider;
        _options = options.Value;
        _startedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Interval;

        Logger.Info(
            "Adapter offline-reconciliation background service starting with interval / grace of {IntervalMinutes} minute(s); " +
            "the sweep waits for a quiet period after the last adapter connection (at the latest {MaxWaitMinutes} minute(s) after the start)",
            interval.TotalMinutes, interval.TotalMinutes * ReconciliationGrace.MaxWaitIntervals);

        try
        {
            // Startup grace: let adapters (re)connect after a controller (re)start or rolling
            // upgrade before judging any of them orphaned (ticks before the grace is over do nothing).
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepIfSettledAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error reconciling orphaned online adapters");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private TimeSpan Interval =>
        TimeSpan.FromMinutes(Math.Max(1, _options.AdapterOfflineReconciliationIntervalMinutes));

    /// <summary>
    /// One timer tick: sweeps once the adapters have had their chance to reconnect
    /// (<see cref="ReconciliationGrace"/>), otherwise waits for the next tick. Internal so tests can
    /// drive it without the timer.
    /// </summary>
    internal async Task SweepIfSettledAsync()
    {
        if (!ReconciliationGrace.IsOver(_timeProvider.GetUtcNow(), _startedAt, _connectionTracker.LastConnectedAt,
                Interval))
        {
            Logger.Info(
                "Adapter offline reconciliation waits: {State}",
                _connectionTracker.LastConnectedAt is null
                    ? "no adapter has connected since the controller started yet"
                    : "an adapter connected less than one interval ago; others may still be reconnecting");
            return;
        }

        await ReconcileAllTenantsAsync();
    }

    internal async Task ReconcileAllTenantsAsync()
    {
        var tenantIds = _adapterCache.GetEnabledTenantIds();
        foreach (var tenantId in tenantIds)
        {
            try
            {
                var count = await _adapterService.ReconcileOrphanedOnlineAdaptersAsync(tenantId);
                if (count > 0)
                {
                    Logger.Info(
                        "Reconciled {Count} orphaned online adapter(s) to Offline for tenant '{TenantId}'",
                        count, tenantId);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reconciling orphaned online adapters for tenant '{TenantId}'", tenantId);
            }
        }
    }
}
