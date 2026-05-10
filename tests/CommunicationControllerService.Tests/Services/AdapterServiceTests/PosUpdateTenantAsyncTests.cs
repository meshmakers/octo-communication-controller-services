using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class PosUpdateTenantAsyncTests : AdapterServiceTestsBase
{
    [Test]
    public async Task PosUpdateTenantAsync_Success_CallsAddOrUpdateTenant()
    {
        // Arrange
        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(Array.Empty<RtAdapter>());

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        AdapterCache.Received(1).AddOrUpdateTenant(TenantId);
    }

    [Test]
    public async Task PosUpdateTenantAsync_WithDisconnectedAdapters_ResetsAllAdapterCommunicationStatesToOffline()
    {
        // Arrange
        var adapter1 = RtEntityCreator.CreateAdapter();
        var adapter2 = RtEntityCreator.CreateAdapter();
        adapter1.CommunicationState = RtCommunicationStateEnum.Online;
        adapter2.CommunicationState = RtCommunicationStateEnum.Online;

        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(new[] { adapter1, adapter2 });

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter1.ToRtEntityId(),
                RtCommunicationStateEnum.Offline);

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter2.ToRtEntityId(),
                RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task PosUpdateTenantAsync_WithConnectedAdapter_DoesNotMarkConnectedAdapterOffline()
    {
        // Arrange — connected adapter is in the cache with a SignalR connection id; a
        // post-update must not falsely flip its DB state to Offline.
        var connectedAdapter = RtEntityCreator.CreateAdapter();
        var disconnectedAdapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(connectedAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            connectedAdapter.ToRtEntityId(),
            null,
            []
        ));

        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(new[] { connectedAdapter, disconnectedAdapter });

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        using var _ = Assert.Multiple();

        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, connectedAdapter.ToRtEntityId(),
                Arg.Any<RtCommunicationStateEnum>());

        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, disconnectedAdapter.ToRtEntityId(),
                RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task PosUpdateTenantAsync_AdapterInCacheWithoutConnectionId_IsMarkedOffline()
    {
        // Arrange — adapter is known to the tenant cache but has no active SignalR
        // connection (e.g. connection id was cleared on disconnect). It must be marked Offline.
        var adapter = RtEntityCreator.CreateAdapter();

        AdapterTenant.AddAdapter(adapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            adapter.ToRtEntityId(),
            null,
            []
        ));
        AdapterTenant.RemoveConnectionId(adapter.ToRtEntityId());

        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(new[] { adapter });

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(),
                RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task PosUpdateTenantAsync_WithNoAdapters_DoesNotCallSetCommunicationState()
    {
        // Arrange
        CommunicationRepository.GetAdaptersAsync(TenantId)
            .Returns(Array.Empty<RtAdapter>());

        // Act
        await AdapterService.PosUpdateTenantAsync(TenantId);

        // Assert
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task PosUpdateTenantAsync_CacheThrowsException_WrapsInAdapterServiceException()
    {
        // Arrange
        var expectedException = new InvalidOperationException("Cache operation failed");
        AdapterCache.When(x => x.AddOrUpdateTenant(TenantId))
            .Do(_ => throw expectedException);

        // Act & Assert
        var exception = await Assert.That(async () =>
                await AdapterService.PosUpdateTenantAsync(TenantId))
            .Throws<AdapterServiceException>();

        using var _ = Assert.Multiple();

        await Assert.That(exception).IsNotNull()
            .And.Member(e => e.Message, msg => msg.Contains("Pos update tenant failed"));

        var innerEx = exception?.InnerException;
        await Assert.That(innerEx).IsNotNull()
            .And.IsEqualTo(expectedException);
    }
}
