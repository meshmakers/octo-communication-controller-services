using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 §9.9 / D4 — <b>a lease must carry the work.</b>
/// </summary>
/// <remarks>
///     <para>
///         Increment 7 queued, rotated, granted, claimed, reaped and re-queued correctly, and a member
///         that received a lease still had nothing to run: <c>LeaseDto</c> carried an execution id and
///         nothing else, and there was no controller endpoint a member could ask. The resolution chosen
///         over "send an <c>ExecutePipelineRequest</c> after the grant" is this one, and the reason is
///         the entity count: the queued <c>PipelineExecution</c> already exists and the claim already
///         moved it <c>Queued → Running</c>, so a second entity would mean a reconciliation step and
///         two billing spans for one piece of work (concept §4b).
///     </para>
///     <para>
///         🔴 The projection is resolved <b>before</b> a member is reserved. A pipeline that cannot be
///         projected is a refusal, and refusing after a member was claimed would cost the pool a member
///         for every round the pipeline stays broken.
///     </para>
/// </remarks>
internal class LeaseCarriesTheWorkTests : LeaseServiceTestsBase
{
    [Test]
    public async Task AGrantedLeaseNamesThePipelineAndCarriesItsInputAndConfiguration()
    {
        ArrangeGrantableLease();
        var projection = ArrangeProjectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();

        var lease = CapturePushedLease();
        await Assert.That(lease.ExecutionId).IsEqualTo("exec-1");
        await Assert.That(lease.PipelineRtId).IsEqualTo(PipelineRtId.ToString());
        await Assert.That(lease.PipelineInput).IsEqualTo(PipelineInput);
        await Assert.That(lease.Pipeline).IsNotNull();
        await Assert.That(lease.Pipeline!.NodeConfiguration).IsEqualTo(projection.NodeConfiguration);
        await Assert.That(lease.Pipeline.PipelineRtEntityId.RtId).IsEqualTo(PipelineRtId);
    }

    /// <summary>
    ///     The projection is the controller's, and it is asked for the <b>borrower's</b> adapter — the
    ///     one the lease was granted for. Asking for any other adapter would hand a member a pipeline
    ///     whose credential is not the one on its lease.
    /// </summary>
    [Test]
    public async Task TheProjectionIsAskedForTheBorrowersOwnAdapter()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        await AdapterService.Received(1).GetLeasedPipelineConfigurationAsync(BorrowerTenantId,
            Arg.Is<RtEntityId>(id => id.RtId == Borrower.RtId), PipelineRtId);
    }

    /// <summary>
    ///     🔴 A pipeline the controller cannot project is a <b>refusal with a named reason</b>, not a
    ///     lease with nothing in it. The member must stay available — the work item stays queued and
    ///     the next round serves somebody else rather than the pool shrinking by one.
    /// </summary>
    [Test]
    public async Task AnUnprojectablePipelineRefusesTheLeaseAndNeverReservesAMember()
    {
        ArrangeGrantableLease();
        ArrangeUnprojectablePipeline();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsFalse();
        await Assert.That(result.StatusMessage).Contains(PipelineRtId.ToString());
        // Nothing was pushed, and the member is still idle.
        await Assert.That(MemberProxy.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IClientProxy.SendCoreAsync))).IsFalse();
        await Assert.That(ConnectionManager.GetMembers(LenderTenantId, AdapterPoolRtId.ToString())
            .All(m => m.IsAvailable)).IsTrue();
        // The borrower can see why its queue entry did not move.
        await EventService.Received().StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains(PipelineRtId.ToString())));
    }

    /// <summary>
    ///     A hand-driven lease (increment 6's <c>POST {tenantId}/v1/adapterPool/{id}/lease</c>) names
    ///     no pipeline and stays legal: it carries no work, the member reports "nothing to run", and
    ///     the projection is never asked for.
    /// </summary>
    [Test]
    public async Task AHandDrivenLeaseCarriesNoWorkAndAsksForNoProjection()
    {
        ArrangeGrantableLease();

        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());

        using var _ = Assert.Multiple();
        await Assert.That(result.Granted).IsTrue();

        var lease = CapturePushedLease();
        await Assert.That(lease.PipelineRtId).IsEqualTo(string.Empty);
        await Assert.That(lease.Pipeline).IsNull();
        await Assert.That(lease.PipelineInput).IsNull();
        await AdapterService.DidNotReceiveWithAnyArgs()
            .GetLeasedPipelineConfigurationAsync(default!, default!, default!);
    }

    /// <summary>
    ///     🔴 The output of a leased execution has exactly one way home. A dedicated adapter reports it
    ///     on <c>IAdapterHub.ReportExecutionEndAsync</c>, whose tenant and adapter come from the
    ///     <i>connection</i>; a pool member holds a tenant-free management channel and has no such
    ///     connection. Without the release carrying it, a leased execution would complete with an empty
    ///     <c>OutputData</c> where a dedicated adapter would have filled it.
    /// </summary>
    [Test]
    public async Task TheReleaseCarriesThePipelineOutputOntoTheExistingExecution()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, AWorkRequest());
        var lease = CapturePushedLease();

        CommunicationRepository.GetPipelineExecutionAsync(BorrowerTenantId, "exec-1")
            .Returns(new ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                .RtPipelineExecution
            {
                RtId = OctoObjectId.GenerateNewId(),
                ExecutionId = "exec-1",
                Status = ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                    .RtPipelineExecutionStatusEnum.Running,
                StartedAt = DateTime.UtcNow.AddSeconds(-2)
            });

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = lease.LeaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            StatusMessage = "ok",
            OutputData = "{\"total\":42}",
            ReleasedAtUtc = DateTime.UtcNow
        });

        using var _ = Assert.Multiple();
        // 🔴 ONE entity: the existing execution is UPDATED by its id. Nothing on this path creates a
        // second one, and CreatePipelineExecutionAsync is never reached.
        await CommunicationRepository.Received(1).UpdatePipelineExecutionAsync(BorrowerTenantId, "exec-1",
            Arg.Any<ConstructionKit.Models.System.Communication.Generated.System.Communication.v4
                .RtPipelineExecutionStatusEnum>(),
            Arg.Any<DateTime?>(), Arg.Any<int?>(), Arg.Any<string?>(), "{\"total\":42}");
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .CreatePipelineExecutionAsync(default!, default!, default!, default!);
        await CommunicationRepository.Received(1)
            .StampLeaseReleasedAsync(BorrowerTenantId, "exec-1", Arg.Any<DateTime>());
    }
}
