using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Why a lease was not granted. The metric label, and the reason the caller is told
///     (AB#4924 increment 9).
/// </summary>
/// <remarks>
///     🔴 <b>A named reason, not a free-text message.</b> A refusal counter tagged with a formatted
///     string would produce one series per borrower name, per pool id and per adapter name that ever
///     appeared in a refusal message — unbounded by construction. The enum is the label; the message
///     stays free text and goes to the caller and the log, where it can name whatever it likes.
/// </remarks>
public enum LeaseRefusalReason
{
    /// <summary>Not a refusal.</summary>
    None = 0,

    /// <summary>The <b>lending</b> tenant has leasing switched off (AB#4924 §14).</summary>
    LeasingDisabledLender,

    /// <summary>The <b>borrowing</b> tenant has leasing switched off (AB#4924 §14).</summary>
    LeasingDisabledBorrower,

    /// <summary>No adapter with that rtId in the borrowing tenant.</summary>
    BorrowerAdapterUnknown,

    /// <summary>The borrower's adapter is not <c>LifecycleMode = Leased</c>.</summary>
    BorrowerNotLeased,

    /// <summary>
    ///     AB#5329: the borrower's adapter IS <c>Leased</c> but names no lending pool — its
    ///     <c>LentFrom</c> mirror is missing or carries an unusable <c>Lender</c> record. Enqueue
    ///     stage: nothing is queued, because nothing downstream could ever serve it.
    /// </summary>
    BorrowerNamesNoPool,

    /// <summary>The borrower declares it borrows from a different lending tenant.</summary>
    BorrowerNamesAnotherLender,

    /// <summary>The borrower declares it borrows from a different pool.</summary>
    BorrowerNamesAnotherPool,

    /// <summary>The lender has no such pool, or it could not be read.</summary>
    PoolUnknown,

    /// <summary>The pool's sharing scope does not reach the borrowing tenant.</summary>
    LendingScopeDenied,

    /// <summary>The borrower's adapter has no usable <c>PipelineServiceAccount</c> credential.</summary>
    BorrowerCredentialMissing,

    /// <summary>The pipeline named by the work item could not be projected for the borrower.</summary>
    PipelineProjectionFailed,

    /// <summary>No idle member of the pool is connected to this controller instance.</summary>
    NoIdleMember,

    /// <summary>The admission gate declined — another controller instance claimed the work first.</summary>
    AdmissionGateDeclined,

    /// <summary>The admission gate threw; nothing was decided and the member was put back.</summary>
    AdmissionGateFailed,

    /// <summary>The lease could not be pushed to the reserved member.</summary>
    MemberDispatchFailed,

    /// <summary>
    ///     The borrower already holds <c>LendingMaxConcurrentLeasesPerTenant</c> leases. Scheduler
    ///     stage: no grant was attempted, the work item stays queued.
    /// </summary>
    PerTenantCap,

    /// <summary>
    ///     Every member of the pool is busy or draining. Scheduler stage: no grant was attempted,
    ///     the queue simply grows (concept §6, "pool exhausted").
    /// </summary>
    PoolExhausted,

    /// <summary>
    ///     🔴 The borrowing tenant's <b>database</b> credential could not be resolved (AB#4924) — no
    ///     tenant record, no database name, or this controller holds no datasource credential at all.
    /// </summary>
    /// <remarks>
    ///     Separate from <see cref="BorrowerCredentialMissing" /> on purpose: that one is the
    ///     borrower's OAuth identity and this one is its data access, they fail for entirely different
    ///     reasons, and a dashboard that could not tell them apart would send an operator to the wrong
    ///     half of the system. A refusal is the only correct outcome — a lease granted with a blank
    ///     database credential leaves the member running on whatever credentials its own process
    ///     happens to hold, which is exactly the failure this mechanism exists to prevent.
    /// </remarks>
    BorrowerDatabaseCredentialUnresolvable
}

/// <summary>
///     Where a lease was refused.
/// </summary>
public enum LeaseStage
{
    /// <summary>At enqueue — nothing was written and no work item exists.</summary>
    Enqueue,

    /// <summary>In a scheduling round, before a grant was attempted. The work item stays queued.</summary>
    Schedule,

    /// <summary>In <c>GrantLeaseAsync</c>, the choke point every grant goes through.</summary>
    Grant
}

/// <summary>
///     Why a member was drained.
/// </summary>
public enum LeaseDrainReason
{
    /// <summary>Its lease expired server-side; post-lease cleanliness is unproven (concept §6).</summary>
    TtlExpiry
}

/// <summary>
///     Why an in-flight lease ended without a release.
/// </summary>
public enum LeaseInterruptReason
{
    /// <summary>The TTL expired before the member released.</summary>
    TtlExpiry,

    /// <summary>The member disconnected while holding the lease.</summary>
    MemberLost
}

/// <summary>
///     OpenTelemetry instruments for adapter pool leasing (AB#4924 increment 9, plan §11).
/// </summary>
/// <remarks>
///     <para>
///         Leasing replaces one process per tenant with a shared pool, and every claim that makes is
///         a claim about numbers nobody can see from the entity model alone: that the pool amortises
///         its warm-up, that round-robin is actually fair, that the queue drains, that scale-up fires
///         on the right signal. These instruments are where those claims become checkable.
///     </para>
///     <para>
///         🔴 <b>The amortisation triple is the point.</b> Concept §2.3 keeps
///         <c>LeaseGrantedAt..LeaseReleasedAt</c> and the pipeline's own run time as deliberately
///         separate spans, because the difference between them <i>is</i> the per-lease warm-up the
///         pool exists to amortise. <c>octo.lease.held.duration</c>, <c>octo.lease.work.duration</c>
///         and <c>octo.lease.overhead.duration</c> are that difference made readable. The work span
///         is measured by the member and travels back on the release
///         (<c>LeaseResultDto.WorkDurationMs</c>) — the controller cannot measure it, because for a
///         leased execution it is the controller that stamps <c>StartedAt</c>, at claim time, which
///         makes the entity's own span identical to the held span by construction.
///     </para>
///     <para>
///         Static, mirroring <see cref="WorkloadLifecycleMetrics" /> and
///         <c>MongoCommandObservability</c>: instruments are process-wide, and threading a metrics
///         dependency through the scheduler, the lease service, the trigger service and the reaper
///         would add wiring without adding a seam worth having.
///     </para>
///     <para>
///         <b>Cardinality.</b> Every series carries the borrowing tenant, the lending tenant and the
///         pool rtId — tenants are dozens per cluster and pools are a handful per lender, so the
///         product is small and bounded. Deliberately <b>not</b> labels: the execution id and the
///         lease id (one series per work item — unbounded and useless), the pipeline rtId (bounded
///         per tenant but multiplies the product by the pipeline count, and per-pipeline timing
///         already lives on <c>RtPipelineStatistics</c>), and the <b>member id</b>. The member id is
///         the subtle one: it is bounded at any instant by <c>MaxReplicas</c>, but not over time — a
///         member id changes on every pod restart, and draining is precisely the path that restarts
///         members, so a drain counter tagged by member would grow its label set fastest exactly
///         when it is firing. Drains and expiries are counted per pool; the member id goes in the
///         log line, which is an exceptional event rather than a per-lease one.
///     </para>
///     <para>
///         ⚠️ <b>Log volume.</b> A lease is frequent. Everything continuous — depth, wait, member
///         counts, signals — is a metric here and is deliberately <i>not</i> logged per scheduling
///         round; the logs keep the exceptional events (a grant, a refusal, an expiry, a drain).
///         That is the estate's recorded lesson from the adapter log-level flood, applied before it
///         happens rather than after.
///     </para>
/// </remarks>
internal static class AdapterLeasingMetrics
{
    /// <summary>
    ///     Meter name, shared with <see cref="WorkloadLifecycleMetrics" /> and therefore already
    ///     registered in octo-common-services' <c>ObservabilityBuilder</c>. Nothing new has to be
    ///     wired up for these instruments to reach Prometheus.
    /// </summary>
    public const string MeterName = WorkloadLifecycleMetrics.MeterName;

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    #region Tag names

    private const string TagTenant = "octo.tenant.id";
    private const string TagAdapterPoolTenant = "octo.pool.tenant_id";
    private const string TagAdapterPoolRtId = "octo.pool.rt_id";
    private const string TagAdapterPoolName = "octo.pool.name";
    private const string TagStage = "octo.lease.stage";
    private const string TagRefusalReason = "octo.lease.refusal_reason";
    private const string TagReleaseReason = "octo.lease.release_reason";
    private const string TagOutcome = "octo.lease.outcome";
    private const string TagInterruptReason = "octo.lease.interrupt_reason";
    private const string TagDrainReason = "octo.lease.drain_reason";
    private const string TagMemberState = "octo.pool.member_state";
    private const string TagScaleUpOutcome = "octo.lease.scaleup.outcome";
    private const string TagSignalKind = "octo.lease.scaleup.signal_kind";

    #endregion

    #region Counters and histograms

    private static readonly Counter<long> Enqueued = Meter.CreateCounter<long>(
        "octo.lease.enqueued.count",
        unit: "count",
        description:
        "Work items entering a pool queue. Together with octo.lease.granted.count this is the " +
        "queue's in-rate against its out-rate — the only honest answer to 'is the queue growing " +
        "or draining'. Re-queued attempts are counted on octo.lease.requeued.count instead, so " +
        "the full in-rate is the sum of the two");

    private static readonly Counter<long> Granted = Meter.CreateCounter<long>(
        "octo.lease.granted.count",
        unit: "count",
        description:
        "Leases granted, per borrowing tenant. This is the served-count round-robin fairness is " +
        "judged on: starvation that nobody can see is starvation that ships");

    private static readonly Counter<long> Refused = Meter.CreateCounter<long>(
        "octo.lease.refused.count",
        unit: "count",
        description:
        "Refusals, by named reason and by the stage they happened at. 'Leasing disabled' is an " +
        "operator decision and is expected to be non-zero during a staged rollout; every other " +
        "reason sustained is a fault");

    private static readonly Counter<long> Released = Meter.CreateCounter<long>(
        "octo.lease.released.count",
        unit: "count",
        description: "Leases handed back by their member, by release reason and work outcome");

    private static readonly Counter<long> Interrupted = Meter.CreateCounter<long>(
        "octo.lease.interrupted.count",
        unit: "count",
        description:
        "Leases that ended without a release — the TTL expired, or the member vanished mid-lease. " +
        "A rising rate means members are failing while holding a tenant");

    private static readonly Counter<long> Requeued = Meter.CreateCounter<long>(
        "octo.lease.requeued.count",
        unit: "count",
        description:
        "Interrupted attempts that were successfully enqueued again as a fresh execution " +
        "(concept §6, at-least-once). An interrupt without a matching re-queue is work that was " +
        "lost");

    private static readonly Counter<long> MembersDrained = Meter.CreateCounter<long>(
        "octo.lease.member_drained.count",
        unit: "count",
        description:
        "Pool members told to drain and be replaced. Counted per pool and never per member id: a " +
        "drain loop is a property of the pool, and the member id changes on every restart the " +
        "loop causes");

    private static readonly Counter<long> ScaleUps = Meter.CreateCounter<long>(
        "octo.lease.scaleup.count",
        unit: "count",
        description:
        "Scale-up decisions the queue signals produced, by outcome. Emitted alongside the signals " +
        "themselves so the averaging window (concept §8 Q14) can be chosen from data rather than " +
        "from argument");

    private static readonly Histogram<double> QueueWait = Meter.CreateHistogram<double>(
        "octo.lease.queue.wait",
        unit: "s",
        description:
        "Seconds from QueuedAt to LeaseGrantedAt, per borrowing tenant. The wait distribution is " +
        "the other half of the fairness question: equal served-counts with wildly unequal waits " +
        "is not fairness",
        // A queue wait spans "the next scheduling round" (5 s) to "the pool is undersized and this
        // has been sitting for ten minutes". The default millisecond boundaries would put every
        // one of those in the last bucket.
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [1, 2, 5, 10, 20, 30, 60, 120, 300, 600, 1800]
        });

    private static readonly Histogram<double> HeldDuration = Meter.CreateHistogram<double>(
        "octo.lease.held.duration",
        unit: "s",
        description:
        "Seconds a pool member was held for one borrower: LeaseGrantedAt to LeaseReleasedAt. The " +
        "span that prices the borrower (concept §4b) and the numerator of the amortisation ratio",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 900]
        });

    private static readonly Histogram<double> WorkDuration = Meter.CreateHistogram<double>(
        "octo.lease.work.duration",
        unit: "s",
        description:
        "Seconds the member actually spent running the borrower's pipeline, as measured by the " +
        "member and reported on the release. Never derived from the execution entity: for a " +
        "leased execution the controller stamps StartedAt at claim time, which makes the entity " +
        "span identical to the held span by construction",
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.5, 1, 2, 5, 10, 30, 60, 120, 300, 600, 900]
        });

    private static readonly Histogram<double> OverheadDuration = Meter.CreateHistogram<double>(
        "octo.lease.overhead.duration",
        unit: "s",
        description:
        "Held seconds minus work seconds — the per-lease warm-up (token, tenant repository, CK " +
        "model, pipeline registration) plus the release round trip. This is the overhead a pool " +
        "exists to amortise, and the number concept §4's 'irrelevant at 16/h, prohibitive at " +
        "4089/h' claim rests on",
        // Tuned an order of magnitude lower than the held/work pair: the interesting question is
        // whether warm-up is hundreds of milliseconds or tens of seconds, and boundaries that
        // start at half a second would answer neither.
        advice: new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.05, 0.1, 0.25, 0.5, 1, 2, 3, 5, 7.5, 10, 20, 30]
        });

    #endregion

    #region Observable state

    /// <summary>
    ///     What each pool's last scheduling round saw. Replaced wholesale per round, so a pool that
    ///     stops being scheduled stops publishing rather than freezing at its last value.
    /// </summary>
    private static readonly ConcurrentDictionary<AdapterPoolKey, AdapterPoolObservation> Pools = new();

    /// <summary>
    ///     Pool display names, learned when a scheduling round reads the pool entity. Kept apart
    ///     from <see cref="Pools" /> so a name survives a round that could not read the entity.
    /// </summary>
    private static readonly ConcurrentDictionary<AdapterPoolKey, string> DeploymentSiteNames = new();

    // Assigning the gauges to fields is what keeps them alive; the callbacks do the reporting.
    // ReSharper disable NotAccessedField.Local
    private static readonly ObservableGauge<int> QueueDepthGauge = Meter.CreateObservableGauge(
        "octo.lease.queue.depth",
        ObserveQueueDepth,
        unit: "{item}",
        description:
        "Work items waiting for a lease, per pool and per borrowing tenant. Per borrowing tenant " +
        "because round-robin makes 'the queue' a collection of per-tenant queues — one aggregate " +
        "number cannot show one tenant starving behind another");

    private static readonly ObservableGauge<double> QueueOldestWaitGauge = Meter.CreateObservableGauge(
        "octo.lease.queue.oldest_wait",
        () => ObserveAdapterPool(p => p.OldestWaitSeconds),
        unit: "s",
        description:
        "Age of the oldest work item waiting on a pool. This is the exact quantity the scale-up " +
        "wait signal thresholds against, published so the threshold can be judged against it");

    private static readonly ObservableGauge<int> PoolMembersGauge = Meter.CreateObservableGauge(
        "octo.lease.pool.members",
        ObserveMembers,
        unit: "{member}",
        description:
        "Pool members connected to THIS controller instance, by state. Per-instance by " +
        "construction — a SignalR connection lives on one pod — so a cluster-wide count is " +
        "sum by (pool) across the controller pods");

    private static readonly ObservableGauge<double> ScaleUpWindowGauge = Meter.CreateObservableGauge(
        "octo.lease.scaleup.window",
        () => ObserveAdapterPool(p => p.AveragingWindowSeconds),
        unit: "s",
        description:
        "The depth-averaging window actually in force for this pool — the pool's own " +
        "ScaleUpQueueWaitSeconds unless LeaseScaleUpAveragingWindowSeconds overrides it. " +
        "Published because a measurement campaign that cannot tell which window produced which " +
        "decision measures nothing");

    private static readonly ObservableGauge<int> ScaleUpSignalGauge = Meter.CreateObservableGauge(
        "octo.lease.scaleup.signal",
        ObserveSignals,
        unit: "{signal}",
        description:
        "1 while a scale-up signal is firing, by signal kind (depth / wait). The inputs to the " +
        "decision, next to the decision itself on octo.lease.scaleup.count");

    /// <summary>
    ///     The alertable condition, evaluated where the knowledge lives.
    /// </summary>
    /// <remarks>
    ///     "Queue wait above the threshold" and "the pool is at its ceiling" are two facts that
    ///     live in different places — one is a queue reading, the other is the pool entity's
    ///     <c>MaxReplicas</c> against the member count — and an alert rule joining them would be a
    ///     second implementation of a judgement the scheduler already makes every round. Publishing
    ///     the answer keeps the rule a threshold on one series, exactly as
    ///     <c>octo.workload.offline_unexpected</c> does for the scale-to-zero epic.
    /// </remarks>
    private static readonly ObservableGauge<int> UndersizedGauge = Meter.CreateObservableGauge(
        "octo.lease.pool.undersized",
        () => ObserveAdapterPool(p => p.Undersized ? 1 : 0),
        unit: "{pool}",
        description:
        "1 while a pool's queue signals say it should grow and it is already at MaxReplicas — it " +
        "is undersized and cannot self-heal. 0 otherwise. This is the condition worth alerting on");
    // ReSharper restore NotAccessedField.Local

    #endregion

    #region Recording

    /// <summary>Records a work item entering a pool queue.</summary>
    public static void RecordEnqueued(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId)
    {
        Enqueued.Add(1, Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId));
    }

    /// <summary>Records a granted lease.</summary>
    public static void RecordGranted(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId)
    {
        Granted.Add(1, Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId));
    }

    /// <summary>
    ///     Records how long a work item waited between being queued and being leased.
    /// </summary>
    /// <remarks>
    ///     Recorded by the scheduler rather than by the lease service: only queued work has a wait,
    ///     and a hand-driven lease has no <c>QueuedAt</c> to measure from.
    /// </remarks>
    public static void RecordQueueWait(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId,
        TimeSpan wait)
    {
        QueueWait.Record(Math.Max(0, wait.TotalSeconds), Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId));
    }

    /// <summary>Records a refusal with its named reason.</summary>
    public static void RecordRefused(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId,
        LeaseStage stage, LeaseRefusalReason reason)
    {
        Refused.Add(1,
        [
            ..Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId),
            new KeyValuePair<string, object?>(TagStage, StageTag(stage)),
            new KeyValuePair<string, object?>(TagRefusalReason, RefusalTag(reason))
        ]);
    }

    /// <summary>
    ///     Records a lease handed back by its member, and the amortisation triple.
    /// </summary>
    /// <param name="borrowerTenantId">Tenant the member was serving.</param>
    /// <param name="lenderTenantId">Tenant that owns the pool.</param>
    /// <param name="adapterPoolRtId">RtId of the pool.</param>
    /// <param name="releaseReason">Why the lease ended, as the member reported it.</param>
    /// <param name="success">Whether the work item succeeded.</param>
    /// <param name="held">LeaseGrantedAt to now — how long the member was held for this borrower.</param>
    /// <param name="work">
    ///     How long the member actually spent running the work item, as it measured. Null when the
    ///     member reported none — an older member, or a lease that failed before the work item ran.
    ///     The work and overhead histograms stay silent in that case rather than recording a
    ///     fabricated zero, which would make the pool look infinitely wasteful.
    /// </param>
    public static void RecordReleased(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId,
        string releaseReason, bool success, TimeSpan held, TimeSpan? work)
    {
        var tags = Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId);

        Released.Add(1,
        [
            ..tags,
            new KeyValuePair<string, object?>(TagReleaseReason, releaseReason),
            new KeyValuePair<string, object?>(TagOutcome, success ? "success" : "failure")
        ]);

        var heldSeconds = Math.Max(0, held.TotalSeconds);
        HeldDuration.Record(heldSeconds, tags);

        if (work is not { } workSpan)
        {
            return;
        }

        var workSeconds = Math.Max(0, workSpan.TotalSeconds);
        WorkDuration.Record(workSeconds, tags);

        // Clamped at zero rather than dropped when it comes out negative. The two spans are
        // measured on two clocks — the controller's and the member's — so a sub-millisecond skew
        // can invert them on a very short work item, and dropping those samples would bias the
        // distribution towards exactly the slow leases the overhead question is not about.
        OverheadDuration.Record(Math.Max(0, heldSeconds - workSeconds), tags);
    }

    /// <summary>Records a lease that ended without a release.</summary>
    public static void RecordInterrupted(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId,
        LeaseInterruptReason reason, TimeSpan held)
    {
        var tags = Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId);
        Interrupted.Add(1,
            [..tags, new KeyValuePair<string, object?>(TagInterruptReason, InterruptTag(reason))]);

        // Stamped here too, and for the same reason LeaseReleasedAt is stamped on the TTL path: a
        // span recorded only on the happy path silently under-reports exactly the failures a
        // borrower did pay for.
        HeldDuration.Record(Math.Max(0, held.TotalSeconds), tags);
    }

    /// <summary>Records an interrupted attempt that was successfully enqueued again.</summary>
    public static void RecordRequeued(string borrowerTenantId, string lenderTenantId, string adapterPoolRtId,
        LeaseInterruptReason reason)
    {
        Requeued.Add(1,
        [
            ..Tags(borrowerTenantId, lenderTenantId, adapterPoolRtId),
            new KeyValuePair<string, object?>(TagInterruptReason, InterruptTag(reason))
        ]);
    }

    /// <summary>Records a member told to drain and be replaced.</summary>
    public static void RecordMemberDrained(string lenderTenantId, string adapterPoolRtId, LeaseDrainReason reason)
    {
        MembersDrained.Add(1,
        [
            new KeyValuePair<string, object?>(TagAdapterPoolTenant, lenderTenantId),
            new KeyValuePair<string, object?>(TagAdapterPoolRtId, adapterPoolRtId),
            new KeyValuePair<string, object?>(TagAdapterPoolName, DeploymentSiteName(lenderTenantId, adapterPoolRtId)),
            new KeyValuePair<string, object?>(TagDrainReason, DrainTag(reason))
        ]);
    }

    /// <summary>Records a scale-up decision: the pool grew, hit its ceiling, or the request failed.</summary>
    public static void RecordScaleUp(string lenderTenantId, string adapterPoolRtId, string outcome)
    {
        ScaleUps.Add(1,
        [
            new KeyValuePair<string, object?>(TagAdapterPoolTenant, lenderTenantId),
            new KeyValuePair<string, object?>(TagAdapterPoolRtId, adapterPoolRtId),
            new KeyValuePair<string, object?>(TagAdapterPoolName, DeploymentSiteName(lenderTenantId, adapterPoolRtId)),
            new KeyValuePair<string, object?>(TagScaleUpOutcome, outcome)
        ]);
    }

    /// <summary>Outcome tag values of <see cref="RecordScaleUp" />.</summary>
    public static class ScaleUpOutcomes
    {
        /// <summary>A scale request was issued and accepted.</summary>
        public const string Scaled = "scaled";

        /// <summary>The pool wanted to grow and is already at <c>MaxReplicas</c>.</summary>
        public const string AtCeiling = "at_ceiling";

        /// <summary>The scale request itself failed.</summary>
        public const string Failed = "failed";
    }

    /// <summary>
    ///     Records the pool's display name, so every series carries something a human can read.
    /// </summary>
    /// <remarks>
    ///     Kept separate from the per-round observation because a round that could not read the pool
    ///     entity must not blank out a name an earlier round supplied — a dashboard nobody can read
    ///     is not observability.
    /// </remarks>
    public static void NamePool(string lenderTenantId, string adapterPoolRtId, string? deploymentSiteName)
    {
        if (string.IsNullOrEmpty(deploymentSiteName))
        {
            return;
        }

        DeploymentSiteNames[AdapterPoolKey.Create(lenderTenantId, adapterPoolRtId)] = deploymentSiteName;
    }

    /// <summary>
    ///     Publishes everything one scheduling round observed about one pool. Replaces the previous
    ///     observation wholesale.
    /// </summary>
    /// <param name="lenderTenantId">Tenant that owns the pool.</param>
    /// <param name="adapterPoolRtId">RtId of the pool.</param>
    /// <param name="depthByBorrower">Queued work item count per borrowing tenant.</param>
    /// <param name="oldestWait">Age of the oldest waiting item, or zero when nothing waits.</param>
    /// <param name="members">Members of this pool connected to this controller instance.</param>
    /// <param name="averagingWindow">The depth-averaging window in force for this pool.</param>
    /// <param name="depthSignal">Whether the depth signal is firing.</param>
    /// <param name="waitSignal">Whether the wait signal is firing.</param>
    /// <param name="undersized">Whether the pool wants to grow and is already at its ceiling.</param>
    public static void ObserveRound(string lenderTenantId, string adapterPoolRtId,
        IReadOnlyDictionary<string, int> depthByBorrower, TimeSpan oldestWait,
        (int Available, int Leased, int Draining) members, TimeSpan averagingWindow,
        bool depthSignal, bool waitSignal, bool undersized)
    {
        Pools[AdapterPoolKey.Create(lenderTenantId, adapterPoolRtId)] = new AdapterPoolObservation(
            DateTime.UtcNow,
            lenderTenantId,
            adapterPoolRtId,
            depthByBorrower.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase),
            Math.Max(0, oldestWait.TotalSeconds),
            members.Available,
            members.Leased,
            members.Draining,
            Math.Max(0, averagingWindow.TotalSeconds),
            depthSignal,
            waitSignal,
            undersized);
    }

    /// <summary>
    ///     How long a pool keeps publishing after the last round that observed it.
    /// </summary>
    /// <remarks>
    ///     🔴 Must comfortably exceed <c>LeaseSchedulerIntervalSeconds</c> (default 5 s), so a pool
    ///     that is being scheduled normally never falls out between two rounds. At that default this
    ///     is 36 rounds of slack, and a pool deleted at 09:00 stops publishing by 09:03.
    /// </remarks>
    public static readonly TimeSpan PoolObservationStaleAfter = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     Drops pools that no round has observed for <see cref="PoolObservationStaleAfter" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A pool that was deleted, or whose last borrower was re-pointed, must stop publishing
    ///         rather than freeze at its last reading — a gauge stuck at "12 items queued" for a pool
    ///         that no longer exists is an alert that can never clear, and the first thing anyone
    ///         does with an alert that never clears is stop reading it.
    ///     </para>
    ///     <para>
    ///         🔴 <b>Expiry rather than "evict everything this round did not see".</b> The immediate
    ///         form is tempting and wrong: several controller pods and — in the test suite — several
    ///         concurrent callers each observe a different subset of pools, so "not in my set" does
    ///         not mean "gone". An age horizon is a statement about the pool, not about whoever
    ///         happened to sweep last.
    ///     </para>
    /// </remarks>
    /// <param name="nowUtc">Test seam; production passes nothing and gets the wall clock.</param>
    public static void SweepStaleDeploymentSites(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        foreach (var (key, observation) in Pools)
        {
            if (now - observation.ObservedAtUtc > PoolObservationStaleAfter)
            {
                Pools.TryRemove(key, out _);
                DeploymentSiteNames.TryRemove(key, out _);
            }
        }
    }

    #endregion

    #region Observation

    private static IEnumerable<Measurement<int>> ObserveQueueDepth()
    {
        foreach (var pool in Pools.Values)
        {
            foreach (var (tenantId, depth) in pool.DepthByBorrower)
            {
                yield return new Measurement<int>(depth,
                    new KeyValuePair<string, object?>(TagTenant, tenantId),
                    new KeyValuePair<string, object?>(TagAdapterPoolTenant, pool.LenderTenantId),
                    new KeyValuePair<string, object?>(TagAdapterPoolRtId, pool.AdapterPoolRtId),
                    new KeyValuePair<string, object?>(TagAdapterPoolName,
                        DeploymentSiteName(pool.LenderTenantId, pool.AdapterPoolRtId)));
            }
        }
    }

    private static IEnumerable<Measurement<int>> ObserveMembers()
    {
        foreach (var pool in Pools.Values)
        {
            yield return MemberMeasurement(pool, "available", pool.AvailableMembers);
            yield return MemberMeasurement(pool, "leased", pool.LeasedMembers);
            yield return MemberMeasurement(pool, "draining", pool.DrainingMembers);
        }
    }

    private static Measurement<int> MemberMeasurement(AdapterPoolObservation pool, string state, int count) =>
        new(count,
            new KeyValuePair<string, object?>(TagAdapterPoolTenant, pool.LenderTenantId),
            new KeyValuePair<string, object?>(TagAdapterPoolRtId, pool.AdapterPoolRtId),
            new KeyValuePair<string, object?>(TagAdapterPoolName, DeploymentSiteName(pool.LenderTenantId, pool.AdapterPoolRtId)),
            new KeyValuePair<string, object?>(TagMemberState, state));

    private static IEnumerable<Measurement<int>> ObserveSignals()
    {
        foreach (var pool in Pools.Values)
        {
            yield return SignalMeasurement(pool, "depth", pool.DepthSignal);
            yield return SignalMeasurement(pool, "wait", pool.WaitSignal);
        }
    }

    private static Measurement<int> SignalMeasurement(AdapterPoolObservation pool, string kind, bool firing) =>
        new(firing ? 1 : 0,
            new KeyValuePair<string, object?>(TagAdapterPoolTenant, pool.LenderTenantId),
            new KeyValuePair<string, object?>(TagAdapterPoolRtId, pool.AdapterPoolRtId),
            new KeyValuePair<string, object?>(TagAdapterPoolName, DeploymentSiteName(pool.LenderTenantId, pool.AdapterPoolRtId)),
            new KeyValuePair<string, object?>(TagSignalKind, kind));

    private static IEnumerable<Measurement<T>> ObserveAdapterPool<T>(Func<AdapterPoolObservation, T> selector)
        where T : struct
    {
        foreach (var pool in Pools.Values)
        {
            yield return new Measurement<T>(selector(pool),
                new KeyValuePair<string, object?>(TagAdapterPoolTenant, pool.LenderTenantId),
                new KeyValuePair<string, object?>(TagAdapterPoolRtId, pool.AdapterPoolRtId),
                new KeyValuePair<string, object?>(TagAdapterPoolName,
                    DeploymentSiteName(pool.LenderTenantId, pool.AdapterPoolRtId)));
        }
    }

    #endregion

    #region Helpers

    private static KeyValuePair<string, object?>[] Tags(string borrowerTenantId, string lenderTenantId,
        string adapterPoolRtId) =>
    [
        new(TagTenant, borrowerTenantId),
        new(TagAdapterPoolTenant, lenderTenantId),
        new(TagAdapterPoolRtId, adapterPoolRtId),
        new(TagAdapterPoolName, DeploymentSiteName(lenderTenantId, adapterPoolRtId))
    ];

    private static string DeploymentSiteName(string lenderTenantId, string adapterPoolRtId) =>
        DeploymentSiteNames.GetValueOrDefault(AdapterPoolKey.Create(lenderTenantId, adapterPoolRtId), string.Empty);

    /// <summary>
    ///     The label values, written out rather than derived from <c>ToString</c>.
    /// </summary>
    /// <remarks>
    ///     An enum member renamed for readability would silently rename a metric label and break
    ///     every dashboard and alert built on it. The switch makes that a compile-time choice.
    /// </remarks>
    private static string RefusalTag(LeaseRefusalReason reason) => reason switch
    {
        LeaseRefusalReason.LeasingDisabledLender => "leasing_disabled_lender",
        LeaseRefusalReason.LeasingDisabledBorrower => "leasing_disabled_borrower",
        LeaseRefusalReason.BorrowerAdapterUnknown => "borrower_adapter_unknown",
        LeaseRefusalReason.BorrowerNotLeased => "borrower_not_leased",
        LeaseRefusalReason.BorrowerNamesNoPool => "borrower_names_no_pool",
        LeaseRefusalReason.BorrowerNamesAnotherLender => "borrower_names_another_lender",
        LeaseRefusalReason.BorrowerNamesAnotherPool => "borrower_names_another_pool",
        LeaseRefusalReason.PoolUnknown => "pool_unknown",
        LeaseRefusalReason.LendingScopeDenied => "lending_scope_denied",
        LeaseRefusalReason.BorrowerCredentialMissing => "borrower_credential_missing",
        LeaseRefusalReason.PipelineProjectionFailed => "pipeline_projection_failed",
        LeaseRefusalReason.NoIdleMember => "no_idle_member",
        LeaseRefusalReason.AdmissionGateDeclined => "admission_gate_declined",
        LeaseRefusalReason.AdmissionGateFailed => "admission_gate_failed",
        LeaseRefusalReason.MemberDispatchFailed => "member_dispatch_failed",
        LeaseRefusalReason.PerTenantCap => "per_tenant_cap",
        LeaseRefusalReason.PoolExhausted => "pool_exhausted",
        LeaseRefusalReason.BorrowerDatabaseCredentialUnresolvable =>
            "borrower_database_credential_unresolvable",
        _ => "none"
    };

    private static string StageTag(LeaseStage stage) => stage switch
    {
        LeaseStage.Enqueue => "enqueue",
        LeaseStage.Schedule => "schedule",
        _ => "grant"
    };

    private static string InterruptTag(LeaseInterruptReason reason) => reason switch
    {
        LeaseInterruptReason.TtlExpiry => "ttl_expiry",
        _ => "member_lost"
    };

    private static string DrainTag(LeaseDrainReason reason) => reason switch
    {
        _ => "ttl_expiry"
    };

    /// <summary>One pool, identified across tenants and normalised the way the scheduler does.</summary>
    private readonly record struct AdapterPoolKey(string LenderTenantId, string AdapterPoolRtId)
    {
        public static AdapterPoolKey Create(string lenderTenantId, string adapterPoolRtId) =>
            new(lenderTenantId.ToLowerInvariant(), adapterPoolRtId.ToLowerInvariant());
    }

    /// <summary>What one scheduling round saw of one pool.</summary>
    private sealed record AdapterPoolObservation(
        DateTime ObservedAtUtc,
        string LenderTenantId,
        string AdapterPoolRtId,
        Dictionary<string, int> DepthByBorrower,
        double OldestWaitSeconds,
        int AvailableMembers,
        int LeasedMembers,
        int DrainingMembers,
        double AveragingWindowSeconds,
        bool DepthSignal,
        bool WaitSignal,
        bool Undersized);

    #endregion
}
