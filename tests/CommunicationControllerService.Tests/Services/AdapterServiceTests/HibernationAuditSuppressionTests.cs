using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
///     AB#4919: a workload that scales to zero disconnects on purpose. The offline handling must
///     still write the state — <c>Offline</c> is factually true while hibernated — but must keep the
///     event out of the tenant's audit trail, otherwise every idle cycle files an incident and a real
///     outage stops standing out.
/// </summary>
internal class HibernationAuditSuppressionTests : AdapterServiceTestsBase
{
    private static RtAdapter OnlineAdapter()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.CommunicationState = RtCommunicationStateEnum.Online;
        return adapter;
    }

    private void Hibernating(RtAdapter adapter, bool hibernating = true)
    {
        WorkloadLifecycleService.IsIntentionallyDownAsync(TenantId, adapter.RtId).Returns(hibernating);
    }

    [Test]
    public async Task Disconnect_WhileHibernating_WritesOfflineWithoutAuditEvent()
    {
        // Arrange
        var adapter = OnlineAdapter();
        Hibernating(adapter);

        // Act
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, adapter.ToRtEntityId(), ConnectionId);

        // Assert — the state still follows reality, only the audit noise is gone.
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);
        await CommunicationEventService.DidNotReceive()
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("is now offline")),
                Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task Disconnect_WhileRunning_StillWritesTheOfflineEvent()
    {
        // Arrange — the ordinary disconnect must be reported exactly as before.
        var adapter = OnlineAdapter();
        Hibernating(adapter, false);

        // Act
        await AdapterService.SetAdapterCommunicationStateOfflineAsync(TenantId, adapter.ToRtEntityId(), ConnectionId);

        // Assert
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("is now offline")),
                Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task Reconciliation_OfHibernatingAdapter_StillCorrectsTheStateButDoesNotReportIt()
    {
        // Arrange — a hibernated workload has no connection by design, so the sweep finds it, but
        // reporting that as an anomaly would page someone for a scale-down.
        var adapter = OnlineAdapter();
        CommunicationRepository.GetAdaptersAsync(TenantId).Returns([adapter]);
        Hibernating(adapter);

        // Act
        var count = await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert — a stale Online would show the workload as healthy, so the write must still happen.
        await Assert.That(count).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .SetAdapterCommunicationStateAsync(TenantId, adapter.ToRtEntityId(), RtCommunicationStateEnum.Offline);
        await CommunicationEventService.DidNotReceive()
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("had no live connection")),
                Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task Reconciliation_OfRunningAdapter_StillReportsTheOrphan()
    {
        // Arrange
        var adapter = OnlineAdapter();
        CommunicationRepository.GetAdaptersAsync(TenantId).Returns([adapter]);
        Hibernating(adapter, false);

        // Act
        await AdapterService.ReconcileOrphanedOnlineAdaptersAsync(TenantId);

        // Assert
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Is<string>(m => m.Contains("had no live connection")),
                Arg.Any<RtEntityId?>());
    }
}
