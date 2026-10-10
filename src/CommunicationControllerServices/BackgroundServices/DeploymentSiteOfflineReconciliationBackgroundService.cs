using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
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
/// Safety: the first sweep waits a startup grace equal to the configured interval so the operators
/// (re)connect and claim their sites before any of them is judged orphaned — in particular the
/// central operator, which re-creates the Cloud site CRs from <c>RegisterOperatorAsync</c>.
/// Ownership is re-checked right before every write, and a site claimed later simply turns
/// <c>Online</c> again through the normal registration path.
/// </summary>
internal class DeploymentSiteOfflineReconciliationBackgroundService : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IAdapterCache _adapterCache;
    private readonly IDeploymentSiteService _deploymentSiteService;
    private readonly IShutdownState _shutdownState;
    private readonly CommunicationControllerOptions _options;

    public DeploymentSiteOfflineReconciliationBackgroundService(
        IAdapterCache adapterCache,
        IDeploymentSiteService deploymentSiteService,
        IShutdownState shutdownState,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _deploymentSiteService = deploymentSiteService;
        _shutdownState = shutdownState;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.DeploymentSiteOfflineReconciliationIntervalMinutes));

        Logger.Info(
            "Deployment-site offline-reconciliation background service starting with interval / startup grace of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        try
        {
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAllTenantsAsync();
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
