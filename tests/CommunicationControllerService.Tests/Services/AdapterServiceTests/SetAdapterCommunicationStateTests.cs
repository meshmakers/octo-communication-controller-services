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

internal class SetAdapterCommunicationStateTests : AdapterServiceTestsBase
{
    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task SetAdapterCommunicationStateOnlineAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert
        await Assert.That(async () =>
                await AdapterService.SetAdapterCommunicationStateOnlineAsync("unknownTenant", rtAdapter.ToRtEntityId(), ConnectionId))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task SetAdapterCommunicationStateOnlineAsync_AdapterNotInCache_DoesNotThrow()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act - should not throw
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert - no repository calls should be made
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetAdapterCommunicationStateOnlineAsync_AdapterInCache_SetsOnlineAndUpdatesConnection()
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

        var newConnectionId = "newConnectionId";

        // Act
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, rtAdapter.ToRtEntityId(), newConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Online);

        // Verify connection ID was updated in cache
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsEqualTo(newConnectionId);
    }

    [Test]
    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    public async Task SetAdapterCommunicationStateOfflineAsync_TenantNotInCache_ThrowsException()
    {
        // Arrange
        AdapterCache.TryGetTenant("unknownTenant", out Arg.Any<AdapterTenant?>())
            .Returns(false);

        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act & Assert
        await Assert.That(async () =>
                await AdapterService.SetAdapterCommunicationStateOfflineAsync("unknownTenant", rtAdapter.ToRtEntityId()))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_AdapterNotInCache_DoesNotThrow()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act - should not throw
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert - no repository calls should be made
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_AdapterInCache_SetsOfflineAndRemovesConnection()
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
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId());

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);

        // Verify connection ID was removed from cache
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsNull();
    }
}
