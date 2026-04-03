using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class DeployPipelineAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task DeployPipelineAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployPipelineAsync("unknownTenant", rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task DeployPipelineAsync_AdapterNotInCache_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Adapter").And.Contains("not loaded"));
    }

    [Test]
    public async Task DeployPipelineAsync_PipelineNotFound_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns((RtPipeline?)null);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pipeline").And.Contains("not found"));
    }

    [Test]
    public async Task DeployPipelineAsync_DataFlowNotFound_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns((RtDataFlow?)null);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId()))
            .Throws<AdapterServiceException>();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Data flow").And.Contains("not found"));
    }

    [Test]
    public async Task DeployPipelineAsync_NewPipelineWithoutCustomDefinition_AddsToConfiguration()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;

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
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().PipelineRtEntityId == rtPipeline.ToRtEntityId() &&
                config.Pipelines.First().IsDebuggingEnabled == true));
    }

    [Test]
    public async Task DeployPipelineAsync_NewPipelineWithCustomDefinition_UsesCustomDefinition()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.PipelineDefinition = "original definition";
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;

        var customDefinition = "custom debug definition";

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
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId(), customDefinition);

        // Assert
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.First().NodeConfiguration == customDefinition &&
                config.Pipelines.First().IsDebuggingEnabled == true));
    }

    [Test]
    public async Task DeployPipelineAsync_ExistingPipeline_ReplacesWithDebugVersion()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.PipelineDefinition = "updated definition";
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;

        // Start with existing pipeline (non-debug)
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    "old definition", [])
            ]
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        // Should still have only one pipeline but with debug enabled and updated definition
        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 1 &&
                config.Pipelines.First().IsDebuggingEnabled == true &&
                config.Pipelines.First().NodeConfiguration == "updated definition"));

        // Should set old pipeline to Pending
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId(), RtDeploymentStateEnum.Pending, null);
    }

    [Test]
    public async Task DeployPipelineAsync_MultiplePipelines_OnlyUpdatesSpecificPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();
        rtPipeline1.DeploymentState = RtDeploymentStateEnum.Deployed;
        rtPipeline2.DeploymentState = RtDeploymentStateEnum.Deployed;

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

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline1.ToRtEntityId())
            .Returns(rtPipeline1);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline1.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline2.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline1, rtPipeline2]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, Arg.Any<OctoObjectId>())
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act - Deploy only pipeline1 for debugging
        await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline1.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Is<AdapterConfigurationDto>(config =>
                config.Pipelines.Count == 2 &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline1.ToRtEntityId() && p.IsDebuggingEnabled == true) &&
                config.Pipelines.Any(p => p.PipelineRtEntityId == rtPipeline2.ToRtEntityId() && p.IsDebuggingEnabled == false)));

        // Both old pipelines should be set to Pending
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(), RtDeploymentStateEnum.Pending, null);
        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(), RtDeploymentStateEnum.Pending, null);
    }

    [Test]
    public async Task DeployPipelineAsync_SameConfigurationAlreadyDeployed_DoesNotUpdateAdapter()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.DeploymentState = RtDeploymentStateEnum.Deployed;

        // Already has the exact same configuration deployed with debugging enabled
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), true,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId());

        // Assert - Should not call update callback if configuration is the same
        await AdapterHubCallbacks.DidNotReceiveWithAnyArgs().AdapterConfigurationUpdatedAsync(Arg.Any<string>(), Arg.Any<AdapterConfigurationDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
    }
}
