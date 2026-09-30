using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;

/// <summary>
/// Publishes the persisted state of every adapter, application and pool as OTel gauges
/// (AB#5432, item 3) for the tenants that opted in via
/// <c>System/TenantModeConfiguration.PublishWorkloadObservability</c>.
///
/// <para>
/// Sweep-driven rather than event-driven, for the same two reasons as the pipeline statistics
/// gauges: the values come from persisted state, so they survive a controller restart instead of
/// starting from nothing, and they exist for an entity that has not changed state in weeks —
/// exactly the entity whose stuck <c>Error</c> has to be alertable. The freshness of every gauge is
/// therefore one sweep interval; the sweep-age gauge next to them is computed at collection time
/// and keeps rising in between, which is what makes this loop's own death visible.
/// </para>
///
/// <para>
/// A tenant that has not opted in is skipped silently — no log line per tenant per sweep. This is
/// the default state of almost every tenant, and a per-sweep info line for each of them is how a
/// log becomes unreadable. The opt-in read itself is one small query per tenant per sweep and
/// cannot fail the sweep (see <c>IsWorkloadObservabilityEnabledAsync</c>).
/// </para>
///
/// <para>
/// Structure mirrors <see cref="AdapterOfflineReconciliationBackgroundService"/> and
/// <see cref="WorkloadLifecycleWatchdogBackgroundService"/>: interval loop, startup grace, per-tenant
/// try/catch. The startup grace matters here too — a freshly started pod has an empty adapter cache
/// and no SignalR connections yet, so sweeping immediately would publish a burst of Offline
/// severities that resolve themselves a minute later.
/// </para>
/// </summary>
internal class WorkloadStateMetricsBackgroundService(
    IAdapterCache adapterCache,
    ICommunicationRepository communicationRepository,
    IOptions<CommunicationControllerOptions> options)
    : BackgroundService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Tenants the previous sweep published for. Whatever falls out of this set — switched off, or
    /// opted back out — has to have its series dropped, or its sweep-age gauge would climb forever
    /// and alert about a tenant nobody is watching any more.
    /// </summary>
    private IReadOnlySet<string> _lastPublishedTenantIds = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.WorkloadStateMetricsIntervalMinutes));

        Logger.Info(
            "Workload state metrics background service starting with interval / startup grace of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        try
        {
            // Startup grace: let adapters reconnect after a controller (re)start before their
            // communication state is published, otherwise every one of them reports as offline once.
            await Task.Delay(interval, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await SweepAllTenantsAsync();
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error in workload state metrics sweep");
                }

                await Task.Delay(interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    internal async Task SweepAllTenantsAsync()
    {
        var published = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            try
            {
                if (!await communicationRepository.IsWorkloadObservabilityEnabledAsync(tenantId))
                {
                    continue;
                }

                await SweepTenantAsync(tenantId);
                published.Add(tenantId);
            }
            catch (Exception ex)
            {
                // Deliberately does NOT add the tenant to the published set and does NOT complete
                // its sweep: a failed sweep leaves the previous values in place and lets the
                // sweep-age gauge rise, which is the honest signal.
                Logger.Error(ex, "Error publishing workload state metrics for tenant '{TenantId}'", tenantId);
            }
        }

        foreach (var goneTenantId in _lastPublishedTenantIds.Except(published))
        {
            WorkloadStateMetrics.ForgetTenant(goneTenantId);
        }

        _lastPublishedTenantIds = published;
    }

    internal async Task SweepTenantAsync(string tenantId)
    {
        var observed = new HashSet<string>(StringComparer.Ordinal);

        // Workloads = adapters and applications (RtDeployableWorkload); pools are the layer above and
        // are read separately, the same split the operator model makes.
        var workloads = await communicationRepository.GetWorkloadsAsync(tenantId);
        foreach (var workload in workloads)
        {
            WorkloadStateMetrics.ObserveWorkload(tenantId, workload);
            observed.Add(workload.RtId.ToString());
        }

        var pools = await communicationRepository.GetPoolsAsync(tenantId);
        foreach (var pool in pools)
        {
            WorkloadStateMetrics.ObservePool(tenantId, pool);
            observed.Add(pool.RtId.ToString());
        }

        // Only now: the liveness stamp means "a sweep ran to completion", not "a sweep was started".
        WorkloadStateMetrics.CompleteSweep(tenantId, observed);
    }
}
