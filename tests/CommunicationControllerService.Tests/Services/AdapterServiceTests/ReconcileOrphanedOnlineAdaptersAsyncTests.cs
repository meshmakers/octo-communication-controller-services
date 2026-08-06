using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class ReconcileOrphanedOnlineAdaptersAsyncTests : AdapterServiceTestsBase
{
    private static RtAdapter OnlineAdapter()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.CommunicationState = RtCommunicationStateEnum.Online;
        return adapter;
    }

    private void ReturnAdapters(params RtAdapter[] adapters)
    {
        CommunicationRepository.GetAdaptersAsync(TenantId).Returns(adapters);
    }

    [Test]
    public async Task OnlineAdapterWithoutLiveConnection_IsReconciledToOffline()
    {
        // Arrange — persisted Online, but nothing tracked as connected.
        var adapter = OnlineAdapter();
        ReturnAdapters(adapter);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task OnlineAdapterWithLiveConnection_IsNotReconciled()
    {
        // Arrange — persisted Online AND tracked as connected on this pod.
        var adapter = OnlineAdapter();
        ReturnAdapters(adapter);
        AdapterConnectionTracker.TrackConnected(TenantId, adapter.ToRtEntityId(), ConnectionId);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task NonOnlineAdapter_IsSkipped()
    {
        // Arrange — an already-Offline adapter must not be touched.
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.CommunicationState = RtCommunicationStateEnum.Offline;
        ReturnAdapters(adapter);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task AdapterWithoutCkTypeId_IsSkipped()
    {
        // Arrange — defensive: a runtime adapter with no concrete type id is skipped, not crashed.
        var adapter = OnlineAdapter();
        adapter.CkTypeId = null;
        ReturnAdapters(adapter);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task MixedFleet_OnlyOrphanedOnlineAdaptersAreReconciled()
    {
        // Arrange
        var orphaned = OnlineAdapter();          // Online, no connection -> reconcile
        var connected = OnlineAdapter();         // Online, connected -> keep
        var offline = RtEntityCreator.CreateAdapter();
        offline.CommunicationState = RtCommunicationStateEnum.Offline; // already Offline -> skip
        ReturnAdapters(orphaned, connected, offline);
        AdapterConnectionTracker.TrackConnected(TenantId, connected.ToRtEntityId(), ConnectionId);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, orphaned.ToRtEntityId(), RtCommunicationStateEnum.Offline);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, connected.ToRtEntityId(), RtCommunicationStateEnum.Offline);
        await CommunicationRepository.DidNotReceive()
            .SetAdapterCommunicationStateAsync(TenantId, offline.ToRtEntityId(), RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task OnlineWrite_TracksConnection_SoSubsequentSweepKeepsItOnline()
    {
        // Arrange — drive the real Online path so the tracker is populated exactly like production,
        // then confirm the sweep treats it as connected.
        var adapter = OnlineAdapter();
        ReturnAdapters(adapter);
        await AdapterService.SetAdapterCommunicationStateOnlineAsync(TenantId, adapter.ToRtEntityId(), ConnectionId);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await Assert.That(count).IsEqualTo(0);
    }
}
