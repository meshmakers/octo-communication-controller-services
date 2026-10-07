using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     OpenTelemetry instruments for the persisted state of adapters, applications and pools
///     (AB#5432, item 3).
///
///     <para>
///         <b>Why a second class next to <see cref="WorkloadLifecycleMetrics" />.</b> That class is
///         the on-demand lifecycle (AB#4919): event-driven, written from the hub and lifecycle paths
///         at the moment a transition happens, unconditional for every tenant, and about workloads
///         only. This one is sweep-driven, derived from persisted state, gated behind a per-tenant
///         opt-in, and covers pools as well. Folding the opt-in gate into a class whose two existing
///         instruments (<c>octo.workload.hibernated</c>, <c>octo.workload.offline_unexpected</c>) are
///         live and ungated is how one of those live series would eventually be switched off by
///         accident. Same meter, so nothing changes in the observability wiring and the two families
///         still join on <c>octo.tenant.id</c> / <c>octo.workload.rt_id</c>.
///     </para>
///
///     <para>
///         <b>What is published.</b> The three states <c>GetAdapters</c> and <c>GetPools</c> already
///         return, one gauge each:
///         <list type="bullet">
///             <item><description><c>octo.workload.deployment_state</c> — every kind.</description></item>
///             <item>
///                 <description>
///                     <c>octo.workload.communication_state</c> and
///                     <c>octo.workload.configuration_state</c> — adapters and pools only. The CK
///                     model carries <c>CommunicationState</c> / <c>ConfigurationState</c> on
///                     <c>Adapter</c> and <c>Pool</c>, not on <c>DeployableWorkload</c>: an
///                     Application neither registers over SignalR nor receives a pipeline
///                     configuration, so it has no such state. Reporting a fabricated zero for it
///                     would make "configured" mean two different things.
///                 </description>
///             </item>
///         </list>
///     </para>
///
///     <para>
///         <b>The value is the severity, not the enum.</b> A state without an alarm value cannot be
///         alerted on: <c>DeploymentState=4</c> is healthy (Disabled — deliberately not deployable)
///         while <c>=3</c> is an outage, so a rule written against the ordinals would have to encode
///         the enum, and would silently mean something else the day a value is inserted. Every gauge
///         therefore reports <see cref="Healthy" /> / <see cref="Warning" /> / <see cref="Critical" />
///         and carries the state's name in the <c>octo.workload.state</c> tag for the human reading
///         the alert. This is the same shape as the existing <c>octo.ck.library.state</c>
///         (value = severity, <c>octo.ck.state</c> = name), so the check rules stay one family.
///     </para>
///
///     <para>
///         <b>Healthy publishes a zero.</b> <c>octo.ck.library.state</c> is fault-only, and that is
///         its weakness: a repaired library and a dead monitor look identical. Here every entity the
///         sweep sees reports on every pass, healthy or not — for workloads that is the more useful
///         answer anyway, because "this adapter exists and is fine" is a fact an operator wants, and
///         because it makes the absence of a series mean exactly one thing.
///     </para>
///
///     <para>
///         <b>Sign of life.</b> Publishing zeros is not by itself enough: if a tenant has no entities,
///         or the sweep loop dies while the process lives, there is nothing to notice. The per-tenant
///         <c>octo.workload.sweep.age</c> is computed against the wall clock at collection time and
///         so keeps rising when the loop stops — the failure mode that went unnoticed for four hours
///         on 29.9. <c>octo.workload.sweep.entities</c> separates "opted in, nothing to report" from
///         "reporting".
///     </para>
///
///     <para>
///         <b>Cardinality.</b> Three series per adapter and per pool, one per application, plus two
///         per tenant — a fixed, slowly-changing set bounded by operator-authored entities (tens per
///         tenant). Deliberately <i>not</i> one gauge per aspect with the state as the only
///         distinguishing tag and a constant value of 1: that shape multiplies by the size of each
///         enum (5 + 3 + 4 = 12 series per adapter instead of 3) and turns every rule into a label
///         match that has to be revisited whenever an enum grows. The one label that does change with
///         the state is <c>octo.workload.state</c>; a transition retires the old series and starts a
///         new one, so an instant query may see both inside the staleness window — use
///         <c>max by (…)</c> when that matters. Nothing is tagged with a status message, a chart
///         version or a pool membership: unbounded or effectively so, and none of it changes an
///         alerting decision.
///     </para>
/// </summary>
internal static class WorkloadStateMetrics
{
    /// <summary>
    ///     Same meter as <see cref="WorkloadLifecycleMetrics" /> — already registered in
    ///     octo-common-services' <c>ObservabilityBuilder</c>, so these instruments are exported
    ///     without any change to the observability wiring (AB#5430 gave service metrics an OTLP
    ///     exporter at all; before that nothing on this meter ever left the process).
    /// </summary>
    public const string MeterName = WorkloadLifecycleMetrics.MeterName;

    /// <summary>The entity is in a state that needs nobody's attention.</summary>
    public const int Healthy = 0;

    /// <summary>Transient or incomplete — alert only with a <c>for:</c> duration.</summary>
    public const int Warning = 1;

    /// <summary>A fault. This is the value the check rules fire on.</summary>
    public const int Critical = 2;

    /// <summary>Tag value of <c>octo.workload.kind</c> for an adapter.</summary>
    public const string AdapterKind = "adapter";

    /// <summary>Tag value of <c>octo.workload.kind</c> for an application.</summary>
    public const string ApplicationKind = "application";

    /// <summary>
    ///     Tag value of <c>octo.workload.kind</c> for a pool. A pool is not a workload in the
    ///     operator's model — it is the layer above — but it carries the same three states and the
    ///     same opt-in covers it, so it shares the metric family and is told apart by this tag
    ///     rather than by a parallel <c>octo.pool.*</c> contract nobody would remember to alert on.
    /// </summary>
    public const string PoolKind = "pool";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    ///     Last observed state per entity. Replaced by the sweep and pruned by
    ///     <see cref="CompleteSweep" />, so the exported set follows the entities that exist.
    /// </summary>
    private static readonly ConcurrentDictionary<(string TenantId, string RtId), EntityEntry> States = new();

    /// <summary>Last completed sweep per tenant — the liveness signal.</summary>
    private static readonly ConcurrentDictionary<string, SweepEntry> Sweeps = new(StringComparer.Ordinal);

    // Assigning the gauges to fields is what keeps them alive; the callbacks do the reporting.
    // ReSharper disable NotAccessedField.Local

    private static readonly ObservableGauge<int> DeploymentStateGauge = Meter.CreateObservableGauge(
        "octo.workload.deployment_state",
        () => Observe(_ => true, DeploymentSeverity, e => Name(e.DeploymentState)),
        unit: "{severity}",
        description:
        "Severity of a workload's or pool's DeploymentState: 0 while it rests or is deployed, " +
        "1 while a deploy is pending, 2 on a deployment error. The state's name is in " +
        "octo.workload.state");

    private static readonly ObservableGauge<int> CommunicationStateGauge = Meter.CreateObservableGauge(
        "octo.workload.communication_state",
        () => Observe(e => e.CommunicationState != null, CommunicationSeverity,
            e => Name(e.CommunicationState!.Value)),
        unit: "{severity}",
        description:
        "Severity of an adapter's or pool's CommunicationState: 0 while online, or while it is " +
        "not expected to be connected at all (not deployed, hibernating), 1 while unregistered, " +
        "2 while unexpectedly offline. Applications have no such state and publish no series");

    private static readonly ObservableGauge<int> ConfigurationStateGauge = Meter.CreateObservableGauge(
        "octo.workload.configuration_state",
        () => Observe(e => e.ConfigurationState != null, ConfigurationSeverity,
            e => Name(e.ConfigurationState!.Value)),
        unit: "{severity}",
        description:
        "Severity of an adapter's or pool's ConfigurationState: 0 while configured, 1 while " +
        "unconfigured or pending, 2 on a configuration error. Applications have no such state " +
        "and publish no series");

    /// <summary>
    ///     Seconds since the last sweep that ran to completion for this tenant. Computed at
    ///     collection time, so it rises without bound while the sweep loop is not running — that is
    ///     what makes a dead emitter distinguishable from a fleet that is simply healthy.
    /// </summary>
    private static readonly ObservableGauge<double> SweepAgeGauge = Meter.CreateObservableGauge(
        "octo.workload.sweep.age",
        ObserveSweepAge,
        unit: "s",
        description:
        "Seconds since the last completed workload-state sweep of this tenant. Rises without " +
        "bound if the emitting job stops, which is the only way 'everything is healthy' and " +
        "'nothing is reporting' can be told apart");

    /// <summary>
    ///     How many entities the last completed sweep reported for this tenant. Zero is a legitimate
    ///     answer (an opted-in tenant with no adapters yet) and has to be distinguishable from a
    ///     sweep that did not happen — which is what the age gauge next to it is for.
    /// </summary>
    private static readonly ObservableGauge<int> SweepEntitiesGauge = Meter.CreateObservableGauge(
        "octo.workload.sweep.entities",
        ObserveSweepEntities,
        unit: "{entity}",
        description: "Adapters, applications and pools reported by the last completed sweep of this tenant");
    // ReSharper restore NotAccessedField.Local

    private sealed record EntityEntry(
        string Kind,
        string Name,
        RtDeploymentStateEnum DeploymentState,
        RtCommunicationStateEnum? CommunicationState,
        RtConfigurationStateEnum? ConfigurationState,
        RtLifecycleStateEnum? LifecycleState);

    private sealed record SweepEntry(DateTime CompletedAt, int EntityCount);

    /// <summary>
    ///     Publishes the state of one workload. An <see cref="RtAdapter" /> contributes all three
    ///     gauges; an <see cref="RtApplication" /> only the deployment state, because the model gives
    ///     it no communication or configuration state.
    /// </summary>
    public static void ObserveWorkload(string tenantId, RtDeployableWorkload workload)
    {
        var adapter = workload as RtAdapter;
        States[Key(tenantId, workload.RtId)] = new EntityEntry(
            adapter != null ? AdapterKind : ApplicationKind,
            workload.Name ?? string.Empty,
            workload.DeploymentState,
            adapter?.CommunicationState,
            adapter?.ConfigurationState,
            workload.LifecycleState);
    }

    /// <summary>Publishes the state of one pool.</summary>
    public static void ObservePool(string tenantId, RtPool pool)
    {
        States[Key(tenantId, pool.RtId)] = new EntityEntry(
            PoolKind,
            pool.Name ?? string.Empty,
            pool.DeploymentState,
            pool.CommunicationState,
            pool.ConfigurationState,
            // A pool is not scaled to zero — it has no lifecycle state to suppress against.
            null);
    }

    /// <summary>
    ///     Closes a sweep: drops every entity of the tenant the sweep did not see (a deleted adapter
    ///     whose gauge kept reporting would eventually alert about something that no longer exists)
    ///     and stamps the liveness signal.
    ///
    ///     Called only on a sweep that got all the way through, which is the whole point — a
    ///     half-failed sweep must not look like a fresh one.
    /// </summary>
    public static void CompleteSweep(string tenantId, IReadOnlySet<string> observedRtIds)
    {
        foreach (var key in States.Keys)
        {
            if (key.TenantId == tenantId && !observedRtIds.Contains(key.RtId))
            {
                States.TryRemove(key, out _);
            }
        }

        Sweeps[tenantId] = new SweepEntry(DateTime.UtcNow, observedRtIds.Count);
    }

    /// <summary>
    ///     Drops everything published for one tenant — used when the tenant is switched off or opts
    ///     back out. Without it the age gauge of a tenant that is never swept again would climb
    ///     forever and alert about something nobody is meant to be watching.
    /// </summary>
    public static void ForgetTenant(string tenantId)
    {
        foreach (var key in States.Keys)
        {
            if (key.TenantId == tenantId)
            {
                States.TryRemove(key, out _);
            }
        }

        Sweeps.TryRemove(tenantId, out _);
    }

    /// <summary>
    ///     Deployment severity. <c>Undeployed</c> and <c>Disabled</c> are resting states an operator
    ///     chose (Edge pools, workloads without a chart), not faults; <c>Pending</c> is a deploy in
    ///     flight and only interesting if it stays that way, so it is a warning a rule can hold with
    ///     <c>for:</c>; <c>Error</c> is the fault.
    /// </summary>
    private static int DeploymentSeverity(EntityEntry entry) => entry.DeploymentState switch
    {
        RtDeploymentStateEnum.Deployed => Healthy,
        RtDeploymentStateEnum.Undeployed => Healthy,
        RtDeploymentStateEnum.Disabled => Healthy,
        RtDeploymentStateEnum.Pending => Warning,
        RtDeploymentStateEnum.Error => Critical,
        // A value added to the enum later must not read as healthy.
        _ => Warning,
    };

    /// <summary>
    ///     Communication severity, judged where the knowledge is — the same reasoning as
    ///     <c>octo.workload.offline_unexpected</c>: "offline" stopped meaning "broken" the day
    ///     scale-to-zero shipped, and a check rule cannot join a lifecycle state it never receives.
    /// </summary>
    private static int CommunicationSeverity(EntityEntry entry)
    {
        if (!IsExpectedToRun(entry) || IsIntentionallyDown(entry))
        {
            return Healthy;
        }

        return entry.CommunicationState switch
        {
            RtCommunicationStateEnum.Online => Healthy,
            // Never connected, or reset by a configuration push. Transient on a fresh deploy.
            RtCommunicationStateEnum.Unregistered => Warning,
            RtCommunicationStateEnum.Offline => Critical,
            _ => Warning,
        };
    }

    /// <summary>
    ///     Configuration severity. <c>Error</c> is the case this work item was asked for and is
    ///     critical for anything that is meant to be running, hibernating or not — a hibernated
    ///     adapter has no business carrying a configuration error. The incomplete states are
    ///     suppressed while a workload is intentionally down, because going offline resets
    ///     <c>ConfigurationState</c> to <c>Unconfigured</c> by design (see
    ///     <c>AdapterService.SetAdapterCommunicationStateOfflineAsync</c>), so every hibernated
    ///     adapter would otherwise sit at a permanent warning.
    /// </summary>
    private static int ConfigurationSeverity(EntityEntry entry)
    {
        if (!IsExpectedToRun(entry))
        {
            // Nothing pushed a configuration to a workload that is not deployed, and whatever the
            // entity still carries is stale. A deployment that failed is reported by
            // octo.workload.deployment_state; reporting it twice would only double the noise.
            return Healthy;
        }

        if (entry.ConfigurationState == RtConfigurationStateEnum.Error)
        {
            return Critical;
        }

        if (IsIntentionallyDown(entry))
        {
            return Healthy;
        }

        return entry.ConfigurationState switch
        {
            RtConfigurationStateEnum.Configured => Healthy,
            RtConfigurationStateEnum.Unconfigured => Warning,
            RtConfigurationStateEnum.Pending => Warning,
            _ => Warning,
        };
    }

    /// <summary>
    ///     True while the platform is trying to have this entity up. Deliberately narrower than
    ///     <see cref="ActiveDeployment.IsActive" />, which also counts <c>Error</c> because a failed
    ///     helm release may still hold resources: an entity whose deploy errored is not expected to
    ///     be online, and its offline-ness is a consequence of the deployment error rather than a
    ///     second, independent fault.
    /// </summary>
    private static bool IsExpectedToRun(EntityEntry entry) =>
        entry.DeploymentState is RtDeploymentStateEnum.Deployed or RtDeploymentStateEnum.Pending;

    /// <summary>
    ///     True while an on-demand workload is down on purpose. <c>Waking</c> counts: a wake in
    ///     flight is legitimately not connected yet, and the wake itself is already measured by
    ///     <c>octo.workload.wake.duration</c> / <c>octo.workload.wake.count</c>.
    /// </summary>
    private static bool IsIntentionallyDown(EntityEntry entry) =>
        entry.LifecycleState is RtLifecycleStateEnum.Hibernated or RtLifecycleStateEnum.Draining
            or RtLifecycleStateEnum.Waking;

    private static (string TenantId, string RtId) Key(string tenantId, OctoObjectId rtId) =>
        (tenantId, rtId.ToString());

    private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    private static IEnumerable<Measurement<int>> Observe(Func<EntityEntry, bool> applies,
        Func<EntityEntry, int> severity, Func<EntityEntry, string> stateName)
    {
        foreach (var ((tenantId, rtId), entry) in States)
        {
            if (!applies(entry))
            {
                continue;
            }

            yield return new Measurement<int>(severity(entry),
                new KeyValuePair<string, object?>("octo.tenant.id", tenantId),
                new KeyValuePair<string, object?>("octo.workload.rt_id", rtId),
                // 1:1 with the rtId, so it multiplies nothing — and an alert naming only a 24-hex
                // id is not actionable at three in the morning.
                new KeyValuePair<string, object?>("octo.workload.name", entry.Name),
                new KeyValuePair<string, object?>("octo.workload.kind", entry.Kind),
                new KeyValuePair<string, object?>("octo.workload.state", stateName(entry)));
        }
    }

    private static IEnumerable<Measurement<double>> ObserveSweepAge()
    {
        foreach (var (tenantId, sweep) in Sweeps)
        {
            yield return new Measurement<double>(
                Math.Max(0, (DateTime.UtcNow - sweep.CompletedAt).TotalSeconds),
                new KeyValuePair<string, object?>("octo.tenant.id", tenantId));
        }
    }

    private static IEnumerable<Measurement<int>> ObserveSweepEntities()
    {
        foreach (var (tenantId, sweep) in Sweeps)
        {
            yield return new Measurement<int>(sweep.EntityCount,
                new KeyValuePair<string, object?>("octo.tenant.id", tenantId));
        }
    }
}
