using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IAdapterConnectionTracker" />
internal class AdapterConnectionTracker : IAdapterConnectionTracker
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // tenantId -> (adapterRtEntityId -> connectionId). Purely in-memory / per-pod, mutated only
    // by the AdapterHub connect/disconnect lifecycle. Intentionally NOT touched by tenant
    // pre/post-update so it stays an accurate liveness view when the config cache is flushed.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<RtEntityId, string>> _connectionsByTenant = new();

    public void TrackConnected(string tenantId, RtEntityId adapterRtEntityId, string connectionId)
    {
        var adapters = _connectionsByTenant.GetOrAdd(tenantId, _ => new ConcurrentDictionary<RtEntityId, string>());
        adapters[adapterRtEntityId] = connectionId;
        Logger.Debug("[{TenantId}] Tracking live connection for adapter '{AdapterRtId}' on '{ConnectionId}'",
            tenantId, adapterRtEntityId, connectionId);
    }

    public void TrackDisconnected(string tenantId, RtEntityId adapterRtEntityId, string connectionId)
    {
        if (!_connectionsByTenant.TryGetValue(tenantId, out var adapters))
        {
            return;
        }

        // Compare-and-remove: only clear the tracked connection when it is still the one that is
        // disconnecting. A newer connection that has already replaced it (adapter auto-reconnected)
        // must keep its live entry — mirrors the stale-disconnect guard in
        // AdapterService.SetAdapterCommunicationStateOfflineAsync.
        if (adapters.TryGetValue(adapterRtEntityId, out var current) && current == connectionId)
        {
            adapters.TryRemove(new KeyValuePair<RtEntityId, string>(adapterRtEntityId, connectionId));
            Logger.Debug("[{TenantId}] Cleared tracked connection for adapter '{AdapterRtId}' on '{ConnectionId}'",
                tenantId, adapterRtEntityId, connectionId);
        }
        else
        {
            Logger.Debug(
                "[{TenantId}] Ignoring stale disconnect for adapter '{AdapterRtId}' on '{ConnectionId}' — a newer connection is tracked",
                tenantId, adapterRtEntityId, connectionId);
        }
    }

    public bool HasLiveConnection(string tenantId, RtEntityId adapterRtEntityId)
    {
        return _connectionsByTenant.TryGetValue(tenantId, out var adapters)
               && adapters.ContainsKey(adapterRtEntityId);
    }
}
