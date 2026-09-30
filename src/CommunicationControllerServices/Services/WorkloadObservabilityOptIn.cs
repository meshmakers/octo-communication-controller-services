using System.Collections.Concurrent;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     The process-wide, synchronously readable answer to "may this tenant's workload and pipeline
///     observability be published at all?" (AB#5432).
///
///     <para>
///         <b>One switch, one reader.</b> The switch is
///         <c>System/TenantModeConfiguration.PublishWorkloadObservability</c>
///         (<c>System-2.3.0</c>, optional Boolean, default <c>false</c>) and the only code in this
///         process that ever reads that attribute is
///         <see cref="Repository.ICommunicationRepository.IsWorkloadObservabilityEnabledAsync" />,
///         called once per tenant per sweep by <c>WorkloadStateMetricsBackgroundService</c>. That
///         sweep publishes its verdict here; every emitter — <see cref="WorkloadStateMetrics" />
///         implicitly through the sweep itself, <see cref="PipelineExecutionMetrics" /> and
///         <see cref="WorkloadLifecycleMetrics" /> through this class — then answers the question
///         without touching the database. A second reader of the attribute would be a second
///         generation of the switch, which is exactly what this design rejects.
///     </para>
///
///     <para>
///         <b>Why a cache and not an <c>await</c> at the call site.</b> The two emitters this gates
///         cannot ask a repository:
///         <list type="bullet">
///             <item>
///                 <description>
///                     <see cref="PipelineExecutionMetrics" />' three gauges and
///                     <see cref="WorkloadLifecycleMetrics" />' two are <c>ObservableGauge</c>
///                     callbacks. A callback is synchronous by contract, runs on the exporter's
///                     collection thread once per scrape, and blocking it on a Mongo round trip per
///                     series would stall the whole export.
///                 </description>
///             </item>
///             <item>
///                 <description>
///                     <c>octo.pipeline.execution.count</c> is recorded on the completion path —
///                     batches of a hundred executions at a time — and the wake / hibernation
///                     instruments on the lifecycle paths a request waits behind. A per-call tenant
///                     configuration read there would be a database query per execution.
///                 </description>
///             </item>
///         </list>
///         So the verdict is cached per tenant, exactly the shape
///         <see cref="LifecycleConfigurationService" /> uses for the scale-to-zero gate, with the
///         difference that the refresh is pushed by the sweep that already reads the flag rather
///         than pulled lazily on a miss — a lazy pull would need an <c>await</c> in precisely the
///         two places that cannot have one.
///     </para>
///
///     <para>
///         <b>Bounded, and it fails closed.</b> Each entry carries an expiry. A healthy sweep
///         refreshes every entry every interval, so the TTL never expires in normal operation; it
///         only matters when the sweep — the single reader — has stopped. At that point the process
///         no longer knows whether a tenant is still opted in, and publishing on an unverifiable
///         verdict is the same mistake as treating an unreadable opt-in as an opt-in. An unknown,
///         opted-out or expired tenant therefore reads as <c>false</c>, and because the gauges are
///         gated inside their collection callbacks, that verdict <i>stops</i> their series instead of
///         freezing them at the last value. <c>octo.workload.sweep.age</c> is what makes the dead
///         sweep itself visible.
///     </para>
///
///     <para>
///         Static for the same reason the metric classes are: the instruments are process-wide, and
///         threading a dependency through the hub, the lifecycle service, the watchdog and the
///         statistics sweep would add wiring without adding a seam worth having.
///     </para>
/// </summary>
internal static class WorkloadObservabilityOptIn
{
    /// <summary>
    ///     Opted-in tenants and the instant their verdict goes stale. Opted-out tenants are absent
    ///     rather than stored as <c>false</c> — "not in the map" and "opted out" have to behave
    ///     identically anyway, and a negative entry would be one more thing to expire correctly.
    /// </summary>
    private static readonly ConcurrentDictionary<string, DateTime> OptedInUntil = new(StringComparer.Ordinal);

    /// <summary>
    ///     True while the tenant is known to have opted in. A lock-free dictionary lookup, which is
    ///     what makes it usable on the completion path and inside a gauge callback.
    /// </summary>
    public static bool IsEnabled(string tenantId) =>
        OptedInUntil.TryGetValue(tenantId, out var expiresAt) && DateTime.UtcNow < expiresAt;

    /// <summary>
    ///     Stores the verdict the sweep just read. <paramref name="validFor" /> should be a small
    ///     multiple of the sweep interval: long enough that a single slow or skipped sweep does not
    ///     switch a tenant off, short enough that a process whose sweep died stops publishing on a
    ///     verdict it can no longer confirm.
    /// </summary>
    public static void Refresh(string tenantId, bool optedIn, TimeSpan validFor)
    {
        if (optedIn)
        {
            OptedInUntil[tenantId] = DateTime.UtcNow.Add(validFor);
        }
        else
        {
            OptedInUntil.TryRemove(tenantId, out _);
        }
    }

    /// <summary>
    ///     Drops a tenant's verdict — a tenant that was switched off or deleted is never swept
    ///     again, so its entry would otherwise sit there until it expired.
    /// </summary>
    public static void Forget(string tenantId)
    {
        OptedInUntil.TryRemove(tenantId, out _);
    }
}
