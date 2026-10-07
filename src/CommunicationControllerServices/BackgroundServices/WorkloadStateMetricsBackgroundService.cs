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
/// It is also the <b>single reader of that flag for the whole process</b>. One per-tenant switch
/// governs whether a tenant's workload and pipeline observability is published at all, and this
/// loop is where it is read: the verdict is pushed into <see cref="WorkloadObservabilityOptIn"/>,
/// from where <see cref="PipelineExecutionMetrics"/> and <see cref="WorkloadLifecycleMetrics"/>
/// read it synchronously — they are fed from event paths and <c>ObservableGauge</c> callbacks and
/// cannot await a repository. A tenant that drops out (switched off, deleted, or opted back out) is
/// forgotten in all three metric families here, because this is the only place that sees the
/// transition.
/// </para>
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

    /// <summary>
    /// Tenants the previous pass found opted in. Tracked separately from
    /// <see cref="_lastPublishedTenantIds"/>: that set is about a state sweep that ran to completion,
    /// this one about the opt-in verdict, and the pipeline / lifecycle families have to be forgotten
    /// on a change of the verdict — not on a tenant whose entity read happened to fail.
    /// </summary>
    private IReadOnlySet<string> _lastOptedInTenantIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// How long a verdict stays usable without a refresh. Three intervals: a single slow or skipped
    /// sweep must not switch a tenant's observability off, but a process whose sweep has been dead
    /// for a quarter of an hour no longer knows whether a tenant is opted in, and must not keep
    /// publishing on a verdict it cannot confirm.
    /// </summary>
    internal static TimeSpan OptInValidityFor(TimeSpan interval) => interval * 3;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.WorkloadStateMetricsIntervalMinutes));

        Logger.Info(
            "Workload state metrics background service starting with interval / startup grace of {IntervalMinutes} minute(s)",
            interval.TotalMinutes);

        try
        {
            // The opt-in verdicts are refreshed BEFORE the startup grace. The grace exists so a fresh
            // pod does not publish a burst of Offline severities, which is an argument about the state
            // sweep only — while the verdict gates the pipeline and lifecycle instruments too, and
            // those are event-driven: every execution outcome and every wake in the grace window would
            // otherwise go uncounted for an opted-in tenant.
            try
            {
                await RefreshOptInsAsync(interval);
            }
            catch (Exception ex)
            {
                // A tenant cache that is not up yet must not take the loop down with it — the next
                // pass refreshes the verdicts anyway.
                Logger.Error(ex, "Error reading the workload observability opt-ins at startup");
            }

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
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.Value.WorkloadStateMetricsIntervalMinutes));

        // One flag, read once per tenant per interval, in one place. Everything downstream — this
        // sweep and the two synchronous emitters — decides from the cached verdict.
        var optedIn = await RefreshOptInsAsync(interval);

        var published = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tenantId in optedIn)
        {
            try
            {
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

    /// <summary>
    /// Reads the per-tenant opt-in for every enabled tenant and republishes the verdicts into
    /// <see cref="WorkloadObservabilityOptIn"/>, returning the tenants that are opted in.
    ///
    /// <para>
    /// The read cannot fail the caller and cannot be loud: <c>IsWorkloadObservabilityEnabledAsync</c>
    /// answers <c>false</c> for an absent attribute, an absent configuration entity and a failed read
    /// alike, logging at Debug — this runs per tenant per interval, and a tenant that has not opted in
    /// is the normal case, not an incident.
    /// </para>
    ///
    /// <para>
    /// A tenant that falls out of the opted-in set is forgotten in <b>all three</b> metric families.
    /// The export gates alone would already stop its series (that is why they sit in the collection
    /// callbacks rather than at the recording sites), but the in-memory state has to go too, or a
    /// tenant that opts back in weeks later would briefly report the state it had when it left.
    /// </para>
    /// </summary>
    private async Task<IReadOnlySet<string>> RefreshOptInsAsync(TimeSpan interval)
    {
        var validFor = OptInValidityFor(interval);
        var optedIn = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tenantId in adapterCache.GetEnabledTenantIds())
        {
            var enabled = false;
            try
            {
                enabled = await communicationRepository.IsWorkloadObservabilityEnabledAsync(tenantId);
            }
            catch (Exception ex)
            {
                // The repository already answers false for every unreadable case; this is the belt to
                // its braces, because one tenant's configuration must not be able to take the opt-in
                // refresh — and with it every other tenant's observability — down. Debug on purpose:
                // per tenant per interval, a warning here would be spam.
                Logger.Debug(ex,
                    "Could not read the workload observability opt-in of tenant '{TenantId}'; treating it as opted out",
                    tenantId);
            }

            if (enabled)
            {
                optedIn.Add(tenantId);
            }

            WorkloadObservabilityOptIn.Refresh(tenantId, enabled, validFor);
        }

        foreach (var goneTenantId in _lastOptedInTenantIds.Except(optedIn))
        {
            // Switched off, deleted, or opted back out. Refresh() already dropped the verdict of a
            // tenant that is still enabled; Forget covers the one that is not enabled any more and is
            // therefore never visited again.
            WorkloadObservabilityOptIn.Forget(goneTenantId);
            PipelineExecutionMetrics.ForgetTenant(goneTenantId);
            WorkloadLifecycleMetrics.ForgetTenant(goneTenantId);
        }

        _lastOptedInTenantIds = optedIn;

        return optedIn;
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
