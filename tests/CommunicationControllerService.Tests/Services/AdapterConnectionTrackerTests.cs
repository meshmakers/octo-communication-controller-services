using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

internal class AdapterConnectionTrackerTests
{
    private const string TenantId = "tenantId";
    private const string ConnectionId = "connectionId";

    private static RtEntityId AdapterId() => RtEntityCreator.CreateAdapter().ToRtEntityId();

    [Test]
    public async Task HasLiveConnection_UntrackedAdapter_ReturnsFalse()
    {
        var tracker = new AdapterConnectionTracker();

        await Assert.That(tracker.HasLiveConnection(TenantId, AdapterId())).IsFalse();
    }

    [Test]
    public async Task TrackConnected_ThenHasLiveConnection_ReturnsTrue()
    {
        var tracker = new AdapterConnectionTracker();
        var adapterId = AdapterId();

        tracker.TrackConnected(TenantId, adapterId, ConnectionId);

        await Assert.That(tracker.HasLiveConnection(TenantId, adapterId)).IsTrue();
    }

    [Test]
    public async Task HasLiveConnection_DifferentTenant_ReturnsFalse()
    {
        var tracker = new AdapterConnectionTracker();
        var adapterId = AdapterId();
        tracker.TrackConnected(TenantId, adapterId, ConnectionId);

        await Assert.That(tracker.HasLiveConnection("otherTenant", adapterId)).IsFalse();
    }

    [Test]
    public async Task TrackDisconnected_MatchingConnection_ClearsLiveConnection()
    {
        var tracker = new AdapterConnectionTracker();
        var adapterId = AdapterId();
        tracker.TrackConnected(TenantId, adapterId, ConnectionId);

        tracker.TrackDisconnected(TenantId, adapterId, ConnectionId);

        await Assert.That(tracker.HasLiveConnection(TenantId, adapterId)).IsFalse();
    }

    [Test]
    public async Task TrackDisconnected_StaleConnection_KeepsLiveConnection()
    {
        // A late disconnect from a superseded connection must not clear the freshly reconnected one.
        var tracker = new AdapterConnectionTracker();
        var adapterId = AdapterId();
        tracker.TrackConnected(TenantId, adapterId, "newConnectionId");

        tracker.TrackDisconnected(TenantId, adapterId, "oldConnectionId");

        await Assert.That(tracker.HasLiveConnection(TenantId, adapterId)).IsTrue();
    }

    [Test]
    public async Task Reconnect_ThenStaleDisconnectOfOldConnection_StaysLive()
    {
        var tracker = new AdapterConnectionTracker();
        var adapterId = AdapterId();
        tracker.TrackConnected(TenantId, adapterId, "oldConnectionId");
        // Adapter reconnects on a new connection.
        tracker.TrackConnected(TenantId, adapterId, "newConnectionId");

        // The old connection's late disconnect fires.
        tracker.TrackDisconnected(TenantId, adapterId, "oldConnectionId");

        await Assert.That(tracker.HasLiveConnection(TenantId, adapterId)).IsTrue();
    }

    [Test]
    public async Task TrackDisconnected_UnknownTenant_DoesNotThrow()
    {
        var tracker = new AdapterConnectionTracker();

        tracker.TrackDisconnected("unknownTenant", AdapterId(), ConnectionId);

        await Assert.That(tracker.HasLiveConnection("unknownTenant", AdapterId())).IsFalse();
    }
}
