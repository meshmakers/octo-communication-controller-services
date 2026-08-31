using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// Pins the AB#4984 reverse gate: deploying a pipeline with a process-bound trigger to a
/// workload with LifecycleMode=OnDemand is rejected (hibernation would silently stop the
/// trigger), while AlwaysOn workloads and wake-capable pipelines deploy unchanged.
/// </summary>
internal class DeployPipelineOnDemandGateTests : AdapterServiceTestsBase
{
    private const string ProcessBoundDefinition =
        """
        triggers:
          - type: FromPolling@1
        """;

    private const string WakeCapableDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private (RtEntityId AdapterRtEntityId, RtEntityId PipelineRtEntityId) ArrangeDeployablePipeline(
        string pipelineDefinition, RtLifecycleModeEnum lifecycleMode)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.LifecycleMode = lifecycleMode;
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.PipelineDefinition = pipelineDefinition;

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId)
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task DeployPipelineAsync_OnDemandWorkloadWithProcessBoundTrigger_IsRejected()
    {
        var (adapterRtEntityId, pipelineRtEntityId) =
            ArrangeDeployablePipeline(ProcessBoundDefinition, RtLifecycleModeEnum.OnDemand);

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("FromPolling@1");
        await Assert.That(ex!.Message).Contains("OnDemand");
        // The rejected pipeline must not reach the adapter
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_OnDemandWorkloadWithWakeCapableTrigger_Succeeds()
    {
        var (adapterRtEntityId, pipelineRtEntityId) =
            ArrangeDeployablePipeline(WakeCapableDefinition, RtLifecycleModeEnum.OnDemand);

        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_AlwaysOnWorkloadWithProcessBoundTrigger_Succeeds()
    {
        var (adapterRtEntityId, pipelineRtEntityId) =
            ArrangeDeployablePipeline(ProcessBoundDefinition, RtLifecycleModeEnum.AlwaysOn);

        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_SuccessfulDeploy_RefreshesPersistedCapability()
    {
        var (adapterRtEntityId, pipelineRtEntityId) =
            ArrangeDeployablePipeline(ProcessBoundDefinition, RtLifecycleModeEnum.AlwaysOn);

        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // The AlwaysOn workload deploys fine, but the persisted Studio display value must
        // now report "not capable" including the blocking reason.
        await CommunicationRepository.Received(1).SetWorkloadOnDemandCapabilityAsync(TenantId,
            adapterRtEntityId.RtId, false, Arg.Is<string?>(r => r != null && r.Contains("FromPolling@1")));
    }
}
