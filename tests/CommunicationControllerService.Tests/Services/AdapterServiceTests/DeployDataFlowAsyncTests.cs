using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class DeployDataFlowAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task DeployDataFlowAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtDataFlow = RtEntityCreator.CreateDataFlow();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployDataFlowAsync("unknownTenant", rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Tenant not enabled"));
    }

    [Test]
    public async Task DeployDataFlowAsync_NoPipelinesFound_ThrowsException()
    {
        // Arrange
        var rtDataFlow = RtEntityCreator.CreateDataFlow();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([]);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("has no pipelines assigned"));
    }

    [Test]
    public async Task DeployDataFlowAsync_SingleAdapterWithSinglePipeline_DeploysSuccessfully()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns([]);

        // Act
        await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().DataFlowRtId == rtDataFlow.RtId &&
                config.Pipelines.First().PipelineRtEntityId == rtPipeline.ToRtEntityId()));
    }

    [Test]
    public async Task DeployDataFlowAsync_AdapterNotInCache_ThrowsException()
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
                await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task DeployDataFlowAsync_ReplacesExistingPipelineVersion()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipelineOld = RtEntityCreator.CreatePipeline("old definition");
        var rtPipelineNew = RtEntityCreator.CreatePipeline("new definition");

        // Start with old pipeline version deployed
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipelineOld.ToRtEntityId(), false,
                    rtPipelineOld.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipelineNew]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipelineNew.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipelineNew.RtId)
            .Returns([]);

        // Act
        await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().PipelineRtEntityId == rtPipelineNew.ToRtEntityId() &&
                config.Pipelines.First().NodeConfiguration == "new definition"));
    }

    [Test]
    public async Task DeployDataFlowAsync_MultiplePipelinesMultipleAdapters_DeploysToAllAdapters()
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
            []
        ));

        AdapterTenant.AddAdapter(rtAdapter2.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter2.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter1);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter2);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter1.ToRtEntityId())
            .Returns(rtAdapter1);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter2.ToRtEntityId())
            .Returns(rtAdapter2);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        // Act
        await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        using var _ = Assert.Multiple();

        // Both adapters should receive configuration updates
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter1.ToRtEntityId() &&
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().PipelineRtEntityId == rtPipeline1.ToRtEntityId()));

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter2.ToRtEntityId() &&
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().PipelineRtEntityId == rtPipeline2.ToRtEntityId()));
    }

    [Test]
    public async Task DeployDataFlowAsync_PreservesOtherPipelinesOnAdapter()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow1 = RtEntityCreator.CreateDataFlow();
        var rtDataFlow2 = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline(); // For data pipeline 1
        var rtPipeline2 = RtEntityCreator.CreatePipeline(); // For data pipeline 2 (to be deployed)

        // Start with pipeline from different data pipeline already deployed
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow2.RtId)
            .Returns([rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline2.RtId)
            .Returns([]);

        // Act
        await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow2.RtId);

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 2 &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline1.ToRtEntityId()) &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline2.ToRtEntityId())));
    }

    [Test]
    public async Task DeployDataFlowAsync_MultiplePipelinesSameAdapter_DeploysAllPipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        // Both pipelines belong to the same data pipeline and same adapter
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline2.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns([]);

        // Act
        await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId);

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.AdapterRtEntityId == rtAdapter.ToRtEntityId() &&
                config.Pipelines.Count == 2 &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline1.ToRtEntityId()) &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline2.ToRtEntityId())));
    }

    [Test]
    public async Task DeployDataFlowAsync_PipelineWithoutAdapter_ThrowsException()
    {
        // Arrange
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId)
            .Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns((RtAdapter?)null);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployDataFlowAsync(TenantId, rtDataFlow.RtId))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("has no adapter assigned"));
    }
}
