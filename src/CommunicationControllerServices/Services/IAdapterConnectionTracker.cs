using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Per-pod registry of live adapter SignalR connections, maintained by the
/// <see cref="Hubs.AdapterHub"/> connect / disconnect lifecycle.
///
/// Unlike the configuration <see cref="Caches.Adapters.IAdapterCache"/>, this registry is
/// <b>never</b> flushed by a tenant pre/post-update: a live adapter stays connected through a
/// CK-cache reload (the SignalR connection is independent of the config cache), so the config
/// cache momentarily loses connected adapters while this tracker keeps them. That makes it the
/// reliable "is this adapter currently connected to this pod?" signal for the offline
/// reconciliation sweep (AB#4699) — a cache-miss cannot be trusted to mean "disconnected", but a
/// tracker-miss can (once the startup grace has elapsed).
/// </summary>
internal interface IAdapterConnectionTracker
{
    /// <summary>
    /// Records that <paramref name="adapterRtEntityId"/> is connected on
    /// <paramref name="connectionId"/>. Called from <c>AdapterHub.OnConnectedAsync</c>.
    /// </summary>
    void TrackConnected(string tenantId, RtEntityId adapterRtEntityId, string connectionId);

    /// <summary>
    /// Removes the tracked connection for <paramref name="adapterRtEntityId"/> — but only if the
    /// currently tracked connection still equals <paramref name="connectionId"/> (compare-and-remove),
    /// so a late disconnect from a superseded connection cannot clear a freshly reconnected one.
    /// Called from <c>AdapterHub.OnDisconnectedAsync</c> on the normal (non-shutdown) path.
    /// </summary>
    void TrackDisconnected(string tenantId, RtEntityId adapterRtEntityId, string connectionId);

    /// <summary>
    /// Returns <c>true</c> when a live SignalR connection is tracked for
    /// <paramref name="adapterRtEntityId"/> on this pod.
    /// </summary>
    bool HasLiveConnection(string tenantId, RtEntityId adapterRtEntityId);
}
