using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#4924 §9.3/§9.4 — expired leases and queue-driven scale-up.
/// </summary>
internal class ReapAndScaleUpTests : LeaseSchedulerServiceTestsBase
{
    /// <summary>
    ///     Concept §6, "Release never arrives": the TTL releases the lease server-side, the previous
    ///     attempt is interrupted and re-queued, and the member is <b>drained and restarted</b> rather
    ///     than re-used, because its post-lease cleanliness is unproven.
    /// </summary>
    [Test]
    public async Task AnExpiredLease_IsInterruptedRequeuedAndTheMemberIsDrained()
    {
        var connectionId = ArrangeBusyMember(TenantA, expiresAtUtc: DateTime.UtcNow.AddSeconds(-1));
        LeaseService.InterruptAndRequeueAsync(Arg.Any<LeaseDto>(), Arg.Any<LeaseInterruptReason>(), Arg.Any<string>())
            .Returns("retry-execution-id");

        var reaped = await Scheduler.ReapExpiredLeasesAsync();

        using var _ = Assert.Multiple();
        await Assert.That(reaped).IsEqualTo(1);
        await LeaseService.Received(1).InterruptAndRequeueAsync(
            Arg.Is<LeaseDto>(l => l.TenantId == TenantA), LeaseInterruptReason.TtlExpiry, Arg.Any<string>());
        await LeaseService.Received(1).DrainMemberAsync(connectionId, Arg.Any<string>());
        // Drained AND no longer holding the lease: both, or the member is either re-used or wedged.
        await Assert.That(ConnectionManager.TryGetMember(connectionId)!.ActiveLease).IsNull();
        await Assert.That(ConnectionManager.TryGetMember(connectionId)!.IsDraining).IsTrue();
        await Assert.That(ConnectionManager.TryGetMember(connectionId)!.IsAvailable).IsFalse();
        await EventService.Received(1).StoreErrorEventAsync(TenantA,
            Arg.Is<string>(m => m.Contains("retry-execution-id")));
    }

    /// <summary>
    ///     A lease that is still inside its TTL is left strictly alone, however long it has run.
    /// </summary>
    [Test]
    public async Task ALeaseInsideItsTtl_IsNotReaped()
    {
        var connectionId = ArrangeBusyMember(TenantA, expiresAtUtc: DateTime.UtcNow.AddMinutes(5));

        var reaped = await Scheduler.ReapExpiredLeasesAsync();

        using var _ = Assert.Multiple();
        await Assert.That(reaped).IsEqualTo(0);
        await LeaseService.DidNotReceive().InterruptAndRequeueAsync(Arg.Any<LeaseDto>(), Arg.Any<LeaseInterruptReason>(), Arg.Any<string>());
        await Assert.That(ConnectionManager.TryGetMember(connectionId)!.ActiveLease).IsNotNull();
    }

    /// <summary>
    ///     The wait signal fires on the oldest waiting item alone — it needs no averaging window,
    ///     because an item that has waited a minute has already integrated a minute of pressure.
    /// </summary>
    [Test]
    public async Task TheWaitSignal_ScalesThePoolUpByOne()
    {
        var pool = ArrangePool(minReplicas: 1, maxReplicas: 3, scaleUpQueueDepthThreshold: 50,
            scaleUpQueueWaitSeconds: 30, policy: RtPoolScaleUpPolicyEnum.QueueDepthOrWaitSeconds);

        ArrangeQueue(TenantA, Entry("a-0", minutesAgo: 10));
        ArrangeBusyMember(TenantB);

        await Scheduler.RunSchedulingRoundAsync();

        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, pool.RtId, 2);
    }

    /// <summary>
    ///     🔴 The depth signal is averaged over a window, and a single round is not an average. One
    ///     round over a deep queue must NOT scale — otherwise a burst that drains in seconds starts a
    ///     process that is idle by the time it is ready.
    /// </summary>
    [Test]
    public async Task TheDepthSignal_DoesNotFireOnASingleSample()
    {
        ArrangePool(scaleUpQueueDepthThreshold: 2, scaleUpQueueWaitSeconds: 0,
            policy: RtPoolScaleUpPolicyEnum.QueueDepth);

        ArrangeQueue(TenantA, Entry("a-0", 0), Entry("a-1", 0), Entry("a-2", 0), Entry("a-3", 0));
        ArrangeBusyMember(TenantB);

        await Scheduler.RunSchedulingRoundAsync();

        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
    }

    /// <summary>
    ///     With the window configured down to its minimum, two rounds spanning it do fire.
    /// </summary>
    [Test]
    public async Task TheDepthSignal_FiresOnceTheWindowIsCovered()
    {
        // 🔴 One second, set explicitly: the point of the option is that the window is configurable
        // rather than a constant, and a test that could only pass with the default would not show it.
        Options.LeaseScaleUpAveragingWindowSeconds = 1;

        var pool = ArrangePool(scaleUpQueueDepthThreshold: 2, scaleUpQueueWaitSeconds: 0,
            policy: RtPoolScaleUpPolicyEnum.QueueDepth);

        ArrangeQueue(TenantA, Entry("a-0", 0), Entry("a-1", 0), Entry("a-2", 0), Entry("a-3", 0));
        ArrangeBusyMember(TenantB);

        await Scheduler.RunSchedulingRoundAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await Scheduler.RunSchedulingRoundAsync();

        await DeploymentSiteService.Received(1).ScaleAdapterPoolAsync(LenderTenantId, pool.RtId, 2);
    }

    /// <summary>
    ///     A pool already at its ceiling is never asked to grow; the operator is told instead
    ///     (concept §6, "Pool exhausted").
    /// </summary>
    [Test]
    public async Task AtMaxReplicas_TheCeilingIsReportedAndNoScaleIsRequested()
    {
        ArrangePool(minReplicas: 1, maxReplicas: 1, scaleUpQueueWaitSeconds: 30);

        ArrangeQueue(TenantA, Entry("a-0", minutesAgo: 10));
        ArrangeBusyMember(TenantB);

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
        await EventService.Received(1).StoreErrorEventAsync(LenderTenantId,
            Arg.Is<string>(m => m.Contains("ceiling")));
    }

    /// <summary>
    ///     An empty queue never scales anything, whatever the policy says.
    /// </summary>
    [Test]
    public async Task AnEmptyQueue_NeverScales()
    {
        ArrangePool(scaleUpQueueDepthThreshold: 0, scaleUpQueueWaitSeconds: 1);
        ArrangeQueue(TenantA);
        ArrangeMembers(1);

        await Scheduler.RunSchedulingRoundAsync();

        await DeploymentSiteService.DidNotReceive()
            .ScaleAdapterPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<int>());
    }
}
