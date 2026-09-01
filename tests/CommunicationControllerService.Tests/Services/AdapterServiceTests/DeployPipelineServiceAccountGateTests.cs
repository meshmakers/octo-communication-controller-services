using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// AB#5027 (Epic AB#4979) mandatory-identity deploy guard: a pipeline whose service account
/// cannot be resolved — neither a per-pipeline override nor an adapter default — is refused,
/// on both the pipeline-deploy and the data-flow-deploy path, before any state is written.
/// </summary>
internal class DeployPipelineServiceAccountGateTests : AdapterServiceTestsBase
{
    private const string PipelineDefinition =
        """
        triggers:
          - type: FromHttpRequest@1
        """;

    private (RtAdapter Adapter, RtDataFlow DataFlow, RtPipeline Pipeline) ArrangeDeployablePipeline()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        rtAdapter.Name = "mesh-adapter";
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(PipelineDefinition);

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtDataFlow, rtPipeline);
    }

    /// <summary>Removes the base class's default adapter service account.</summary>
    private void ArrangeNoServiceAccountLinked()
    {
        CommunicationRepository
            .GetServiceAccountForAdapterAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .Returns((RtServiceAccountConfiguration?)null);
    }

    [Test]
    public async Task DeployPipelineAsync_NoServiceAccountResolvable_IsRejectedWithActionableMessage()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        ArrangeNoServiceAccountLinked();

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        // Cause, work item, affected objects and both ways out must be in the message.
        await Assert.That(ex!.Message).Contains("service account");
        await Assert.That(ex!.Message).Contains("AB#5027");
        await Assert.That(ex!.Message).Contains("mesh-adapter");
        await Assert.That(ex!.Message).Contains(pipeline.RtId.ToString());
        await Assert.That(ex!.Message).Contains("PipelineServiceAccount");
        await Assert.That(ex!.Message).Contains("Uses");
    }

    [Test]
    public async Task DeployPipelineAsync_NoServiceAccountResolvable_WritesNoStateAndReachesNoAdapter()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        ArrangeNoServiceAccountLinked();

        await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId()));

        using var _ = Assert.Multiple();
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDefinitionAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterConfigurationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtConfigurationStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeployDataFlowAsync_NoServiceAccountResolvable_IsRejectedAndReachesNoAdapter()
    {
        var (_, dataFlow, _) = ArrangeDeployablePipeline();
        ArrangeNoServiceAccountLinked();

        var ex = await Assert.ThrowsAsync<Exception>(async () =>
            await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!).IsTypeOf<AdapterServiceException>();
        await Assert.That(ex!.Message).Contains("AB#5027");
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterConfigurationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtConfigurationStateEnum>(), Arg.Any<string?>());
    }

    [Test]
    public async Task DeployPipelineAsync_AdapterDefaultLinked_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_PipelineOverrideOnly_Deploys()
    {
        var (adapter, _, pipeline) = ArrangeDeployablePipeline();
        ArrangeNoServiceAccountLinked();
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, pipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>(
                [RtEntityCreator.CreateServiceAccountConfiguration("pipeline-override")]));

        await AdapterService.DeployPipelineAsync(TenantId, adapter.ToRtEntityId(), pipeline.ToRtEntityId());

        await AdapterHubCallbacks.Received(1)
            .AdapterConfigurationUpdatedAsync(TenantId, Arg.Any<AdapterConfigurationDto>());
    }
}
