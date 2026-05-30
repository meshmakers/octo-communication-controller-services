using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class SetPipelineDebuggingAsyncTests : AdapterServiceTestsBase
{
    private void ArrangeOnlineAdapter(RtAdapter rtAdapter, RtDataFlow rtDataFlow, RtPipeline rtPipeline)
    {
        // Adapter is live (registered in the tenant cache) -> DeployDataFlowAsync re-push will succeed.
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        // DeployDataFlowAsync internals:
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));
    }

    [Test]
    public async Task SetPipelineDebuggingAsync_Enable_AdapterOnline_PersistsAndRePushesEnabled()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;
        rtPipeline.IsDebuggingEnabled = true; // reflects the persisted value the re-push reads back
        ArrangeOnlineAdapter(rtAdapter, rtDataFlow, rtPipeline);

        var appliedLive = await AdapterService.SetPipelineDebuggingAsync(
            TenantId, rtPipeline.ToRtEntityId(), true);

        using var _ = Assert.Multiple();
        await Assert.That(appliedLive).IsTrue();
        await CommunicationRepository.Received(1)
            .SetPipelineDebuggingEnabledAsync(TenantId, rtPipeline.ToRtEntityId(), true);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Any(p =>
                    p.PipelineRtEntityId == rtPipeline.ToRtEntityId() && p.IsDebuggingEnabled == true)));
    }

    [Test]
    public async Task SetPipelineDebuggingAsync_Disable_AdapterOnline_PersistsAndRePushesDisabled()
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;
        rtPipeline.IsDebuggingEnabled = false; // re-push reads the now-disabled persisted value
        ArrangeOnlineAdapter(rtAdapter, rtDataFlow, rtPipeline);

        var appliedLive = await AdapterService.SetPipelineDebuggingAsync(
            TenantId, rtPipeline.ToRtEntityId(), false);

        using var _ = Assert.Multiple();
        await Assert.That(appliedLive).IsTrue();
        await CommunicationRepository.Received(1)
            .SetPipelineDebuggingEnabledAsync(TenantId, rtPipeline.ToRtEntityId(), false);
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Any(p =>
                    p.PipelineRtEntityId == rtPipeline.ToRtEntityId() && p.IsDebuggingEnabled == false)));
    }

    [Test]
    public async Task SetPipelineDebuggingAsync_AdapterOffline_PersistsOnlyAndReturnsFalse()
    {
        // Do NOT register the adapter in the tenant cache -> DeployDataFlowAsync throws AdapterNotLoaded.
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.IsDebuggingEnabled = true;

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);

        var appliedLive = await AdapterService.SetPipelineDebuggingAsync(
            TenantId, rtPipeline.ToRtEntityId(), true);

        using var _ = Assert.Multiple();
        await Assert.That(appliedLive).IsFalse();
        await CommunicationRepository.Received(1)
            .SetPipelineDebuggingEnabledAsync(TenantId, rtPipeline.ToRtEntityId(), true);
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs()
            .AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task SetPipelineDebuggingAsync_PipelineNotFound_Throws()
    {
        var rtPipeline = RtEntityCreator.CreatePipeline();
        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns((RtPipeline?)null);

        var exception = await Assert.That(async () =>
                await AdapterService.SetPipelineDebuggingAsync(TenantId, rtPipeline.ToRtEntityId(), true))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pipeline").And.Contains("not found"));
    }

    [Test]
    public async Task SetPipelineDebuggingAsync_DataFlowNotFound_Throws()
    {
        var rtPipeline = RtEntityCreator.CreatePipeline();
        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns((RtDataFlow?)null);

        var exception = await Assert.That(async () =>
                await AdapterService.SetPipelineDebuggingAsync(TenantId, rtPipeline.ToRtEntityId(), true))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Data flow").And.Contains("not found"));
    }
}
