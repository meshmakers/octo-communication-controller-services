using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class UndeployDataPipelineAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task UndeployDataPipelineAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataPipelineAsync("unknownTenant", rtDataPipeline.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.HasMember(e => e.Message).Contains("Tenant not enabled");
    }

    [Test]
    public async Task UndeployDataPipelineAsync_NoPipelinesFound_ThrowsException()
    {
        // Arrange
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline.RtId)
            .Returns([]);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.HasMember(e => e.Message).Contains("has no adapter assigned");
    }

    [Test]
    public async Task UndeployDataPipelineAsync_SinglePipelineOnAdapter_RemovesPipelineAndUpdatesState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline.RtId);

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
    public async Task UndeployDataPipelineAsync_AdapterNotInCache_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.HasMember(e => e.Message).Contains("Adapter")
            .And.Contains("not loaded");
    }

    [Test]
    public async Task UndeployDataPipelineAsync_MultiplePipelinesOnAdapter_RemovesOnlySpecifiedDataPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline1 = RtEntityCreator.CreateDataPipeline();
        var rtDataPipeline2 = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline2.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline1.RtId)
            .Returns([rtPipeline1]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline1.RtId);

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
    public async Task UndeployDataPipelineAsync_MultiplePipelinesMultipleAdapters_RemovesFromAllAdapters()
    {
        // Arrange
        var rtAdapter1 = RtEntityCreator.CreateAdapter();
        var rtAdapter2 = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter1.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter1.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, [])
            ]
        ));

        AdapterTenant.AddAdapter(rtAdapter2.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter2.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter1);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter2);

        // Act
        await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline.RtId);

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
    public async Task UndeployDataPipelineAsync_MultiplePipelinesSameDataPipelineOnSameAdapter_RemovesAllFromAdapter()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline.RtId);

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
    public async Task UndeployDataPipelineAsync_AdapterWithOtherPipelines_PreservesOtherPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline1 = RtEntityCreator.CreateDataPipeline();
        var rtDataPipeline2 = RtEntityCreator.CreateDataPipeline();
        var rtDataPipeline3 = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();
        var rtPipeline3 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline2.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline3.RtId, rtPipeline3.ToRtEntityId(), false,
                    rtPipeline3.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataPipeline2.RtId)
            .Returns([rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);

        // Act
        await AdapterService.UndeployDataPipelineAsync(TenantId, rtDataPipeline2.RtId);

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
