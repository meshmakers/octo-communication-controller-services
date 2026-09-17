using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#4924 increment 9 (plan §11).
/// </summary>
/// <remarks>
///     <para>
///         These instruments are the only place several of the design's central claims are checkable
///         at all — that the pool amortises its warm-up (concept §2.3), that round-robin is fair
///         (§9.2), that scale-up fires on the signal it says it does (Q14). A silently dropped tag or
///         a span that never gets recorded hides exactly what the epic has to prove, so the
///         assertions are about the names and tags the exporter will really publish.
///     </para>
///     <para>
///         Recorded through a real <see cref="MeterListener" /> rather than a seam, for the same
///         reason <c>WorkloadLifecycleMetricsTests</c> is. The instruments are process-wide and TUnit
///         runs tests concurrently, so every test uses a unique pool rtId and the listener filters on
///         it — every instrument here carries one.
///     </para>
/// </remarks>
/// <remarks>
///     🔴 <b><c>[NotInParallel]</c> is load-bearing, not tidiness.</b> Every test in this file opens a
///     <see cref="System.Diagnostics.Metrics.MeterListener"/> over process-wide instruments, and a
///     listener being started or disposed on one thread mutates the very subscription lists another
///     thread's <c>Add</c> is walking. The symptom is a measurement that is simply never delivered —
///     one refusal short of sixteen, once in a few dozen runs. A metrics test that loses a
///     measurement at random is worse than no test: it fails for a reason that has nothing to do with
///     the metric. Every class in this repository that opens a listener shares this constraint key.
/// </remarks>
[NotInParallel(nameof(MeterListener))]
internal class AdapterLeasingMetricsTests
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    private static List<Recorded> Collect(string poolRtId, Action act, bool observeGauges = false)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AdapterLeasingMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        // One callback per numeric type: counters are long, histograms double, gauges int or double.
        // A missing callback is not an error, it is silence — which in a metrics test reads exactly
        // like "the instrument was never recorded".
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        // 🔴 The instruments have to EXIST before the listener starts. They are static fields of
        // AdapterLeasingMetrics, so the first test in the process to touch that class is the one
        // that creates them — and if that happens inside the act below, it happens while this
        // listener is already running and racing its own subscription. Forcing the class
        // constructor here makes every run look like the second one.
        RuntimeHelpers.RunClassConstructor(typeof(AdapterLeasingMetrics).TypeHandle);

        listener.Start();

        act();

        if (observeGauges)
        {
            listener.RecordObservableInstruments();
        }

        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.pool.rt_id") == poolRtId).ToList();
    }

    private static Dictionary<string, string> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return map;
    }

    private static string UniquePool() => OctoObjectId.GenerateNewId().ToString();

    private const string Lender = "lender";
    private const string Borrower = "borrower";

    /// <summary>
    ///     Concept §2.3: the held span and the pipeline run span are deliberately different, and the
    ///     difference is the per-lease warm-up the pool exists to amortise. All three have to be
    ///     readable or the separation bought nothing.
    /// </summary>
    [Test]
    public async Task AReleasedLease_RecordsHeldWorkAndTheOverheadBetweenThem()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act — held for 10 s, of which the pipeline itself ran 7.5 s.
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.RecordReleased(Borrower, Lender, poolRtId,
            "completed", success: true, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(7.5)));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.held.duration").Value).IsEqualTo(10);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.work.duration").Value).IsEqualTo(7.5);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.overhead.duration").Value)
            .IsEqualTo(2.5);

        var released = recorded.Single(r => r.Instrument == "octo.lease.released.count");
        await Assert.That(released.Tags["octo.lease.release_reason"]).IsEqualTo("completed");
        await Assert.That(released.Tags["octo.lease.outcome"]).IsEqualTo("success");
        await Assert.That(released.Tags["octo.tenant.id"]).IsEqualTo(Borrower);
        await Assert.That(released.Tags["octo.pool.tenant_id"]).IsEqualTo(Lender);
    }

    /// <summary>
    ///     A member that reported no work span — an older member, or a lease that failed before the
    ///     work item ran — must not be turned into a zero. A fabricated zero would say the pool spent
    ///     100 % of the lease on overhead, which is the most alarming possible reading of "we do not
    ///     know".
    /// </summary>
    [Test]
    public async Task AReleaseWithNoMemberMeasuredWorkSpan_RecordsTheHeldSpanAndNothingElse()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.RecordReleased(Borrower, Lender, poolRtId,
            "failed", success: false, TimeSpan.FromSeconds(4), work: null));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.held.duration").Value).IsEqualTo(4);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.work.duration")).IsFalse();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.overhead.duration")).IsFalse();
    }

    /// <summary>
    ///     The two spans come off two clocks. A sub-millisecond skew on a very short work item can
    ///     invert them, and a negative overhead is not a measurement — but dropping those samples
    ///     would bias the distribution towards the slow leases the question is not about.
    /// </summary>
    [Test]
    public async Task AWorkSpanLongerThanTheHeldSpan_RecordsZeroOverheadRatherThanANegativeOne()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.RecordReleased(Borrower, Lender, poolRtId,
            "completed", success: true, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2.001)));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.overhead.duration").Value)
            .IsEqualTo(0);
    }

    /// <summary>
    ///     Concept §6: a lease reclaimed on TTL still held the member for its whole span, and that
    ///     span is a billing input. Recorded on this path too, for exactly the reason
    ///     <c>LeaseReleasedAt</c> is stamped on it.
    /// </summary>
    [Test]
    public async Task AnInterruptedLease_StillRecordsTheTimeTheMemberWasHeld()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.RecordInterrupted(Borrower, Lender,
            poolRtId, LeaseInterruptReason.TtlExpiry, TimeSpan.FromSeconds(900)));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.held.duration").Value).IsEqualTo(900);
        var interrupted = recorded.Single(r => r.Instrument == "octo.lease.interrupted.count");
        await Assert.That(interrupted.Tags["octo.lease.interrupt_reason"]).IsEqualTo("ttl_expiry");
    }

    /// <summary>
    ///     Every refusal reason gets its own stable label, and the labels are lower_snake_case rather
    ///     than the enum's own spelling — renaming a member for readability must not silently rename
    ///     a label every dashboard and alert is built on.
    /// </summary>
    [Test]
    public async Task EveryRefusalReason_HasItsOwnStableSnakeCaseLabel()
    {
        // Arrange
        var poolRtId = UniquePool();
        var reasons = Enum.GetValues<LeaseRefusalReason>()
            .Where(r => r != LeaseRefusalReason.None)
            .ToList();

        // Act
        var recorded = Collect(poolRtId, () =>
        {
            foreach (var reason in reasons)
            {
                AdapterLeasingMetrics.RecordRefused(Borrower, Lender, poolRtId, LeaseStage.Grant, reason);
            }
        });

        // Assert
        var labels = recorded
            .Where(r => r.Instrument == "octo.lease.refused.count")
            .Select(r => r.Tags["octo.lease.refusal_reason"])
            .ToList();

        using var _ = Assert.Multiple();
        await Assert.That(labels).Count().IsEqualTo(reasons.Count);
        // Distinct: a switch arm that fell through to the default would collapse two reasons onto
        // one series, and the counter would then say "refusals are up" without saying of what.
        await Assert.That(labels.Distinct().Count()).IsEqualTo(reasons.Count);
        await Assert.That(labels.Any(l => l == "none")).IsFalse();
        await Assert.That(labels.All(l => l == l.ToLowerInvariant() && !l.Contains(' '))).IsTrue();
        // Not the enum's ToString: that is what makes a rename a compile-time choice.
        await Assert.That(labels.Contains(nameof(LeaseRefusalReason.PipelineProjectionFailed))).IsFalse();
        await Assert.That(labels.Contains("pipeline_projection_failed")).IsTrue();
        // The two halves of the kill switch are distinguishable, which is the whole point of §14's
        // "the refusal names which half is missing".
        await Assert.That(labels.Contains("leasing_disabled_lender")).IsTrue();
        await Assert.That(labels.Contains("leasing_disabled_borrower")).IsTrue();
    }

    /// <summary>
    ///     Round-robin makes "the queue" a set of per-tenant queues. One aggregate depth cannot show
    ///     one tenant starving behind another, which is the failure this design exists to prevent.
    /// </summary>
    [Test]
    public async Task TheQueueDepthGauge_IsPublishedPerBorrowingTenant()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int> { ["tenant-a"] = 200, ["tenant-b"] = 1 },
                TimeSpan.FromSeconds(90), (Available: 0, Leased: 2, Draining: 1),
                TimeSpan.FromSeconds(60), depthSignal: true, waitSignal: true, undersized: true),
            observeGauges: true);

        // Assert
        var depths = recorded.Where(r => r.Instrument == "octo.lease.queue.depth")
            .ToDictionary(r => r.Tags["octo.tenant.id"], r => r.Value);

        using var _ = Assert.Multiple();
        await Assert.That(depths).Count().IsEqualTo(2);
        await Assert.That(depths["tenant-a"]).IsEqualTo(200);
        await Assert.That(depths["tenant-b"]).IsEqualTo(1);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.queue.oldest_wait").Value)
            .IsEqualTo(90);
    }

    /// <summary>
    ///     Q14 deliberately refuses to guess the averaging window and says to instrument first. That
    ///     only works if the decisions and the window that produced them are both published: a
    ///     campaign that cannot tell which window produced which decision measures nothing.
    /// </summary>
    [Test]
    public async Task ARound_PublishesBothScaleUpSignalsAndTheWindowThatProducedThem()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int> { ["tenant-a"] = 9 }, TimeSpan.FromSeconds(12),
                (Available: 1, Leased: 1, Draining: 0), TimeSpan.FromSeconds(45),
                depthSignal: true, waitSignal: false, undersized: false),
            observeGauges: true);

        // Assert
        var signals = recorded.Where(r => r.Instrument == "octo.lease.scaleup.signal")
            .ToDictionary(r => r.Tags["octo.lease.scaleup.signal_kind"], r => r.Value);

        using var _ = Assert.Multiple();
        await Assert.That(signals["depth"]).IsEqualTo(1);
        await Assert.That(signals["wait"]).IsEqualTo(0);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.scaleup.window").Value)
            .IsEqualTo(45);
    }

    /// <summary>
    ///     The member states have to be mutually exclusive, or a cluster-wide
    ///     <c>sum by (pool)</c> over the controller pods double-counts and the pool looks bigger than
    ///     it is exactly when it is not keeping up.
    /// </summary>
    [Test]
    public async Task TheMemberGauge_ReportsThreeMutuallyExclusiveStates()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () => AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int>(), TimeSpan.Zero, (Available: 2, Leased: 3, Draining: 1),
                TimeSpan.FromSeconds(60), depthSignal: false, waitSignal: false, undersized: false),
            observeGauges: true);

        // Assert
        var members = recorded.Where(r => r.Instrument == "octo.lease.pool.members")
            .ToDictionary(r => r.Tags["octo.pool.member_state"], r => r.Value);

        using var _ = Assert.Multiple();
        await Assert.That(members).Count().IsEqualTo(3);
        await Assert.That(members["available"]).IsEqualTo(2);
        await Assert.That(members["leased"]).IsEqualTo(3);
        await Assert.That(members["draining"]).IsEqualTo(1);
    }

    /// <summary>
    ///     The alertable condition: the pool's own signals say it should grow and it is already at
    ///     <c>MaxReplicas</c>. Published as the answer rather than as the inputs, so the alert rule
    ///     stays a threshold on one series instead of a join nobody maintains.
    /// </summary>
    [Test]
    public async Task TheUndersizedGauge_IsOneOnlyWhileThePoolCannotGrowItselfOutOfTrouble()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act — first undersized, then a round in which it is not.
        var whileUndersized = Collect(poolRtId, () => AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int> { ["tenant-a"] = 40 }, TimeSpan.FromSeconds(600),
                (Available: 0, Leased: 3, Draining: 0), TimeSpan.FromSeconds(60),
                depthSignal: true, waitSignal: true, undersized: true),
            observeGauges: true);

        var afterGrowing = Collect(poolRtId, () => AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int> { ["tenant-a"] = 2 }, TimeSpan.FromSeconds(3),
                (Available: 2, Leased: 2, Draining: 0), TimeSpan.FromSeconds(60),
                depthSignal: false, waitSignal: false, undersized: false),
            observeGauges: true);

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(whileUndersized.Single(r => r.Instrument == "octo.lease.pool.undersized").Value)
            .IsEqualTo(1);
        await Assert.That(afterGrowing.Single(r => r.Instrument == "octo.lease.pool.undersized").Value)
            .IsEqualTo(0);
    }

    /// <summary>
    ///     A pool that stops being scheduled has to stop publishing. A depth gauge frozen at "40
    ///     queued" for a pool that no longer exists is an alert that can never clear, and an alert
    ///     that never clears is one nobody reads.
    /// </summary>
    [Test]
    public async Task APoolNoRoundHasObservedForTheHorizon_StopsPublishing()
    {
        // Arrange
        var poolRtId = UniquePool();
        AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
            new Dictionary<string, int> { ["tenant-a"] = 40 }, TimeSpan.FromSeconds(600),
            (Available: 0, Leased: 3, Draining: 0), TimeSpan.FromSeconds(60),
            depthSignal: true, waitSignal: true, undersized: true);

        // Act — a sweep one minute inside the horizon keeps it, one minute past it drops it.
        var justInside = Collect(poolRtId,
            () => AdapterLeasingMetrics.SweepStaleDeploymentSites(
                DateTime.UtcNow + AdapterLeasingMetrics.PoolObservationStaleAfter - TimeSpan.FromMinutes(1)),
            observeGauges: true);

        var pastTheHorizon = Collect(poolRtId,
            () => AdapterLeasingMetrics.SweepStaleDeploymentSites(
                DateTime.UtcNow + AdapterLeasingMetrics.PoolObservationStaleAfter + TimeSpan.FromMinutes(1)),
            observeGauges: true);

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(justInside.Any(r => r.Instrument == "octo.lease.queue.depth")).IsTrue();
        await Assert.That(pastTheHorizon).IsEmpty();
    }

    /// <summary>
    ///     🔴 The cardinality decision, pinned. A member id is bounded at any instant by
    ///     <c>MaxReplicas</c> and unbounded over time, because it changes on every pod restart — and
    ///     draining is precisely the path that restarts members, so this label would grow fastest
    ///     exactly when the counter is firing. Same for a lease or execution id, which is one series
    ///     per work item.
    /// </summary>
    [Test]
    public async Task NoInstrumentIsTaggedWithAMemberLeaseOrExecutionId()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act — one measurement of every instrument that can be driven from here.
        var recorded = Collect(poolRtId, () =>
        {
            AdapterLeasingMetrics.RecordEnqueued(Borrower, Lender, poolRtId);
            AdapterLeasingMetrics.RecordGranted(Borrower, Lender, poolRtId);
            AdapterLeasingMetrics.RecordQueueWait(Borrower, Lender, poolRtId, TimeSpan.FromSeconds(3));
            AdapterLeasingMetrics.RecordRefused(Borrower, Lender, poolRtId, LeaseStage.Schedule,
                LeaseRefusalReason.PoolExhausted);
            AdapterLeasingMetrics.RecordReleased(Borrower, Lender, poolRtId, "completed", true,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
            AdapterLeasingMetrics.RecordInterrupted(Borrower, Lender, poolRtId,
                LeaseInterruptReason.MemberLost, TimeSpan.FromSeconds(3));
            AdapterLeasingMetrics.RecordRequeued(Borrower, Lender, poolRtId, LeaseInterruptReason.MemberLost);
            AdapterLeasingMetrics.RecordMemberDrained(Lender, poolRtId, LeaseDrainReason.TtlExpiry);
            AdapterLeasingMetrics.RecordScaleUp(Lender, poolRtId,
                AdapterLeasingMetrics.ScaleUpOutcomes.AtCeiling);
            AdapterLeasingMetrics.ObserveRound(Lender, poolRtId,
                new Dictionary<string, int> { ["tenant-a"] = 1 }, TimeSpan.FromSeconds(1),
                (1, 1, 0), TimeSpan.FromSeconds(60), false, false, false);
        }, observeGauges: true);

        // Assert
        var forbidden = new[] { "member_id", "memberid", "lease_id", "leaseid", "execution", "pipeline" };
        var offenders = recorded
            .SelectMany(r => r.Tags.Keys.Select(k => (r.Instrument, Tag: k)))
            .Where(t => forbidden.Any(f => t.Tag.Contains(f, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        using var _ = Assert.Multiple();
        await Assert.That(offenders).IsEmpty();
        // And the instruments really were exercised — an empty recording would satisfy the check
        // above for the wrong reason.
        await Assert.That(recorded.Select(r => r.Instrument).Distinct().Count()).IsGreaterThanOrEqualTo(12);
    }

    /// <summary>
    ///     Drains are counted against the pool, never against the member. A drain loop is a property
    ///     of the pool; the member id is the thing the loop keeps changing.
    /// </summary>
    [Test]
    public async Task ADrain_IsCountedAgainstThePool()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () =>
        {
            AdapterLeasingMetrics.RecordMemberDrained(Lender, poolRtId, LeaseDrainReason.TtlExpiry);
            AdapterLeasingMetrics.RecordMemberDrained(Lender, poolRtId, LeaseDrainReason.TtlExpiry);
        });

        // Assert
        var drains = recorded.Where(r => r.Instrument == "octo.lease.member_drained.count").ToList();

        using var _ = Assert.Multiple();
        await Assert.That(drains).Count().IsEqualTo(2);
        // The same series both times — that is what makes rate() over a short window a drain loop.
        await Assert.That(drains.Select(d => string.Join(",", d.Tags.OrderBy(t => t.Key))).Distinct().Count())
            .IsEqualTo(1);
        await Assert.That(drains[0].Tags["octo.lease.drain_reason"]).IsEqualTo("ttl_expiry");
    }

    /// <summary>
    ///     The scale-up decisions the signals produced, next to the signals themselves. Without the
    ///     outcome label "the pool wanted to grow" and "the pool grew" are the same series.
    /// </summary>
    [Test]
    public async Task ScaleUpDecisions_AreCountedByOutcome()
    {
        // Arrange
        var poolRtId = UniquePool();

        // Act
        var recorded = Collect(poolRtId, () =>
        {
            AdapterLeasingMetrics.RecordScaleUp(Lender, poolRtId, AdapterLeasingMetrics.ScaleUpOutcomes.Scaled);
            AdapterLeasingMetrics.RecordScaleUp(Lender, poolRtId,
                AdapterLeasingMetrics.ScaleUpOutcomes.AtCeiling);
            AdapterLeasingMetrics.RecordScaleUp(Lender, poolRtId, AdapterLeasingMetrics.ScaleUpOutcomes.Failed);
        });

        // Assert
        var outcomes = recorded.Where(r => r.Instrument == "octo.lease.scaleup.count")
            .Select(r => r.Tags["octo.lease.scaleup.outcome"])
            .ToList();

        await Assert.That(outcomes).IsEquivalentTo(new[] { "scaled", "at_ceiling", "failed" });
    }

    /// <summary>
    ///     A pool name learned by one round must survive a later round that could not read the
    ///     entity. A dashboard nobody can read is not observability.
    /// </summary>
    [Test]
    public async Task APoolNameOnceLearned_IsNotBlankedOutByALaterRoundThatDidNotKnowIt()
    {
        // Arrange
        var poolRtId = UniquePool();
        AdapterLeasingMetrics.NamePool(Lender, poolRtId, "shared-pool");

        // Act
        var recorded = Collect(poolRtId, () =>
        {
            AdapterLeasingMetrics.NamePool(Lender, poolRtId, null);
            AdapterLeasingMetrics.RecordGranted(Borrower, Lender, poolRtId);
        });

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.granted.count")
            .Tags["octo.pool.name"]).IsEqualTo("shared-pool");
    }
}
