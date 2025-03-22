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

    public Adapter AddAdapter(RtEntityId adapterRtEntityId, string connectionId, AdapterConfigurationDto configuration)
    {
        var adapter = new Adapter(_adapterCachePublish, adapterRtEntityId, connectionId, configuration);
        _adaptersByConnectId.AddOrUpdate(connectionId, _ => adapter,
            (_, _) => adapter);
        _adaptersById.AddOrUpdate(adapterRtEntityId, _ => adapter,
            (_, _) => adapter);
        
        _adapterCachePublish.PublishConfiguration(TenantId);

        return adapter;
    }

    public void RemoveAdapter(RtEntityId adapterRtEntityId)
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
    
    public void RemoveConnectionId(RtEntityId adapterRtEntityId)
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