using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Periodically reconciles deployment sites that are persisted as <c>Online</c> but are owned by no
/// live operator connection on this pod, marking them <c>Offline</c> (AB#6418).
///
/// Ownership lives only in memory (<c>IOperatorConnectionManager</c>), the communication state in
/// MongoDB. The disconnect path (<c>OperatorHub.OnDisconnectedAsync</c>) resets what a connection
/// claimed — but a controller restart loses every claim, and the old pod deliberately skips the
/// Offline write while shutting down. A site whose operator never comes back (CR removed by hand,
/// edge device offline, operator reconnected without claiming the site) therefore stayed
/// <c>Online</c> in Studio while workload notifications for it were queued silently.
///
/// Safety: the sweep does not judge anything before <see cref="ReconciliationGrace"/> is over — one
/// interval after the last operator registered on this pod (at the latest three intervals after the
/// start, also when none ever registers). A fixed delay from the controller start is not enough: the operators reconnect on their
/// own schedule, and on test-2 they were more than a minute later than the old 5 minute grace, so every
/// Cloud site was written Offline shortly before the central operator claimed it again (and the
/// persisted-state metric published that as a critical alert). Ownership is re-checked right before
/// every write, and a site claimed later simply turns <c>Online</c> again through the normal
/// registration path.
/// </summary>
internal class DeploymentSiteOfflineReconciliationBackgroundService : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IAdapterCache _adapterCache;
    private readonly IDeploymentSiteService _deploymentSiteService;
    private readonly IShutdownState _shutdownState;
    private readonly IOperatorConnectionManager _operatorConnectionManager;
    private readonly TimeProvider _timeProvider;
    private readonly CommunicationControllerOptions _options;
    private readonly DateTimeOffset _startedAt;

    public DeploymentSiteOfflineReconciliationBackgroundService(
        IAdapterCache adapterCache,
        IDeploymentSiteService deploymentSiteService,
        IShutdownState shutdownState,
        IOperatorConnectionManager operatorConnectionManager,
        TimeProvider timeProvider,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _deploymentSiteService = deploymentSiteService;
        _shutdownState = shutdownState;
        _operatorConnectionManager = operatorConnectionManager;
        _timeProvider = timeProvider;
        _options = options.Value;
        _startedAt = timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = Interval;

        Logger.Info(
            "Deployment-site offline-reconciliation background service starting with interval / grace of {IntervalMinutes} minute(s); " +
            "the sweep waits for a quiet period after the last operator registration (at the latest {MaxWaitMinutes} minute(s) after the start)",
            interval.TotalMinutes, interval.TotalMinutes * ReconciliationGrace.MaxWaitIntervals);

        try
        {
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepIfSettledAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error reconciling orphaned online deployment sites");
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
        TimeSpan.FromMinutes(Math.Max(1, _options.DeploymentSiteOfflineReconciliationIntervalMinutes));

    /// <summary>
    /// One timer tick: sweeps once the operators have had their chance to reconnect and claim
    /// (<see cref="ReconciliationGrace"/>), otherwise waits for the next tick. Internal so tests can
    /// drive it without the timer.
    /// </summary>
    internal async Task SweepIfSettledAsync()
    {
        if (!ReconciliationGrace.IsOver(_timeProvider.GetUtcNow(), _startedAt,
                _operatorConnectionManager.LastOperatorRegisteredAt, Interval))
        {
            Logger.Info(
                "Deployment-site offline reconciliation waits: {State}",
                _operatorConnectionManager.LastOperatorRegisteredAt is null
                    ? "no operator has registered since the controller started yet"
                    : "an operator registered less than one interval ago and may still be claiming its sites");
            return;
        }

        await ReconcileAllTenantsAsync();
    }

    /// <summary>
    /// One sweep over all enabled tenants. Internal so tests can drive it without the timer.
    /// </summary>
    internal async Task ReconcileAllTenantsAsync()
    {
        if (_shutdownState.IsShuttingDown)
        {
            // The surviving pod is the authoritative state holder (same rule as the hub).
            return;
        }

        foreach (var tenantId in _adapterCache.GetEnabledTenantIds())
        {
            if (_shutdownState.IsShuttingDown)
            {
                // Shutdown began mid-sweep: leave the remaining tenants to the surviving pod.
                return;
            }

            try
            {
                var count = await _deploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync(tenantId);
                if (count > 0)
                {
                    Logger.Info(
                        "Reconciled {Count} orphaned online deployment site(s) to Offline for tenant '{TenantId}'",
                        count, tenantId);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error reconciling orphaned online deployment sites for tenant '{TenantId}'", tenantId);
            }
        }
    }
}
