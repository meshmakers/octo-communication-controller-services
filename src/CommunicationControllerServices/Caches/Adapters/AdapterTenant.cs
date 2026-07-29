using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class AdapterTenant
{
    private readonly IAdapterCachePublish _adapterCachePublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, Adapter> _adaptersByConnectId;
    private readonly ConcurrentDictionary<RtEntityId, Adapter> _adaptersById;

    public IReadOnlyDictionary<string, Adapter> AdapterByConnectionId { get; private set; }
    public IReadOnlyDictionary<RtEntityId, Adapter> AdapterById { get; }

    public AdapterTenant(IAdapterCachePublish adapterCachePublish, string tenantId)
    {
        _adapterCachePublish = adapterCachePublish;

        TenantId = tenantId;
        _adaptersByConnectId = new ConcurrentDictionary<string, Adapter>();
        _adaptersById = new ConcurrentDictionary<RtEntityId, Adapter>();
        AdapterByConnectionId = new ReadOnlyDictionary<string, Adapter>(_adaptersByConnectId);
        AdapterById = new ReadOnlyDictionary<RtEntityId, Adapter>(_adaptersById);
    }
    
    public AdapterTenant(IAdapterCachePublish adapterCachePublish, string tenantId, IList<AdapterDescription> adapterDescriptions)
    {
        _adapterCachePublish = adapterCachePublish;
        
        TenantId = tenantId;

        var adapters = adapterDescriptions.Select(p => new Adapter(adapterCachePublish, p)).ToArray();
        _adaptersByConnectId = new ConcurrentDictionary<string, Adapter>(
            adapters.Where(x=> !string.IsNullOrWhiteSpace(x.ConnectionId))
                .ToDictionary(p => p.ConnectionId!, p => p));
        _adaptersById = new ConcurrentDictionary<RtEntityId, Adapter>(
            adapters.ToDictionary(p => p.AdapterRtEntityId, p => p));
        AdapterByConnectionId = new ReadOnlyDictionary<string, Adapter>(_adaptersByConnectId);
        AdapterById = new ReadOnlyDictionary<RtEntityId, Adapter>(_adaptersById);
    }

    // Serializes the connection-lifecycle mutations (add / update-connection /
    // remove / remove-connection) so a check-then-act stays atomic. Without it,
    // a stale unregister/disconnect from an OLD connection could interleave
    // between a reconnect's re-register and its own removal, clobbering the
    // freshly registered live connection (AB#4594). PublishConfiguration is a
    // cheap no-op today, so holding the lock across it is safe.
    private readonly object _connectionLock = new();

    public Adapter AddAdapter(RtEntityId adapterRtEntityId, string connectionId, AdapterConfigurationDto configuration)
    {
        lock (_connectionLock)
        {
            var adapter = new Adapter(_adapterCachePublish, adapterRtEntityId, connectionId, configuration);
            _adaptersByConnectId.AddOrUpdate(connectionId, _ => adapter,
                (_, _) => adapter);
            _adaptersById.AddOrUpdate(adapterRtEntityId, _ => adapter,
                (_, _) => adapter);

            _adapterCachePublish.PublishConfiguration(TenantId);

            return adapter;
        }
    }

    public void RemoveAdapter(RtEntityId adapterRtEntityId)
    {
        lock (_connectionLock)
        {
            RemoveAdapterCore(adapterRtEntityId);
        }
    }

    /// <summary>
    /// Removes the adapter ONLY when its currently cached connection id still equals
    /// <paramref name="connectionId"/>. This is the stale-connection guard for the
    /// reconnect race (AB#4594): a graceful UnRegister (or a late OnDisconnected) from an
    /// OLD connection must not remove an adapter that has already re-registered on a NEWER
    /// connection. The compare and the removal run under <see cref="_connectionLock"/>, so a
    /// concurrent AddAdapter / UpdateConnectionId cannot slip in between them — unlike the
    /// previous unconditional RemoveAdapter, which could delete the fresh registration and
    /// leave every subsequent deploy failing with AdapterNotLoaded ("no live SignalR connection").
    /// </summary>
    /// <returns><c>true</c> if the adapter was removed; <c>false</c> when a newer connection is current (no-op).</returns>
    public bool RemoveAdapterIfConnection(RtEntityId adapterRtEntityId, string connectionId)
    {
        lock (_connectionLock)
        {
            if (_adaptersById.TryGetValue(adapterRtEntityId, out var adapter)
                && adapter.ConnectionId == connectionId)
            {
                RemoveAdapterCore(adapterRtEntityId);
                return true;
            }

            return false;
        }
    }

    private void RemoveAdapterCore(RtEntityId adapterRtEntityId)
    {
        if (_adaptersById.TryRemove(adapterRtEntityId, out var adapter))
        {
            if (!string.IsNullOrWhiteSpace(adapter.ConnectionId))
            {
                if (_adaptersByConnectId.TryRemove(adapter.ConnectionId, out _))
                {
                    _adapterCachePublish.PublishConfiguration(TenantId);
                }
            }
        }
    }

    public void UpdateConnectionId(RtEntityId adapterRtEntityId, string connectionId)
    {
        lock (_connectionLock)
        {
            if (_adaptersById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                var oldConnectionId = adapter.ConnectionId;
                adapter.SetConnectionId(connectionId);

                if (!string.IsNullOrWhiteSpace(oldConnectionId))
                {
                    _adaptersByConnectId.TryRemove(oldConnectionId, out _);
                }
                _adaptersByConnectId.AddOrUpdate(connectionId, _ => adapter,
                    (_, _) => adapter);
                _adapterCachePublish.PublishConfiguration(TenantId);
            }
        }
    }

    public void RemoveConnectionId(RtEntityId adapterRtEntityId)
    {
        lock (_connectionLock)
        {
            RemoveConnectionIdCore(adapterRtEntityId);
        }
    }

    /// <summary>
    /// Clears the adapter's connection id ONLY when it still equals <paramref name="connectionId"/>.
    /// Same atomic stale-connection guard as <see cref="RemoveAdapterIfConnection"/>, so a late
    /// OnDisconnected from an old connection cannot null out a live reconnected one (AB#4594).
    /// </summary>
    /// <returns><c>true</c> if the connection id was cleared; <c>false</c> when a newer connection is current.</returns>
    public bool RemoveConnectionIdIfConnection(RtEntityId adapterRtEntityId, string connectionId)
    {
        lock (_connectionLock)
        {
            if (_adaptersById.TryGetValue(adapterRtEntityId, out var adapter)
                && adapter.ConnectionId == connectionId)
            {
                RemoveConnectionIdCore(adapterRtEntityId);
                return true;
            }

            return false;
        }
    }

    private void RemoveConnectionIdCore(RtEntityId adapterRtEntityId)
    {
        if (_adaptersById.TryGetValue(adapterRtEntityId, out var adapter) && !string.IsNullOrWhiteSpace(adapter.ConnectionId))
        {
            _adaptersByConnectId.TryRemove(adapter.ConnectionId, out _);
            adapter.SetConnectionId(null);
            _adapterCachePublish.PublishConfiguration(TenantId);
        }
    }

    public IEnumerable<AdapterDescription> GetAdapterDescriptions()
    {
        return AdapterById.Values.Select(p => p.GetAdapterDescription()).ToArray();
    }
}