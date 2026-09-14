using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

/// <summary>
///     AB#4924 §9.1 — a <c>Leased</c> adapter has no process of its own, so its work is queued rather
///     than sent to the execute queue. A manual adapter is untouched: it has no queue and executes
///     immediately, and that asymmetry is intended (concept §5).
/// </summary>
internal class LeasedAdapterEnqueueTests : TriggerManagementServiceTestsBase
{
    private RtAdapter ArrangeAdapter(RtLifecycleModeEnum lifecycleMode, OctoObjectId pipelineRtId)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "an-adapter";
        adapter.LifecycleMode = lifecycleMode;

        CommunicationRepository
            .GetAdapterByPipelineAsync(TenantId,
                Arg.Is<RtEntityId>(id => id.RtId == pipelineRtId))
            .Returns(adapter);
        return adapter;
    }

    [Test]
    public async Task ALeasedAdapter_QueuesTheWorkItemInsteadOfSendingTheExecuteCommand()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var adapter = ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        var result = await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
            pipelineInput: "{\"x\":1}");

        using var _ = Assert.Multiple();
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);

        await CommunicationRepository.Received(1).EnqueueExecutionAsync(TenantId,
            Arg.Is<RtPipelineExecution>(e =>
                e.ExecutionId == result.Id.ToString() && e.InputData == "{\"x\":1}"),
            Arg.Is<RtEntityId>(id => id.RtId == pipelineRtId),
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId),
            Arg.Any<DateTime>());

        // 🔴 Nothing was sent. A Leased adapter has no process listening on the execute queue, so a
        // send here would be a message nobody consumes — the silent-drop failure the AB#4918 wake
        // gate exists to prevent, reintroduced by a different route.
        await ExecuteMeshPipelineCommandClient.DidNotReceive()
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task ALeasedAdapter_DoesNotRunTheWakeGate()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        // There is no workload to wake — the whole point of Leased is that the borrower owns no
        // process. Calling the wake gate would look for one and find nothing to do, at best.
        await WorkloadLifecycleService.DidNotReceive()
            .EnsureWorkloadRunningForPipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    public async Task AManualAdapter_StillExecutesImmediately(RtLifecycleModeEnum lifecycleMode)
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(lifecycleMode, pipelineRtId);

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        using var _ = Assert.Multiple();
        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
        await ExecuteMeshPipelineCommandClient.Received(1)
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    /// <summary>
    ///     A dry run is a synchronous answer to a caller holding the request open. Parking it behind
    ///     a rotation would turn "validate this pipeline" into something that returns minutes later,
    ///     so it falls through to the normal path and fails there instead of pretending to have run.
    /// </summary>
    [Test]
    public async Task ADryRunOnALeasedAdapter_IsNotQueued()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null,
            isDryRun: true);

        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }
}
