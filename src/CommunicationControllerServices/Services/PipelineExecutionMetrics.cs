using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     OpenTelemetry instruments for pipeline executions (AB#5425).
///
///     Until now a pipeline execution existed only as unstructured log text
///     (<c>PipelineExecutionService … Batch started N executions …</c>). Log bodies are not a
///     dimension of the logs metric and the inferred log attributes do not attach to it, so every
///     rate grouped by them is zero — which is why a failing or a stalled pipeline could not be
///     alerted on at all. The <c>Monitoring.CkHealth</c> pipeline on test-2 stopped after two runs
///     and nobody noticed for four hours.
///
///     Two questions have to become answerable, and they need different kinds of signal:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>Is a pipeline running into errors?</b> A count of failures per pipeline —
///                 published unconditionally, so a healthy pipeline reports a zero rather than
///                 nothing.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>Has a pipeline that was running stopped running?</b> A pure error counter
///                 cannot answer this: a pipeline that stops executing stops failing too. It needs
///                 a sign of life that arrives on <i>every</i> pass, so that its absence — or a
///                 rising age — means something.
///             </description>
///         </item>
///     </list>
///
///     <b>The mistake this deliberately does not repeat.</b> The existing <c>octo.ck.*</c> gauges
///     are fault-only: a healthy library emits nothing at all. Absence there is ambiguous —
///     "repaired" and "dead" look identical — and a staleness rule built on them fires every time
///     somebody fixes something. Every instrument here is therefore published for every pipeline
///     the statistics sweep enumerates, healthy or not, so that a missing series means exactly one
///     thing: the controller is not reporting.
///
///     <b>Where the numbers come from.</b> The three gauges are fed from
///     <see cref="PipelineExecutionService.UpdateStatisticsAsync" />, which the execution-cleanup
///     background service already runs for every pipeline of every enabled tenant on every sweep.
///     That has two properties worth more than event-driven counters here: the values are derived
///     from persisted state, so they survive a controller restart instead of starting from zero,
///     and they exist for a pipeline that has not executed in weeks — exactly the case that has to
///     be alertable. The freshness of a gauge is therefore the sweep interval
///     (<c>PipelineExecutionStuckCheckIntervalMinutes</c>, minutes-scale), except for the age,
///     which is computed against the wall clock at collection time and so keeps rising between
///     sweeps.
///
///     <b>Cardinality.</b> Series count is the cost driver and is bounded deliberately:
///     <list type="bullet">
///         <item>
///             <description>
///                 The gauges carry tenant + pipeline (three series per pipeline). Pipelines are
///                 operator-authored entities — tens per tenant — so this is a bounded, slowly
///                 changing set, and a pipeline that is deleted is dropped from the map by
///                 <see cref="RetainPipelines" /> on the next sweep instead of lingering forever.
///             </description>
///         </item>
///         <item>
///             <description>
///                 The event counter carries tenant + outcome only — four series per tenant. It is
///                 deliberately <i>not</i> per pipeline: executions are the high-frequency event in
///                 this system (batches of a hundred at a time), the per-pipeline breakdown is
///                 already covered by the gauges at a fixed cost, and tenant x pipeline x outcome
///                 on a hot path is exactly how a metrics bill doubles overnight.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Nothing is tagged with an execution id, an error message, a trigger type or an
///                 adapter. The first is unbounded, the second effectively so, and the last two
///                 multiply every series without changing any alerting decision.
///             </description>
///         </item>
///     </list>
///
///     <b>Governed by the one per-tenant switch.</b> Nothing here is published for a tenant that has
///     not set <c>System/TenantModeConfiguration.PublishWorkloadObservability</c> (AB#5432) — see
///     <see cref="WorkloadObservabilityOptIn" /> for why the verdict is a cached, synchronously
///     readable flag rather than a repository call, and the two places this class consults it:
///     <list type="bullet">
///         <item>
///             <description>
///                 <b>The counter is gated where it is recorded</b> (<see cref="RecordExecutionOutcome" />
///                 / <see cref="RecordExecutionOutcomes" />). For a <c>Counter</c> the recording <i>is</i>
///                 the export — there is no later point at which an increment could be withheld.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>The gauges are gated where they are exported</b> (inside the collection
///                 callbacks, <see cref="Observe" /> / <see cref="ObserveLong" />) rather than where
///                 <see cref="ObservePipeline" /> / <see cref="ObserveStatistics" /> feed them. Those
///                 two are called from the statistics sweep, which has to run for every enabled
///                 tenant regardless — it maintains the persisted <c>RtPipelineStatistics</c>, not
///                 just these metrics — so gating them there would mean either skipping real work or
///                 gating in two places. Gating at the callback also gives the opt-out the one
///                 property it needs: the series <i>stop</i> at the next collection instead of
///                 freezing at their last value, and an opt-in takes effect at the next collection
///                 instead of waiting a sweep interval for the map to refill.
///             </description>
///         </item>
///     </list>
///
///     Static, mirroring <see cref="WorkloadLifecycleMetrics" />: the instruments are process-wide
///     and threading a metrics dependency through the service would add wiring without adding a
///     seam worth having.
/// </summary>
internal static class PipelineExecutionMetrics
{
    /// <summary>
    ///     Same meter as <see cref="WorkloadLifecycleMetrics" /> — already registered in
    ///     octo-common-services' <c>ObservabilityBuilder</c>, so these instruments are exported
    ///     without any change to the observability wiring.
    /// </summary>
    public const string MeterName = WorkloadLifecycleMetrics.MeterName;

    /// <summary>Reported for a pipeline that has never executed. Alert rules use <c>&gt;</c>, which excludes it.</summary>
    public const double NeverExecuted = -1;

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>
    ///     Outcomes reported by an adapter, counted as they arrive. This is the exact event stream:
    ///     immediate (no sweep delay) and monotonic, so a rate over it is meaningful.
    ///
    ///     It counts <i>reported</i> outcomes, not distinct executions — an adapter that replays a
    ///     completion after a reconnect is counted again. Executions that no adapter ever reports
    ///     on because the stuck reaper failed them are not counted here at all; they do show up in
    ///     <see cref="FailuresGauge" />, which is derived from persisted status and therefore the
    ///     authoritative per-pipeline number.
    /// </summary>
    private static readonly Counter<long> Executions = Meter.CreateCounter<long>(
        "octo.pipeline.execution.count",
        unit: "{execution}",
        description:
        "Pipeline execution outcomes reported by adapters, tagged by outcome " +
        "(completed / failed / cancelled / interrupted)");

    /// <summary>
    ///     Last statistics snapshot per pipeline, published as gauges. Replaced wholesale on every
    ///     sweep, so the exported set follows the pipelines that actually exist.
    /// </summary>
    private static readonly ConcurrentDictionary<(string TenantId, string PipelineRtId), PipelineGaugeEntry> States =
        new();

    /// <summary>
    ///     The zone the scheduler evaluates cron expressions in (AB#5492): <c>OctoRecurringSchedule</c>
    ///     stamps every schedule with <see cref="TimeZoneInfo.Local" /> of this process. Declared before
    ///     the gauges whose callbacks read it — static initialisers run in textual order.
    /// </summary>
    private static readonly TimeZoneInfo CronTimeZone = TimeZoneInfo.Local;

    // Assigning the gauges to fields is what keeps them alive; the callbacks do the reporting.
    // ReSharper disable NotAccessedField.Local

    /// <summary>
    ///     The stall signal. Seconds since the pipeline's most recent execution <i>started</i>,
    ///     computed against the wall clock at collection time so it keeps rising while nothing
    ///     runs — which is the whole point: a pipeline that dies quietly produces a ramp, not a
    ///     silence.
    ///
    ///     Published for every pipeline, including ones that are disabled or not deployed; the
    ///     <c>octo.pipeline.deployment_state</c> tag is what an alert rule filters on, so that a
    ///     deliberately switched-off pipeline does not page anyone while still being visible.
    /// </summary>
    private static readonly ObservableGauge<double> AgeGauge = Meter.CreateObservableGauge(
        "octo.pipeline.execution.age",
        () => Observe(e => e.LastExecutionAt == null
            ? NeverExecuted
            : Math.Max(0, (DateTime.UtcNow - e.LastExecutionAt.Value).TotalSeconds)),
        unit: "s",
        description:
        "Seconds since this pipeline's last execution started, -1 if it has never executed. " +
        "Rises without bound while a pipeline is not running, which is what makes a stalled " +
        "pipeline alertable");

    /// <summary>
    ///     Failed executions of this pipeline in the last hour, from the platform's own sliding
    ///     window. Zero for a healthy pipeline — emitted, not omitted, so that "no failures" and
    ///     "no reporting" stay distinguishable.
    /// </summary>
    private static readonly ObservableGauge<long> FailuresGauge = Meter.CreateObservableGauge(
        "octo.pipeline.execution.failures",
        () => ObserveLong(e => e.LastHourFailureCount),
        unit: "{execution}",
        description: "Executions of this pipeline that failed in the last hour (0 when healthy)");

    /// <summary>
    ///     The denominator. Without it a failure count cannot be read: three failures out of three
    ///     and three out of three thousand are not the same incident.
    /// </summary>
    private static readonly ObservableGauge<long> SuccessesGauge = Meter.CreateObservableGauge(
        "octo.pipeline.execution.successes",
        () => ObserveLong(e => e.LastHourSuccessCount),
        unit: "{execution}",
        description: "Executions of this pipeline that succeeded in the last hour");

    /// <summary>
    ///     The schedule-aware stall signal (AB#5492). Where <see cref="AgeGauge" /> says how long ago
    ///     the last execution started, this says how many cron fire times have passed since without
    ///     one — <c>0</c> for every healthy pipeline whatever its cadence, so a single rule covers the
    ///     hourly and the daily pipeline alike, and nothing at all for a pipeline without an enabled
    ///     cron trigger (HTTP, data-event), whose silence is not a stall.
    ///
    ///     Published only while the pipeline is <c>deployed</c> — the same collapsed predicate the
    ///     <c>octo.pipeline.deployment_state</c> tag carries — because a disabled or undeployed
    ///     pipeline is not expected to fire. Computed against the wall clock at collection time like
    ///     the age, so the count grows between sweeps; the arithmetic is in <see cref="PipelineCronSchedule" />.
    /// </summary>
    private static readonly ObservableGauge<long> CronMissedExecutionsGauge = Meter.CreateObservableGauge(
        "octo.pipeline.cron.missed_executions",
        () => ObserveCron((entry, now) =>
            PipelineCronSchedule.CountMissedExecutions(entry.CronTriggers, entry.LastExecutionAt, now, CronTimeZone)),
        unit: "{execution}",
        description:
        "Number of cron fire times that passed since the pipeline's last recorded execution; " +
        "0 when the pipeline runs on schedule");

    /// <summary>
    ///     The pipeline's expected cadence, for reading the count above: three missed executions of an
    ///     every-minute pipeline and three of a daily one are different incidents.
    /// </summary>
    private static readonly ObservableGauge<double> CronIntervalGauge = Meter.CreateObservableGauge(
        "octo.pipeline.cron.interval",
        () => ObserveCron((entry, now) =>
            PipelineCronSchedule.TypicalIntervalSeconds(entry.CronTriggers, now, CronTimeZone)),
        unit: "s",
        description: "Seconds between two consecutive cron fire times of this pipeline (the shortest " +
                     "when several triggers apply)");
    // ReSharper restore NotAccessedField.Local

    private sealed record PipelineGaugeEntry(
        string PipelineName,
        string DeploymentState,
        DateTime? LastExecutionAt,
        long LastHourSuccessCount,
        long LastHourFailureCount,
        IReadOnlyList<CronTrigger> CronTriggers);

    /// <summary>
    ///     Counts one adapter-reported execution outcome. Silently does nothing for a tenant that
    ///     has not opted in — see <see cref="WorkloadObservabilityOptIn" />.
    /// </summary>
    public static void RecordExecutionOutcome(string tenantId, RtPipelineExecutionStatusEnum status)
    {
        if (!WorkloadObservabilityOptIn.IsEnabled(tenantId))
        {
            return;
        }

        var outcome = OutcomeOf(status);
        if (outcome == null)
        {
            // Running / Interrupted-as-an-intermediate-state are not outcomes. Interrupted
            // executions are counted once their final result is reported.
            return;
        }

        Executions.Add(1,
            new KeyValuePair<string, object?>("octo.tenant.id", tenantId),
            new KeyValuePair<string, object?>("octo.pipeline.outcome", outcome));
    }

    /// <summary>Counts several outcomes of the same kind at once (the batch completion path).</summary>
    public static void RecordExecutionOutcomes(string tenantId, RtPipelineExecutionStatusEnum status, int count)
    {
        if (count <= 0 || !WorkloadObservabilityOptIn.IsEnabled(tenantId))
        {
            return;
        }

        var outcome = OutcomeOf(status);
        if (outcome == null)
        {
            return;
        }

        Executions.Add(count,
            new KeyValuePair<string, object?>("octo.tenant.id", tenantId),
            new KeyValuePair<string, object?>("octo.pipeline.outcome", outcome));
    }

    /// <summary>
    ///     Publishes the identity of a pipeline — the labels a human reads in an alert. Called from
    ///     the sweep, which is the only place that holds the entity; the statistics arrive
    ///     separately via <see cref="ObserveStatistics" />.
    /// </summary>
    public static void ObservePipeline(string tenantId, RtPipeline pipeline)
    {
        // Enabled=false wins over the persisted deployment state: a disabled pipeline is not
        // rolled out to its adapter regardless of what DeploymentState still says, and collapsing
        // both into one tag is what lets an alert rule express "expected to run" as a single
        // matcher instead of a join.
        var deploymentState = pipeline.Enabled == false
            ? "disabled"
            : pipeline.DeploymentState.ToString().ToLowerInvariant();

        Update(tenantId, pipeline.RtId, e => e with
        {
            PipelineName = pipeline.Name ?? string.Empty,
            DeploymentState = deploymentState,
        });
    }

    /// <summary>
    ///     Publishes a pipeline's statistics snapshot. Called on every exit path of the statistics
    ///     update, including the ones that skip the database write, because a pipeline whose
    ///     counters did not change still has to report — that is the difference between this and
    ///     the fault-only <c>octo.ck.*</c> gauges.
    /// </summary>
    public static void ObserveStatistics(string tenantId, OctoObjectId pipelineRtId,
        DateTime? lastExecutionAt, long lastHourSuccessCount, long lastHourFailureCount)
    {
        Update(tenantId, pipelineRtId, e => e with
        {
            LastExecutionAt = lastExecutionAt,
            LastHourSuccessCount = lastHourSuccessCount,
            LastHourFailureCount = lastHourFailureCount,
        });
    }

    /// <summary>
    ///     Publishes the cron triggers a pipeline is expected to fire on (AB#5492). Called from the
    ///     sweep with the pipeline's <b>enabled</b> triggers; an empty list is the normal case for an
    ///     HTTP or data-event pipeline and withdraws the <c>octo.pipeline.cron.*</c> series. Only
    ///     expressions that parse are kept — a malformed one could never have been scheduled, so it
    ///     must not take down the series of a pipeline whose other trigger is fine.
    ///
    ///     The per-trigger anchor for a pipeline that has never executed is the trigger's last
    ///     modification, falling back to its creation: the moment the schedule in its current form
    ///     came into force. That is the conservative choice — a trigger's deployment state is rewritten
    ///     on every tenant start, so after a controller restart a never-executed pipeline starts
    ///     counting afresh — and it is the only anchor the entities carry; a pipeline has no
    ///     deployment timestamp.
    /// </summary>
    public static void ObserveCronTriggers(string tenantId, OctoObjectId pipelineRtId,
        IEnumerable<RtPipelineTrigger> enabledTriggers)
    {
        var cronTriggers = new List<CronTrigger>();
        foreach (var trigger in enabledTriggers)
        {
            if (trigger.Enabled != true || !PipelineCronSchedule.TryParse(trigger.CronExpression, out var parsed))
            {
                continue;
            }

            cronTriggers.Add(new CronTrigger(trigger.CronExpression!.Trim(), parsed,
                trigger.RtChangedDateTime ?? trigger.RtCreationDateTime));
        }

        Update(tenantId, pipelineRtId, e => e with { CronTriggers = cronTriggers });
    }

    /// <summary>
    ///     Drops every pipeline of the tenant that the sweep did not see. A deleted pipeline whose
    ///     age gauge kept climbing would eventually alert about something that no longer exists.
    /// </summary>
    public static void RetainPipelines(string tenantId, IReadOnlySet<string> pipelineRtIds)
    {
        foreach (var key in States.Keys)
        {
            if (key.TenantId == tenantId && !pipelineRtIds.Contains(key.PipelineRtId))
            {
                States.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    ///     Drops every pipeline of one tenant. A tenant that was switched off is never swept again,
    ///     so without this its pipelines would report a forever-rising age and eventually alert
    ///     about something nobody is meant to be running.
    ///
    ///     Scoped to a single tenant on purpose: a "retain only these tenants" call would have to
    ///     reach across every tenant in the process, which is neither needed here — the caller
    ///     already knows which tenants dropped out — nor safely testable.
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
    }

    private static string? OutcomeOf(RtPipelineExecutionStatusEnum status) => status switch
    {
        RtPipelineExecutionStatusEnum.Completed => "completed",
        RtPipelineExecutionStatusEnum.Failed => "failed",
        RtPipelineExecutionStatusEnum.Cancelled => "cancelled",
        RtPipelineExecutionStatusEnum.Interrupted => "interrupted",
        _ => null,
    };

    private static void Update(string tenantId, OctoObjectId pipelineRtId,
        Func<PipelineGaugeEntry, PipelineGaugeEntry> change)
    {
        States.AddOrUpdate((tenantId, pipelineRtId.ToString()),
            _ => change(new PipelineGaugeEntry(string.Empty, "unknown", null, 0, 0, [])),
            (_, existing) => change(existing));
    }

    /// <summary>
    ///     Collection for the <c>octo.pipeline.cron.*</c> gauges (AB#5492). Unlike <see cref="Observe" />
    ///     this one is selective by contract: a series exists only for a deployed pipeline with at
    ///     least one enabled cron trigger, and only when the selector has a value — so the rule written
    ///     against it stays quiet for every pipeline the schedule says nothing about.
    /// </summary>
    private static IEnumerable<Measurement<T>> ObserveCron<T>(Func<PipelineGaugeEntry, DateTime, T?> selector)
        where T : struct
    {
        var now = DateTime.UtcNow;
        foreach (var (key, entry) in States)
        {
            if (entry.CronTriggers.Count == 0 || entry.DeploymentState != "deployed" ||
                !WorkloadObservabilityOptIn.IsEnabled(key.TenantId))
            {
                continue;
            }

            var value = selector(entry, now);
            if (value == null)
            {
                continue;
            }

            yield return new Measurement<T>(value.Value, CronTags(key, entry));
        }
    }

    private static IEnumerable<Measurement<double>> Observe(Func<PipelineGaugeEntry, double> selector)
    {
        foreach (var (key, entry) in States)
        {
            // The opt-in gate (AB#5432). Evaluated per collection rather than per sweep so an
            // opt-out stops the series at the next scrape instead of freezing it, and an opt-in does
            // not have to wait for the statistics sweep to refill the map. The lookup is a
            // dictionary hit against a set of tens of tenants — cheaper than building the tags.
            if (!WorkloadObservabilityOptIn.IsEnabled(key.TenantId))
            {
                continue;
            }

            yield return new Measurement<double>(selector(entry), Tags(key, entry));
        }
    }

    private static IEnumerable<Measurement<long>> ObserveLong(Func<PipelineGaugeEntry, long> selector)
    {
        foreach (var (key, entry) in States)
        {
            if (!WorkloadObservabilityOptIn.IsEnabled(key.TenantId))
            {
                continue;
            }

            yield return new Measurement<long>(selector(entry), Tags(key, entry));
        }
    }

    private static KeyValuePair<string, object?>[] Tags((string TenantId, string PipelineRtId) key,
        PipelineGaugeEntry entry) =>
    [
        new("octo.tenant.id", key.TenantId),
        new("octo.pipeline.rt_id", key.PipelineRtId),
        // The rtId is the stable identity; the name is carried because an alert that names only a
        // 24-hex id is not actionable at three in the morning. It is 1:1 with the rtId, so it does
        // not multiply series.
        new("octo.pipeline.name", entry.PipelineName),
        new("octo.pipeline.deployment_state", entry.DeploymentState),
    ];

    /// <summary>
    ///     Tags of the cron gauges. No deployment state — the series only exists while deployed — but
    ///     the expression(s), joined with <c>;</c>, so an alert can say what cadence was expected.
    ///     1:1 with the pipeline's trigger set, so it does not multiply series.
    /// </summary>
    private static KeyValuePair<string, object?>[] CronTags((string TenantId, string PipelineRtId) key,
        PipelineGaugeEntry entry) =>
    [
        new("octo.tenant.id", key.TenantId),
        new("octo.pipeline.rt_id", key.PipelineRtId),
        new("octo.pipeline.name", entry.PipelineName),
        new("octo.cron.expression", string.Join(";", entry.CronTriggers.Select(t => t.Expression))),
    ];
}
