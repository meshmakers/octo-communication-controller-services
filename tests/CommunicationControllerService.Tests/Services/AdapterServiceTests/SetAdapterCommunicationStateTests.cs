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
    public async Task SetAdapterCommunicationStateOnlineAsync_AdapterNotInCache_StillUpdatesDbState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act - should not throw
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert - DB state should always be updated, even if adapter is not in cache
        // This ensures the DB reflects the correct state after service restarts or cache misses
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task SetAdapterCommunicationStateOnlineAsync_AdapterInCacheWithoutConnection_LogsOnlineEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        // Add adapter with connection, then remove it (simulates adapter in cache but disconnected)
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), "oldConnectionId", new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));
        AdapterTenant.RemoveConnectionId(rtAdapter.ToRtEntityId());

        var newConnectionId = "newConnectionId";

        // Act
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, rtAdapter.ToRtEntityId(), newConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Online);

        // Verify connection ID was updated in cache
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsEqualTo(newConnectionId);

        // Verify "is now online" event was logged (not "reconnected") with adapter as related entity
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(s => s.Contains("is now online")),
                rtAdapter.ToRtEntityId());
    }

    [Test]
    public async Task SetAdapterCommunicationStateOnlineAsync_AdapterAlreadyOnline_LogsReconnectEvent()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        // Add adapter with existing connection (simulates adapter already online)
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
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

        // Verify "reconnected" event was logged (not "is now online") with adapter as related entity
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(s => s.Contains("reconnected")),
                rtAdapter.ToRtEntityId());
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
                await AdapterService.SetAdapterCommunicationStateOfflineAsync("unknownTenant", rtAdapter.ToRtEntityId(), ConnectionId))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_AdapterNotInCache_StillUpdatesDbState()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act - should not throw
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert - DB state should always be updated, even if adapter is not in cache
        // This ensures the DB reflects the correct state after service restarts or cache misses
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_AdapterInCache_SetsOfflineAndRemovesConnection()
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

        // Act
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, rtAdapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);

        // Verify connection ID was removed from cache
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsNull();
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_StaleDisconnect_IgnoresAndDoesNotSetOffline()
    {
        // Arrange - adapter is in cache with a newer connection
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        var newConnectionId = "newConnectionId";
        var staleConnectionId = "oldConnectionId";

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), newConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        // Act - stale disconnect from old connection
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId(), staleConnectionId);

        // Assert - should NOT set offline in DB or remove connection from cache
        using var _ = Assert.Multiple();

        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());

        // Verify connection ID is still the new one
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsEqualTo(newConnectionId);
    }

    [Test]
    public async Task SetAdapterCommunicationStateOfflineAsync_AfterReconnect_OldConnectionIgnored()
    {
        // Arrange - simulate the race condition:
        // 1. Adapter connects with old connection
        // 2. Adapter reconnects with new connection (OnConnectedAsync fires first)
        // 3. Old connection's OnDisconnectedAsync fires after new connection is already online
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        var oldConnectionId = "oldConnectionId";
        var newConnectionId = "newConnectionId";

        // Step 1: Adapter initially connected with old connection
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), oldConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        // Step 2: New connection comes in (reconnect)
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, rtAdapter.ToRtEntityId(), newConnectionId);

        CommunicationRepository.ClearReceivedCalls();

        // Step 3: Old connection's disconnect fires AFTER new connection is established
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, rtAdapter.ToRtEntityId(), oldConnectionId);

        // Assert - the stale disconnect must be ignored
        using var _ = Assert.Multiple();

        // DB should NOT be updated to Offline
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());

        // Cache should still have the new connection
        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId).IsEqualTo(newConnectionId);
    }
}
