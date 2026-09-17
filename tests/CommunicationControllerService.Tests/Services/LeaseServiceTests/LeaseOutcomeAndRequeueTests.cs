using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 §9.1/§9.3 — what a release writes on the borrower's execution, and what a crash or an
///     expiry mid-lease does with the work item.
/// </summary>
internal class LeaseOutcomeAndRequeueTests : LeaseServiceTestsBase
{
    private const string ExecutionId = "6f0d6f1c-6f2f-4f2a-9a43-2a0f0a3a9c11";

    private async Task<string> GrantWithWorkItemAsync()
    {
        ArrangeGrantableLease();
        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest(ExecutionId));
        return result.LeaseId!;
    }

    private static RtPipelineExecution ARunningExecution(DateTime? startedAt = null)
    {
        return new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
            ExecutionId = ExecutionId,
            Status = RtPipelineExecutionStatusEnum.Running,
            StartedAt = startedAt ?? DateTime.UtcNow.AddSeconds(-5)
        };
    }

    /// <summary>
    ///     🔴 <c>LeaseReleasedAt</c> is the end of the span a member was held for one borrower, and it
    ///     is a billing input (concept §4b). It is stamped on every release path.
    /// </summary>
    [Test]
    public async Task ACleanRelease_StampsLeaseReleasedAt()
    {
        var leaseId = await GrantWithWorkItemAsync();
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId)
            .Returns(ARunningExecution());

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true
        });

        await CommunicationRepository.Received(1)
            .StampLeaseReleasedAsync(BorrowerTenantId, ExecutionId, Arg.Any<DateTime>());
    }

    /// <summary>
    ///     The release is the controller's last-resort completion signal: an execution still Running
    ///     when the member hands the process back would otherwise be reaped as stuck fifteen minutes
    ///     later, with a message about an adapter restart that never happened.
    /// </summary>
    [Test]
    public async Task ARunningExecution_IsCompletedByTheRelease()
    {
        var leaseId = await GrantWithWorkItemAsync();
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId)
            .Returns(ARunningExecution());

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true
        });

        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, ExecutionId,
            RtPipelineExecutionStatusEnum.Completed, Arg.Any<DateTime?>(), Arg.Any<int?>(), null, Arg.Any<string?>());
    }

    /// <summary>
    ///     An execution that already reached a terminal state through the normal adapter path is left
    ///     exactly as it is. The lease release must never overwrite a result somebody else reported.
    /// </summary>
    [Test]
    public async Task AnAlreadyCompletedExecution_IsNotRewrittenByTheRelease()
    {
        var leaseId = await GrantWithWorkItemAsync();
        var execution = ARunningExecution();
        execution.Status = RtPipelineExecutionStatusEnum.Completed;
        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, ExecutionId).Returns(execution);

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Failed,
            Success = false,
            StatusMessage = "node 3 threw"
        });

        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1)
            .StampLeaseReleasedAsync(BorrowerTenantId, ExecutionId, Arg.Any<DateTime>());
        await CommunicationRepository.DidNotReceive().UpdatePipelineExecutionAsync(Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<RtPipelineExecutionStatusEnum>(), Arg.Any<DateTime?>(), Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<string?>());
    }

    /// <summary>
    ///     Concept §6, "Lease holder crashes mid-execution": at-least-once. The attempt is Interrupted
    ///     with its lease span closed, and a fresh attempt is enqueued.
    /// </summary>
    [Test]
    public async Task AMemberThatVanishesMidLease_InterruptsAndRequeuesTheWorkItem()
    {
        await GrantWithWorkItemAsync();
        ArrangeInterruptible();

        var member = ConnectionManager.TryGetMember(ConnectionId)!;
        await LeaseService.HandleMemberDisconnectedAsync(member);

        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Any<string>());
        await CommunicationRepository.Received(1).EnqueueExecutionAsync(BorrowerTenantId,
            Arg.Is<RtPipelineExecution>(e => e.ExecutionId != ExecutionId),
            Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     🔴 The interruption itself carries <c>LeaseReleasedAt</c>. This is the TTL/crash half of the
    ///     billing span — stamping it only on the happy path silently under-bills exactly the
    ///     failures a borrower did pay for.
    /// </summary>
    [Test]
    public async Task InterruptAndRequeue_StampsTheLeaseReleaseTimestamp()
    {
        await GrantWithWorkItemAsync();
        ArrangeInterruptible();
        var before = DateTime.UtcNow.AddSeconds(-1);

        var lease = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease!;
        await LeaseService.InterruptAndRequeueAsync(lease, LeaseInterruptReason.TtlExpiry,
            "the lease expired");

        await CommunicationRepository.Received(1).TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Is<DateTime>(d => d >= before && d <= DateTime.UtcNow.AddSeconds(1)),
            Arg.Is<string>(m => m.Contains("expired")));
    }

    /// <summary>
    ///     The retry carries the original trigger type and input, so re-running it means the same
    ///     thing as the attempt it replaces.
    /// </summary>
    [Test]
    public async Task TheRequeuedAttempt_CarriesTheOriginalTriggerAndInput()
    {
        await GrantWithWorkItemAsync();
        ArrangeInterruptible(RtPipelineTriggerTypeEnum.Scheduled, "{\"a\":1}");

        var lease = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease!;
        await LeaseService.InterruptAndRequeueAsync(lease, LeaseInterruptReason.TtlExpiry,
            "the lease expired");

        await CommunicationRepository.Received(1).EnqueueExecutionAsync(BorrowerTenantId,
            Arg.Is<RtPipelineExecution>(e =>
                e.TriggerType == RtPipelineTriggerTypeEnum.Scheduled && e.InputData == "{\"a\":1}"),
            Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     An attempt that cannot be interrupted (already terminal, or its edges are gone) is not
    ///     re-queued. Enqueuing a replacement for work that already finished would run it twice.
    /// </summary>
    [Test]
    public async Task AnAttemptThatCannotBeInterrupted_IsNotRequeued()
    {
        await GrantWithWorkItemAsync();
        CommunicationRepository.TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
            Arg.Any<DateTime>(), Arg.Any<string>()).Returns((InterruptedLeasedExecution?)null);

        var lease = ConnectionManager.TryGetMember(ConnectionId)!.ActiveLease!;
        var requeued = await LeaseService.InterruptAndRequeueAsync(lease, LeaseInterruptReason.TtlExpiry,
            "the lease expired");

        using var _ = Assert.Multiple();
        await Assert.That(requeued).IsNull();
        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     Draining marks the member locally even when the push fails — a member that cannot be
    ///     reached is exactly the member that must not be handed another tenant.
    /// </summary>
    [Test]
    public async Task DrainMember_MarksTheMemberDrainingEvenWhenThePushFails()
    {
        ArrangeGrantableLease();
        MemberProxy
            .SendCoreAsync(Arg.Any<string>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("connection gone")));

        await LeaseService.DrainMemberAsync(ConnectionId, "its lease expired");

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsDraining).IsTrue();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsFalse();
    }

    private void ArrangeInterruptible(
        RtPipelineTriggerTypeEnum triggerType = RtPipelineTriggerTypeEnum.Manual, string? inputData = null)
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        CommunicationRepository.TryInterruptLeasedExecutionAsync(BorrowerTenantId, ExecutionId,
                Arg.Any<DateTime>(), Arg.Any<string>())
            .Returns(new InterruptedLeasedExecution(
                new RtEntityId(pipeline.CkTypeId!, pipeline.RtId),
                new RtEntityId(Borrower.CkTypeId!, Borrower.RtId),
                triggerType,
                inputData));
    }
}
