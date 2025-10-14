using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class UnregisterAsyncTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task UnregisterAsync_TenantNotInCache_NoException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert - should not throw
        await AdapterService.UnregisterAsync("unknownTenant", rtAdapter.ToRtEntityId(), ConnectionId);

        // Verify no repository calls were made
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task UnregisterAsync_AdapterNotInCache_RemovesFromCacheOnly()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act
        await AdapterService.UnregisterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        // Verify no repository calls were made since adapter wasn't in cache
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task UnregisterAsync_AdapterWithoutPipelines_SetsUnregisteredState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));

        // Act
        await AdapterService.UnregisterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPipelineDeploymentStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtDeploymentStateEnum>(), Arg.Any<string?>());

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(),
                RtCommunicationStateEnum.Unregistered);

        // Verify adapter was removed from cache
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(rtAdapter.ToRtEntityId())).IsFalse();
    }

    [Test]
    public async Task UnregisterAsync_AdapterWithOnePipeline_SetsPipelineToPendingAndUnregistersAdapter()
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

        // Act
        await AdapterService.UnregisterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, null);

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(),
                RtCommunicationStateEnum.Unregistered);

        // Verify adapter was removed from cache
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(rtAdapter.ToRtEntityId())).IsFalse();
    }

    [Test]
    public async Task UnregisterAsync_AdapterWithMultiplePipelines_SetsAllPipelinesToPending()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline1 = RtEntityCreator.CreateDataPipeline();
        var rtDataPipeline2 = RtEntityCreator.CreateDataPipeline();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();
        var rtPipeline3 = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline1.RtId, rtPipeline1.ToRtEntityId(), false,
                    rtPipeline1.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline1.RtId, rtPipeline2.ToRtEntityId(), false,
                    rtPipeline2.PipelineDefinition, []),
                new PipelineConfigurationDto(rtDataPipeline2.RtId, rtPipeline3.ToRtEntityId(), false,
                    rtPipeline3.PipelineDefinition, [])
            ]
        ));

        // Act
        await AdapterService.UnregisterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline1.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, null);

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline2.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, null);

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline3.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, null);

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(),
                RtCommunicationStateEnum.Unregistered);

        // Verify adapter was removed from cache
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(rtAdapter.ToRtEntityId())).IsFalse();
    }

    [Test]
    public async Task UnregisterAsync_CalledWithDifferentConnectionId_StillUnregisters()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataPipeline = RtEntityCreator.CreateDataPipeline();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var differentConnectionId = "differentConnectionId";

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataPipeline.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        // Act - Note: connectionId parameter is logged but not used in logic
        await AdapterService.UnregisterAsync(TenantId, rtAdapter.ToRtEntityId(), differentConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetPipelineDeploymentStateAsync(TenantId, rtPipeline.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, null);

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(),
                RtCommunicationStateEnum.Unregistered);

        // Verify adapter was removed from cache
        await Assert.That(AdapterTenant.AdapterById.ContainsKey(rtAdapter.ToRtEntityId())).IsFalse();
    }
}
