using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     OpenTelemetry instruments for the on-demand workload lifecycle (AB#4919).
///
///     Scale-to-zero trades memory for latency, and neither side of that trade is visible without
///     numbers: how often workloads are woken, how long a wake costs the request that triggered it,
///     and how much of the day a workload actually spends hibernated. The last one is what says
///     whether the feature is paying for itself on a given workload — a workload that hibernates
///     for two minutes an hour is pure overhead.
///
///     Static, mirroring <c>MongoCommandObservability</c> in the MongoDB engine: instruments are
///     process-wide, and threading a metrics dependency through the lifecycle service, the watchdog
///     and the scale-ack path would add wiring without adding a seam worth having.
///
///     Cardinality is bounded by the number of ingress-managed workloads (dozens per cluster), so
///     tenant, rtId and name are all safe as tags. The rtId is the stable identity; the name is
///     carried because a dashboard nobody can read is not observability.
/// </summary>
internal static class WorkloadLifecycleMetrics
{
    /// <summary>Meter name registered in octo-common-services' <c>ObservabilityBuilder</c>.</summary>
    public const string MeterName = "Meshmakers.Octo.Communication";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Wakes = Meter.CreateCounter<long>(
        "octo.workload.wake.count",
        unit: "count",
        description: "Wakes of an on-demand workload, tagged by outcome (configured / timeout)");

    private static readonly Histogram<double> WakeDuration = Meter.CreateHistogram<double>(
        "octo.workload.wake.duration",
        unit: "s",
        description:
        "Seconds from the scale-up request until the workload reports ConfigurationState=Configured. " +
        "This is the latency an inbound request pays for a wake",
        // The default boundaries are tuned for milliseconds and jump 5 → 10 → 25 → 50, which puts a
        // 7.8 s wake and a 24 s wake in the same bucket — exactly the range every measured wake
        // falls into, so the percentiles would say nothing. These span the wake budget (default
        // 60 s) with detail where the values actually are.
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [1, 2, 3, 5, 7.5, 10, 15, 20, 30, 45, 60, 90],
        });

    private static readonly Counter<long> Hibernations = Meter.CreateCounter<long>(
        "octo.workload.hibernation.count",
        unit: "count",
        description: "Completed hibernations (operator acknowledged scale to 0)");

    /// <summary>
    ///     Last known hibernation state per workload, published as a gauge. Averaged over time by
    ///     the backend, this is the hibernation ratio the epic asks for.
    ///
    ///     Written on every state transition and re-published by the idle watchdog from the
    ///     persisted state on each sweep, so a controller restart heals the map within one sweep
    ///     instead of reporting a stale zero forever.
    /// </summary>
    private static readonly ConcurrentDictionary<(string TenantId, string WorkloadRtId), WorkloadGaugeEntry> States =
        new();

    // Assigning the gauges to fields is what keeps them alive; the callbacks do the reporting.
    // ReSharper disable NotAccessedField.Local
    private static readonly ObservableGauge<int> HibernatedGauge = Meter.CreateObservableGauge(
        "octo.workload.hibernated",
        () => Observe(e => e.Hibernated),
        unit: "{workload}",
        description: "1 while an on-demand workload is hibernated or draining, 0 while it is running");

    /// <summary>
    ///     The alertable condition, evaluated where the knowledge lives.
    ///
    ///     "Offline" alone stopped meaning "broken" the day scale-to-zero shipped, and the two facts
    ///     needed to tell those apart — the communication state and the lifecycle state — are not
    ///     both exported as series, so an alert rule could not join them. Publishing the answer
    ///     instead of the inputs keeps the rule a threshold on one series and keeps the judgement in
    ///     the one place that already makes it.
    /// </summary>
    private static readonly ObservableGauge<int> OfflineUnexpectedGauge = Meter.CreateObservableGauge(
        "octo.workload.offline_unexpected",
        () => Observe(e => e.OfflineUnexpected),
        unit: "{workload}",
        description:
        "1 while a workload is offline for a reason other than an intentional hibernation — " +
        "this is the condition worth alerting on, 0 otherwise");
    // ReSharper restore NotAccessedField.Local

    private sealed record WorkloadGaugeEntry(string WorkloadName, bool Hibernated, bool OfflineUnexpected);

    /// <summary>Records a wake that reached <c>Configured</c> within the budget.</summary>
    public static void RecordWakeSucceeded(string tenantId, OctoObjectId workloadRtId, string? workloadName,
        TimeSpan duration)
    {
        var tags = Tags(tenantId, workloadRtId, workloadName);
        Wakes.Add(1, [..tags, new KeyValuePair<string, object?>("octo.wake.outcome", "configured")]);
        WakeDuration.Record(duration.TotalSeconds, tags);
        SetHibernated(tenantId, workloadRtId, workloadName, hibernated: false);
    }

    /// <summary>
    ///     Records a wake that never reached <c>Configured</c>. Deliberately not recorded on the
    ///     duration histogram: the budget is a cut-off, not an observation, and mixing it in would
    ///     pull every percentile towards the timeout.
    /// </summary>
    public static void RecordWakeTimedOut(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        Wakes.Add(1,
            [..Tags(tenantId, workloadRtId, workloadName),
                new KeyValuePair<string, object?>("octo.wake.outcome", "timeout")]);
    }

    /// <summary>Records a completed hibernation (the operator acknowledged the scale to 0).</summary>
    public static void RecordHibernated(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        Hibernations.Add(1, Tags(tenantId, workloadRtId, workloadName));
        SetHibernated(tenantId, workloadRtId, workloadName, hibernated: true);
    }

    /// <summary>
    ///     Publishes the gauge from a workload's persisted state. Called by the idle watchdog for
    ///     every on-demand workload it sweeps, which is what makes the gauge survive a restart.
    /// </summary>
    public static void ObserveState(string tenantId, RtDeployableWorkload workload)
    {
        SetHibernated(tenantId, workload.RtId, workload.Name,
            workload.LifecycleState is RtLifecycleStateEnum.Hibernated or RtLifecycleStateEnum.Draining);
    }

    /// <summary>Drops a workload from the gauge — an undeployed workload has no state to report.</summary>
    public static void Forget(string tenantId, OctoObjectId workloadRtId)
    {
        States.TryRemove((tenantId, workloadRtId.ToString()), out _);
    }

    /// <summary>
    ///     Records that a workload went offline. <paramref name="intentional"/> comes from the
    ///     lifecycle state, so a hibernation reads as expected while anything else — crash, eviction,
    ///     lost node — is what the alert fires on.
    /// </summary>
    public static void RecordOffline(string tenantId, OctoObjectId workloadRtId, string? workloadName,
        bool intentional)
    {
        Update(tenantId, workloadRtId, workloadName, e => e with { OfflineUnexpected = !intentional });
    }

    /// <summary>Records that a workload is online again, whatever took it offline before.</summary>
    public static void RecordOnline(string tenantId, OctoObjectId workloadRtId, string? workloadName)
    {
        Update(tenantId, workloadRtId, workloadName, e => e with { OfflineUnexpected = false });
    }

    private static void SetHibernated(string tenantId, OctoObjectId workloadRtId, string? workloadName,
        bool hibernated)
    {
        Update(tenantId, workloadRtId, workloadName, e => e with { Hibernated = hibernated });
    }

    private static void Update(string tenantId, OctoObjectId workloadRtId, string? workloadName,
        Func<WorkloadGaugeEntry, WorkloadGaugeEntry> change)
    {
        States.AddOrUpdate((tenantId, workloadRtId.ToString()),
            _ => change(new WorkloadGaugeEntry(workloadName ?? string.Empty, false, false)),
            // A caller that does not know the name (the disconnect path only has the rtId) must not
            // blank out a name an earlier one supplied — the label is what makes a dashboard legible.
            (_, existing) => change(string.IsNullOrEmpty(workloadName)
                ? existing
                : existing with { WorkloadName = workloadName }));
    }

    private static IEnumerable<Measurement<int>> Observe(Func<WorkloadGaugeEntry, bool> selector)
    {
        foreach (var ((tenantId, workloadRtId), entry) in States)
        {
            yield return new Measurement<int>(selector(entry) ? 1 : 0,
                new KeyValuePair<string, object?>("octo.tenant.id", tenantId),
                new KeyValuePair<string, object?>("octo.workload.rt_id", workloadRtId),
                new KeyValuePair<string, object?>("octo.workload.name", entry.WorkloadName));
        }
    }

    private static KeyValuePair<string, object?>[] Tags(string tenantId, OctoObjectId workloadRtId,
        string? workloadName) =>
    [
        new("octo.tenant.id", tenantId),
        new("octo.workload.rt_id", workloadRtId.ToString()),
        new("octo.workload.name", workloadName ?? string.Empty),
    ];
}
