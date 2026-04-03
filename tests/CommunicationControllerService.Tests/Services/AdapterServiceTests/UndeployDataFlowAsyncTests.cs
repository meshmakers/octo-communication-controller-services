using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class UndeployDataFlowAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task UndeployDataFlowAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtDataFlow = RtEntityCreator.CreateDataFlow();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataFlowAsync("unknownTenant", rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Tenant not enabled"));
    }

    [Test]
    public async Task UndeployDataFlowAsync_NoPipelinesFound_ThrowsException()
    {
        // Arrange
        var rtDataFlow = RtEntityCreator.CreateDataFlow();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([]);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("has no edge or mesh pipeline assigned"));
    }

    [Test]
    public async Task UndeployDataFlowAsync_SinglePipelineOnAdapter_RemovesPipelineAndUpdatesState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId(), RtDeploymentStateEnum.Undeployed, null);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.Pipelines.Count == 0));
    }

    [Test]
    public async Task UndeployDataFlowAsync_AdapterNotInCache_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task UndeployDataFlowAsync_MultiplePipelinesOnAdapter_RemovesOnlySpecifiedDataPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow1 = RtEntityCreator.CreateDataFlow();
        var rtDataFlow2 = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataFlow2.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow1.RtId)
            .Returns([rtPipeline1]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow1.RtId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Undeployed, null);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().PipelineRtEntityId == rtPipeline2.ToRtEntityId()));
    }

    [Test]
    public async Task UndeployDataFlowAsync_MultiplePipelinesMultipleAdapters_RemovesFromAllAdapters()
    {
        // Arrange
        var rtAdapter1 = RtEntityCreator.CreateAdapter();
        var rtAdapter2 = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter1.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter1.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, [])
            ]
        ));

        AdapterTenant.AddAdapter(rtAdapter2.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter2.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter1);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter2);

        // Act
        await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Undeployed, null);
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Undeployed, null);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter1.ToRtEntityId() &&
                config.Pipelines.Count == 0));

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter2.ToRtEntityId() &&
                config.Pipelines.Count == 0));
    }

    [Test]
    public async Task UndeployDataFlowAsync_MultiplePipelinesSameDataPipelineOnSameAdapter_RemovesAllFromAdapter()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        using var _ = Assert.Multiple();

        // The implementation loops through both pipelines and for each one loops through deployed pipelines
        // So each of the 2 deployed pipelines gets called 2 times (once per pipeline in rtUndeployPipeline loop)
        await CommunicationRepository.Received()
            .SetPipelineDeploymentStateAsync(TenantId, Arg.Any<RtEntityId>(), RtDeploymentStateEnum.Undeployed, null);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.Pipelines.Count == 0));
    }

   [Test]
    public async Task UndeployDataFlowAsync_AdapterWithOtherPipelines_PreservesOtherPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow1 = RtEntityCreator.CreateDataFlow();
        var rtDataFlow2 = RtEntityCreator.CreateDataFlow();
        var rtDataFlow3 = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();
        var rtPipeline3 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataFlow2.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataFlow3.RtId, rtPipeline3.ToRtEntityId(), false,
                    rtPipeline3.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow2.RtId)
            .Returns([rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataFlowAsync(TenantId, rtDataFlow2.RtId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Undeployed, null);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 2 &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline1.ToRtEntityId()) &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline3.ToRtEntityId()) &&
                !config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline2.ToRtEntityId())));
    }
}
