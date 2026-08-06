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
/// offline every genuinely-connected adapter that has not yet reconnected.
/// </summary>
internal class AdapterOfflineReconciliationBackgroundService : BackgroundService
{
    private readonly IAdapterCache _adapterCache;
    private readonly IAdapterService _adapterService;
    private readonly CommunicationControllerOptions _options;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public AdapterOfflineReconciliationBackgroundService(
        IAdapterCache adapterCache,
        IAdapterService adapterService,
        IOptions<CommunicationControllerOptions> options)
    {
        _adapterCache = adapterCache;
        _adapterService = adapterService;
        _options = options.Value;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.AdapterOfflineReconciliationIntervalMinutes));

        Logger.Info(
            "Adapter offline-reconciliation background service starting with interval / startup grace of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        try
        {
            // Startup grace: let adapters (re)connect after a controller (re)start or rolling
            // upgrade before judging any of them orphaned.
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAllTenantsAsync();
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

    private async Task ReconcileAllTenantsAsync()
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
