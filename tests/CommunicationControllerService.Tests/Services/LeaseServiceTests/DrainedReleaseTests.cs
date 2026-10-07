using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#5864 — a member that releases with <c>Reason=Drained</c> is granted nothing further, and a
///     lease it refused without running the work item puts that work back into the queue instead of
///     failing it.
/// </summary>
/// <remarks>
///     The test-2-dev repro (2026-10-07): a lease participant failed to leave, the member drained
///     itself and said so, the controller kept it available, granted it the next lease, the member
///     refused it in under 400 ms, and the borrower's execution was set to FAILED — for every grant
///     until the pod was deleted.
/// </remarks>
internal class DrainedReleaseTests : LeaseServiceTestsBase
{
    private const string ExecutionId = "7a1e5c22-0b8d-4f6e-9a51-3c2d1e0f5864";
    private const string SecondConnectionId = "conn-pool-member-2";
    private const string SecondMemberId = "octo-pool-1";

    /// <summary>The service's own cap, so the test and the guard cannot drift apart.</summary>
    private static readonly int LeaseServiceMaxRefusals =
        CommunicationControllerServices.Services.LeaseService.MaxRefusedLeaseRequeues;

    private static readonly DateTime OriginalQueuedAt = new(2026, 10, 7, 19, 18, 57, DateTimeKind.Utc);

    private async Task<string> GrantAsync(string executionId = ExecutionId)
    {
        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(executionId));
        await Assert.That(result.Granted).IsTrue();
        return result.LeaseId!;
    }

    private static LeaseResultDto ARefusal(string leaseId) => new()
    {
        LeaseId = leaseId,
        Reason = LeaseReleaseReasonDto.Drained,
        Success = false,
        StatusMessage = "The member is draining and did not take the lease.",
        // The member stamps no work span on a lease it never ran.
        WorkDurationMs = null
    };

    /// <summary>
    ///     Every execution id is interruptible and the stored <c>QueuedAt</c> is the original one, so a
    ///     chain of refusals can be followed through as many attempts as a test needs.
    /// </summary>
    private void ArrangeInterruptible()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        CommunicationRepository.TryInterruptLeasedExecutionAsync(BorrowerTenantId, Arg.Any<string>(),
                Arg.Any<DateTime>(), Arg.Any<string>())
            .Returns(new InterruptedLeasedExecution(
                new RtEntityId(pipeline.CkTypeId!, pipeline.RtId),
                new RtEntityId(Borrower.CkTypeId!, Borrower.RtId),
                RtPipelineTriggerTypeEnum.Scheduled,
                "{\"a\":1}",
                QueuedAt: OriginalQueuedAt));
    }

    private void ArrangeRunningExecution(string executionId = ExecutionId)
    {
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, executionId)
            .Returns(new RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                ExecutionId = executionId,
                Status = RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-1)
            });
    }

    /// <summary>Acceptance criterion 1: the registry records the drain.</summary>
    [Test]
    public async Task ADrainedRelease_MarksTheMemberDrainingInTheRegistry()
    {
        // Arrange
        ArrangeGrantableLease();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = true,
            WorkDurationMs = 1_200,
            StatusMessage = "A lease participant failed to release tenant state"
        });

        // Assert
        var member = ConnectionManager.TryGetMember(ConnectionId)!;
        using var _ = Assert.Multiple();
        await Assert.That(member.ActiveLease).IsNull();
        await Assert.That(member.IsDraining).IsTrue();
        await Assert.That(member.IsAvailable).IsFalse();
    }

    /// <summary>
    ///     🔴 Acceptance criterion 1, the half that killed the pool: no further grant to that member.
    ///     With a second, healthy member the next lease goes there; without one, nothing is granted
    ///     and the work stays queued.
    /// </summary>
    [Test]
    public async Task ADrainedMember_IsNotGrantedTheNextLease()
    {
        // Arrange
        ArrangeGrantableLease();
        var leaseId = await GrantAsync();
        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = true,
            WorkDurationMs = 1_200
        });

        // Act
        var withOnlyTheDrainedMember =
            await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-2"));
        ArrangeConnectedMember(SecondConnectionId, SecondMemberId);
        var withAHealthyMember =
            await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest("exec-3"));

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(withOnlyTheDrainedMember.Granted).IsFalse();
        await Assert.That(withOnlyTheDrainedMember.Reason).IsEqualTo(LeaseRefusalReason.NoIdleMember);
        await Assert.That(withAHealthyMember.Granted).IsTrue();
        await Assert.That(withAHealthyMember.MemberId).IsEqualTo(SecondMemberId);
    }

    /// <summary>
    ///     A Drained release that names a lease the member no longer holds frees nothing — but the
    ///     drain it reports is still true about the process, whose flag never resets.
    /// </summary>
    [Test]
    public async Task AStaleDrainedRelease_StillMarksTheMemberDraining()
    {
        // Arrange
        ArrangeGrantableLease();
        await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, ARefusal("a-lease-from-a-previous-life"));

        // Assert
        var member = ConnectionManager.TryGetMember(ConnectionId)!;
        using var _ = Assert.Multiple();
        await Assert.That(member.ActiveLease).IsNotNull();
        await Assert.That(member.IsDraining).IsTrue();
    }

    /// <summary>
    ///     🔴 Acceptance criterion 2: refused work is re-queued, not failed — and at the position it
    ///     held, with its trigger and input, so a refusal neither reorders the tenant's queue nor
    ///     changes what runs.
    /// </summary>
    [Test]
    public async Task ALeaseRefusedWithoutRunning_IsRequeuedAtItsOriginalPosition_NotFailed()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeInterruptible();
        ArrangeRunningExecution();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, ARefusal(leaseId));

        // Assert
        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Is<string>(m => m.Contains("refused")));
        await CommunicationRepository.Received(1).EnqueueExecutionAsync(BorrowerTenantId,
            Arg.Is<RtPipelineExecution>(e =>
                e.ExecutionId != ExecutionId && e.TriggerType == RtPipelineTriggerTypeEnum.Scheduled &&
                e.InputData == "{\"a\":1}"),
            Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), OriginalQueuedAt);
        await CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<RtPipelineExecutionStatusEnum>(), Arg.Any<DateTime?>(), Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<string?>());
        await EventService.DidNotReceive().StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
        await EventService.Received(1).StoreInformationEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("re-queued")));
    }

    /// <summary>
    ///     The returned item must not wait for the scheduler's tick: another member may be idle right
    ///     now. (A Drained release that returned nothing still wakes nobody — see
    ///     <c>ReleaseAndDisconnectTests.ADrainedRelease_DoesNotPullTheNextRoundForward</c>.)
    /// </summary>
    [Test]
    public async Task ALeaseRefusedWithoutRunning_PullsTheNextRoundForward()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeInterruptible();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, ARefusal(leaseId));

        // Assert
        WakeSignal.Received(1).RequestRound(Arg.Is<string>(r => r.Contains("re-queued")));
    }

    /// <summary>
    ///     🔴 A Drained release WITH a work span ran the pipeline. Re-queuing it would run work that
    ///     already happened a second time, so its own outcome — here a success, with output — is
    ///     applied instead.
    /// </summary>
    [Test]
    public async Task ADrainedReleaseThatRanTheWorkItem_AppliesItsOutcome_AndDoesNotRequeue()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeInterruptible();
        ArrangeRunningExecution();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = true,
            OutputData = "{\"ok\":true}",
            WorkDurationMs = 1_400,
            StatusMessage = "Completed; the member drains: a lease participant failed to leave"
        });

        // Assert
        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, ExecutionId,
            RtPipelineExecutionStatusEnum.Completed, Arg.Any<DateTime?>(), Arg.Any<int?>(), null, "{\"ok\":true}");
        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     A Drained release whose work item ran and failed is the pipeline's failure: it is applied
    ///     and lands in the borrower's event log like any Failed release.
    /// </summary>
    [Test]
    public async Task ADrainedReleaseWhoseWorkItemFailed_IsAppliedAndReported()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeRunningExecution();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = false,
            WorkDurationMs = 900,
            StatusMessage = "node 3 threw"
        });

        // Assert
        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, ExecutionId,
            RtPipelineExecutionStatusEnum.Failed, Arg.Any<DateTime?>(), Arg.Any<int?>(), "node 3 threw",
            Arg.Any<string?>());
        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("node 3 threw")));
    }

    /// <summary>
    ///     🔴 The loop guard. Each refusal comes from a different member (the refusing one is
    ///     draining and never granted again), exactly as a grant-vs-drain race would produce it. The
    ///     first <see cref="LeaseServiceMaxRefusals" /> refusals re-queue; the next one fails the work
    ///     item loudly instead of circling.
    /// </summary>
    [Test]
    public async Task AWorkItemRefusedTooOften_IsFailedInsteadOfRequeuedForever()
    {
        // Arrange
        ArrangeBorrower();
        ArrangeLendingPool(lends: true);
        ArrangeBorrowerCredential();
        ArrangeInterruptible();

        var requeuedIds = new List<string>();
        CommunicationRepository
            .When(r => r.EnqueueExecutionAsync(BorrowerTenantId, Arg.Any<RtPipelineExecution>(),
                Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>()))
            .Do(call => requeuedIds.Add(call.Arg<RtPipelineExecution>().ExecutionId!));

        var executionId = ExecutionId;

        // Act
        for (var attempt = 0; attempt <= LeaseServiceMaxRefusals; attempt++)
        {
            var connectionId = $"conn-refusing-{attempt}";
            ArrangeConnectedMember(connectionId, $"octo-pool-refusing-{attempt}");
            ArrangeRunningExecution(executionId);

            var leaseId = await GrantAsync(executionId);
            await LeaseService.ReleaseLeaseAsync(connectionId, ARefusal(leaseId));

            if (attempt < LeaseServiceMaxRefusals)
            {
                executionId = requeuedIds[^1];
            }
        }

        // Assert
        using var _ = Assert.Multiple();
        await Assert.That(requeuedIds).Count().IsEqualTo(LeaseServiceMaxRefusals);
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, executionId,
            RtPipelineExecutionStatusEnum.Failed, Arg.Any<DateTime?>(), Arg.Any<int?>(),
            Arg.Is<string?>(m => m!.Contains($"refused by {LeaseServiceMaxRefusals + 1} adapter pool members")), Arg.Any<string?>());
        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains(executionId)));
    }

    /// <summary>
    ///     A <c>Failed</c> release without a work span is NOT a refusal: it is a lease that could not be
    ///     entered (credential, CK model), which fails the same way on every member and must surface
    ///     rather than loop.
    /// </summary>
    [Test]
    public async Task AFailedReleaseWithoutAWorkSpan_IsStillFailed_NotRequeued()
    {
        // Arrange
        ArrangeGrantableLease();
        ArrangeInterruptible();
        ArrangeRunningExecution();
        var leaseId = await GrantAsync();

        // Act
        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Failed,
            Success = false,
            StatusMessage = "borrower login failed"
        });

        // Assert
        using var _ = Assert.Multiple();
        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, ExecutionId,
            RtPipelineExecutionStatusEnum.Failed, Arg.Any<DateTime?>(), Arg.Any<int?>(), "borrower login failed",
            Arg.Any<string?>());
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
    }
}
