using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#4924 increment 9 (plan §11) — the instruments as the <b>scheduler</b> drives them.
/// </summary>
/// <remarks>
///     The scheduler owns everything continuous about a pool: how deep each borrower's queue is, how
///     long the oldest item has waited, how many members exist and in what state, and which scale-up
///     signals fired. None of that is logged per round — a round is every five seconds — so these
///     gauges are the only place it exists.
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
internal class SchedulerMetricsTests : LeaseSchedulerServiceTestsBase
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    private List<Recorded> Collect(Func<Task> act, bool observeGauges = false)
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

        act().GetAwaiter().GetResult();

        if (observeGauges)
        {
            listener.RecordObservableInstruments();
        }

        // This test instance's own pool only: the instruments are process-wide statics and the suite
        // runs concurrently.
        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.pool.rt_id") == PoolRtId.ToString())
            .ToList();
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

    /// <summary>
    ///     §9.2 chose round-robin over global FIFO to prevent starvation. Depth published per
    ///     borrowing tenant is what makes starvation visible; one aggregate number would show the
    ///     tenant with 200 items and the tenant with 1 as a single "201".
    /// </summary>
    [Test]
    public async Task ARound_PublishesQueueDepthPerBorrowingTenant()
    {
        // Arrange
        ArrangePool();
        ArrangeMembers(1);
        var starving = $"starving-{Guid.NewGuid():N}";
        var noisy = $"noisy-{Guid.NewGuid():N}";
        ArrangeQueue(starving, Entry("s-1", minutesAgo: 10));
        ArrangeQueue(noisy, Enumerable.Range(0, 5).Select(i => Entry($"n-{i}", minutesAgo: 1)).ToArray());

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync(), observeGauges: true);

        // Assert
        var depths = recorded.Where(r => r.Instrument == "octo.lease.queue.depth")
            .ToDictionary(r => r.Tags["octo.tenant.id"], r => r.Value);

        using var _ = Assert.Multiple();
        await Assert.That(depths.ContainsKey(starving)).IsTrue();
        await Assert.That(depths.ContainsKey(noisy)).IsTrue();
        await Assert.That(depths[starving]).IsEqualTo(1);
        await Assert.That(depths[noisy]).IsEqualTo(5);
        // The pool is named from the entity, so the dashboard reads "shared-pool" and not an rtId.
        await Assert.That(recorded.All(r => r.Tags["octo.pool.name"] == "shared-pool")).IsTrue();
    }

    /// <summary>
    ///     The other half of the fairness question. Equal served-counts with wildly unequal waits is
    ///     not fairness, and the wait is only knowable here — the lease service also serves the
    ///     hand-driven endpoint, which has no <c>QueuedAt</c>.
    /// </summary>
    [Test]
    public async Task AGrantedWorkItem_RecordsItsQueueWaitAgainstItsOwnTenant()
    {
        // Arrange
        ArrangePool();
        ArrangeMembers(1);
        var tenantId = $"waiting-{Guid.NewGuid():N}";
        ArrangeQueue(tenantId, Entry("w-1", minutesAgo: 5));

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync());

        // Assert
        var wait = recorded.Single(r => r.Instrument == "octo.lease.queue.wait");

        using var _ = Assert.Multiple();
        await Assert.That(wait.Tags["octo.tenant.id"]).IsEqualTo(tenantId);
        // Five minutes ago, give or take the time the round itself took.
        await Assert.That(wait.Value).IsGreaterThanOrEqualTo(300);
        await Assert.That(wait.Value).IsLessThan(360);
    }

    /// <summary>
    ///     Concept §6, "pool exhausted": the queue grows and nothing is dropped. Counted per
    ///     borrowing tenant, because "which tenant is not being served" is the question this state
    ///     raises and the remedy differs from the per-tenant cap's.
    /// </summary>
    [Test]
    public async Task APoolWithNoIdleMember_CountsAPoolExhaustedRefusalForEveryWaitingTenant()
    {
        // Arrange — one busy member, nothing idle.
        ArrangePool();
        var busyFor = $"busy-{Guid.NewGuid():N}";
        ArrangeBusyMember(busyFor);
        var tenantA = $"a-{Guid.NewGuid():N}";
        var tenantB = $"b-{Guid.NewGuid():N}";
        ArrangeQueue(tenantA, Entry("a-1", minutesAgo: 1));
        ArrangeQueue(tenantB, Entry("b-1", minutesAgo: 1));

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync());

        // Assert
        var refusals = recorded.Where(r => r.Instrument == "octo.lease.refused.count").ToList();

        using var _ = Assert.Multiple();
        await Assert.That(refusals).Count().IsEqualTo(2);
        await Assert.That(refusals.All(r => r.Tags["octo.lease.refusal_reason"] == "pool_exhausted")).IsTrue();
        await Assert.That(refusals.All(r => r.Tags["octo.lease.stage"] == "schedule")).IsTrue();
        await Assert.That(refusals.Select(r => r.Tags["octo.tenant.id"]).Order())
            .IsEquivalentTo(new[] { tenantA, tenantB }.Order().ToArray());
    }

    /// <summary>
    ///     A tenant held back by <c>LendingMaxConcurrentLeasesPerTenant</c> looks identical to a full
    ///     pool in the queue view and has the opposite remedy — raise the cap, not the pool.
    /// </summary>
    [Test]
    public async Task ATenantBlockedByItsConcurrencyCap_IsCountedAsTheCapAndNotAsExhaustion()
    {
        // Arrange — cap of one, already used by a busy member serving this tenant, and a free member.
        ArrangePool(maxConcurrentLeasesPerTenant: 1);
        var tenantId = $"capped-{Guid.NewGuid():N}";
        ArrangeBusyMember(tenantId);
        ArrangeMembers(1);
        ArrangeQueue(tenantId, Entry("c-1", minutesAgo: 1));

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync());

        // Assert
        var refusal = recorded.Single(r => r.Instrument == "octo.lease.refused.count");

        using var _ = Assert.Multiple();
        await Assert.That(refusal.Tags["octo.lease.refusal_reason"]).IsEqualTo("per_tenant_cap");
        await Assert.That(refusal.Tags["octo.tenant.id"]).IsEqualTo(tenantId);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.granted.count")).IsFalse();
    }

    /// <summary>
    ///     Q14 says to instrument before fixing a window. That means publishing both signals AND the
    ///     window that produced them, from the round that evaluated them.
    /// </summary>
    [Test]
    public async Task ARound_PublishesTheWaitSignalAndTheWindowItWasEvaluatedWith()
    {
        // Arrange — a wait threshold the queued item has long passed.
        ArrangePool(minReplicas: 1, maxReplicas: 3, scaleUpQueueDepthThreshold: 500,
            scaleUpQueueWaitSeconds: 30);
        ArrangeMembers(1);
        ArrangeQueue($"t-{Guid.NewGuid():N}", Entry("q-1", minutesAgo: 10));

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync(), observeGauges: true);

        // Assert
        var signals = recorded.Where(r => r.Instrument == "octo.lease.scaleup.signal")
            .ToDictionary(r => r.Tags["octo.lease.scaleup.signal_kind"], r => r.Value);

        using var _ = Assert.Multiple();
        await Assert.That(signals["wait"]).IsEqualTo(1);
        await Assert.That(signals["depth"]).IsEqualTo(0);
        // The window is DERIVED from the pool's own ScaleUpQueueWaitSeconds while the option is 0
        // (D3). Publishing it is what lets a measurement campaign attribute a decision to a window.
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.scaleup.window").Value)
            .IsEqualTo(30);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.scaleup.count")
            .Tags["octo.lease.scaleup.outcome"]).IsEqualTo("scaled");
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.pool.undersized").Value)
            .IsEqualTo(0);
    }

    /// <summary>
    ///     🔴 The alertable condition. A pool whose signals say grow and that is already at
    ///     <c>MaxReplicas</c> cannot help itself, and that is the one leasing state that needs a
    ///     human. Published as the answer so the alert stays a threshold on one series.
    /// </summary>
    [Test]
    public async Task APoolAtItsCeilingWithWorkWaiting_PublishesUndersizedAndCountsTheCeiling()
    {
        // Arrange — MaxReplicas 1, one member already serving somebody, work waiting far too long.
        ArrangePool(minReplicas: 1, maxReplicas: 1, scaleUpQueueDepthThreshold: 500,
            scaleUpQueueWaitSeconds: 30);
        ArrangeBusyMember($"busy-{Guid.NewGuid():N}");
        ArrangeQueue($"t-{Guid.NewGuid():N}", Entry("q-1", minutesAgo: 10));

        // Act
        var recorded = Collect(() => Scheduler.RunSchedulingRoundAsync(), observeGauges: true);

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.pool.undersized").Value)
            .IsEqualTo(1);
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.scaleup.count")
            .Tags["octo.lease.scaleup.outcome"]).IsEqualTo("at_ceiling");
        // And the pool never grew.
        await PoolService.DidNotReceive().ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<ConstructionKit
            .Contracts.OctoObjectId>(), Arg.Any<int>());
    }

    /// <summary>
    ///     Concept §6: a member whose lease expired is drained and replaced, not re-used. The drain
    ///     is counted against the POOL — a drain loop is a property of the pool, and the member id is
    ///     the one thing the loop keeps changing, so tagging by it would grow the label set fastest
    ///     exactly while the loop runs.
    /// </summary>
    [Test]
    public async Task AnExpiredLease_CountsTheDrainAgainstThePoolWithoutNamingTheMember()
    {
        // Arrange
        var borrower = $"t-{Guid.NewGuid():N}";
        ArrangeBusyMember(borrower, expiresAtUtc: DateTime.UtcNow.AddSeconds(-1));
        LeaseService.InterruptAndRequeueAsync(Arg.Any<Communication.Contracts.DataTransferObjects.LeaseDto>(),
            Arg.Any<LeaseInterruptReason>(), Arg.Any<string>()).Returns("retry-execution-id");

        // Act
        var recorded = Collect(() => Scheduler.ReapExpiredLeasesAsync());

        // Assert
        var drain = recorded.Single(r => r.Instrument == "octo.lease.member_drained.count");

        using var _ = Assert.Multiple();
        await Assert.That(drain.Tags["octo.lease.drain_reason"]).IsEqualTo("ttl_expiry");
        await Assert.That(drain.Tags["octo.pool.tenant_id"]).IsEqualTo(LenderTenantId);
        await Assert.That(drain.Tags.Keys.Any(k => k.Contains("member_id"))).IsFalse();
    }
}
