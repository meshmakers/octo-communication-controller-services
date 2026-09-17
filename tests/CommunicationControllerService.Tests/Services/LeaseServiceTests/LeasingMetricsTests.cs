using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 increment 9 (plan §11) — the instruments as the <b>lease service</b> drives them.
/// </summary>
/// <remarks>
///     <c>AdapterLeasingMetricsTests</c> pins what the instruments look like; this pins that the
///     service actually records them, on the paths that matter. The two are separate on purpose: a
///     metric that is shaped perfectly and never emitted looks exactly like coverage.
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
internal class LeasingMetricsTests : LeaseServiceTestsBase
{
    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    private List<Recorded> Collect(Func<Task> act)
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
        // 🔴 The instruments have to EXIST before the listener starts. They are static fields of
        // AdapterLeasingMetrics, so the first test in the process to touch that class is the one
        // that creates them — and if that happens inside the act below, it happens while this
        // listener is already running and racing its own subscription. Forcing the class
        // constructor here makes every run look like the second one.
        RuntimeHelpers.RunClassConstructor(typeof(AdapterLeasingMetrics).TypeHandle);

        listener.Start();

        act().GetAwaiter().GetResult();

        // Filtered on this test instance's own pool, because the instruments are process-wide and
        // the suite runs concurrently.
        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.pool.rt_id") == AdapterPoolRtId.ToString())
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

    [Test]
    public async Task AGrantedLease_IsCountedAgainstTheBorrowingTenant()
    {
        // Arrange
        ArrangeGrantableLease();

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1")));

        // Assert
        var granted = recorded.Single(r => r.Instrument == "octo.lease.granted.count");

        using var _ = Assert.Multiple();
        await Assert.That(granted.Value).IsEqualTo(1);
        await Assert.That(granted.Tags["octo.tenant.id"]).IsEqualTo(BorrowerTenantId);
        await Assert.That(granted.Tags["octo.pool.tenant_id"]).IsEqualTo(LenderTenantId);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.refused.count")).IsFalse();
    }

    /// <summary>
    ///     §14's "the refusal names which half is missing" has to reach the metric, not only the
    ///     message: during a staged rollout one tenant being off is expected and the other being off
    ///     is a misconfiguration, and an aggregate cannot tell an operator which they are looking at.
    /// </summary>
    [Test]
    public async Task LeasingDisabledOnTheLender_IsCountedAsTheLendersHalf()
    {
        // Arrange
        ArrangeGrantableLease();
        LifecycleConfiguration.IsLeasingEnabledAsync(LenderTenantId).Returns(false);

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1")));

        // Assert
        var refused = recorded.Single(r => r.Instrument == "octo.lease.refused.count");

        using var _ = Assert.Multiple();
        await Assert.That(refused.Tags["octo.lease.refusal_reason"]).IsEqualTo("leasing_disabled_lender");
        await Assert.That(refused.Tags["octo.lease.stage"]).IsEqualTo("grant");
    }

    [Test]
    public async Task LeasingDisabledOnTheBorrower_IsCountedAsTheBorrowersHalf()
    {
        // Arrange
        ArrangeGrantableLease();
        LifecycleConfiguration.IsLeasingEnabledAsync(BorrowerTenantId).Returns(false);

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1")));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.refused.count")
            .Tags["octo.lease.refusal_reason"]).IsEqualTo("leasing_disabled_borrower");
    }

    /// <summary>
    ///     A pipeline that cannot be projected leaves its work item queued forever until somebody
    ///     fixes it. That is the refusal reason most likely to be a silent, tenant-visible outage, so
    ///     it has to be distinguishable from "the pool is busy".
    /// </summary>
    [Test]
    public async Task AnUnprojectablePipeline_IsItsOwnRefusalReason()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeUnprojectablePipeline();

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest()));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.refused.count")
            .Tags["octo.lease.refusal_reason"]).IsEqualTo("pipeline_projection_failed");
    }

    /// <summary>
    ///     A lease attempt across a boundary the tenant tree does not allow is the shape a
    ///     cross-tenant incident would take. It is audited on the borrower's event log already; it
    ///     also has to be countable, because an event log is not something anyone alerts on.
    /// </summary>
    [Test]
    public async Task ALeaseOutsideTheLendingScope_IsItsOwnRefusalReason()
    {
        // Arrange
        ArrangeBorrower();
        ArrangeLendingPool(lends: false);
        ArrangeConnectedMember();

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1")));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.refused.count")
            .Tags["octo.lease.refusal_reason"]).IsEqualTo("lending_scope_denied");
    }

    [Test]
    public async Task NoIdleMember_IsItsOwnRefusalReason()
    {
        // Arrange — everything valid except that no member is connected.
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();

        // Act
        var recorded = Collect(() => LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1")));

        // Assert
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.refused.count")
            .Tags["octo.lease.refusal_reason"]).IsEqualTo("no_idle_member");
    }

    /// <summary>
    ///     🔴 Concept §2.3, end to end through the service. The member measured the work span and sent
    ///     it on the release; the controller measured the held span itself. The overhead between them
    ///     is the number the whole "dedicated for continuous data, leased for periodic work" argument
    ///     rests on.
    /// </summary>
    [Test]
    public async Task TheRelease_TurnsTheMembersWorkSpanIntoAnOverheadSample()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());
        var lease = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease!;

        // Act
        var recorded = Collect(() => LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = lease.LeaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            WorkDurationMs = 1
        }));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.held.duration")).IsTrue();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.work.duration").Value)
            .IsEqualTo(0.001);
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.overhead.duration")).IsTrue();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.released.count")
            .Tags["octo.lease.release_reason"]).IsEqualTo("completed");
    }

    /// <summary>
    ///     A release from a member that reports no work span records the held time and stays silent
    ///     about the rest, rather than inventing a zero that would read as 100 % overhead.
    /// </summary>
    [Test]
    public async Task AReleaseWithoutAWorkSpan_RecordsTheHeldTimeOnly()
    {
        // Arrange
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1"));
        var lease = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease!;

        // Act
        var recorded = Collect(() => LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = lease.LeaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = false
        }));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.held.duration")).IsTrue();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.work.duration")).IsFalse();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.released.count")
            .Tags["octo.lease.outcome"]).IsEqualTo("failure");
    }

    /// <summary>
    ///     Concept §6, at-least-once: an interrupt and its re-queue are two different facts, and the
    ///     gap between their rates is work that was lost.
    /// </summary>
    [Test]
    public async Task AMemberThatVanishesMidLease_CountsAnInterruptAndItsRequeue()
    {
        // Arrange
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1"));
        var member = ConnectionManager.TryGetMember(ConnectionId)!;
        CommunicationRepository
            .TryInterruptLeasedExecutionAsync(BorrowerTenantId, "exec-1", Arg.Any<DateTime>(),
                Arg.Any<string>())
            .Returns(new InterruptedLeasedExecution(
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, PipelineRtId),
                new RtEntityId(Borrower.CkTypeId!, Borrower.RtId),
                RtPipelineTriggerTypeEnum.Manual,
                null));

        // Act
        var recorded = Collect(() => LeaseService.HandleMemberDisconnectedAsync(member));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.interrupted.count")
            .Tags["octo.lease.interrupt_reason"]).IsEqualTo("member_lost");
        await Assert.That(recorded.Single(r => r.Instrument == "octo.lease.requeued.count")
            .Tags["octo.lease.interrupt_reason"]).IsEqualTo("member_lost");
        // The held span is closed on this path too — it is a billing input, not only a diagnostic.
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.held.duration")).IsTrue();
    }

    /// <summary>
    ///     An attempt that could not be re-queued counts the interrupt and <b>not</b> the re-queue.
    ///     The difference between the two counters is the only place work lost to at-least-once shows
    ///     up at all.
    /// </summary>
    [Test]
    public async Task AnInterruptThatCouldNotBeRequeued_CountsOnlyTheInterrupt()
    {
        // Arrange
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-1"));
        var member = ConnectionManager.TryGetMember(ConnectionId)!;
        CommunicationRepository
            .TryInterruptLeasedExecutionAsync(BorrowerTenantId, "exec-1", Arg.Any<DateTime>(),
                Arg.Any<string>())
            .Returns((InterruptedLeasedExecution?)null);

        // Act
        var recorded = Collect(() => LeaseService.HandleMemberDisconnectedAsync(member));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.interrupted.count")).IsTrue();
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.requeued.count")).IsFalse();
    }
}
