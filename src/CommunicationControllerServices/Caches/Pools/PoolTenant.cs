using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class PoolTenant
{
    private readonly IPoolCachePublish _poolCachePublish;
    public string TenantId { get; }

    private readonly ConcurrentDictionary<OctoObjectId, Pool> _poolsById;
    private readonly ConcurrentDictionary<string, Pool> _poolsByName;
    private readonly ConcurrentDictionary<RtEntityId, Adapter> _adaptersById;

    public IReadOnlyDictionary<OctoObjectId, Pool> PoolsById { get; }
    public IReadOnlyDictionary<string, Pool> PoolsByName { get; private set; }
    public IReadOnlyDictionary<RtEntityId, Adapter> AdaptersById { get; }

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;
        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>();
        _poolsByName = new ConcurrentDictionary<string, Pool>();
        _adaptersById = new ConcurrentDictionary<RtEntityId, Adapter>();

        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        AdaptersById = new ReadOnlyDictionary<RtEntityId, Adapter>(_adaptersById);
    }

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId, IList<PoolDescription> poolDescriptions,
        IList<PoolAdapterDescription> poolAdapterDescriptions)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;

        var pools = poolDescriptions.Select(p => new Pool(_poolCachePublish, p)).ToArray();

        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>(
            pools.ToDictionary(p => p.PoolRtId, p => p));
        _poolsByName = new ConcurrentDictionary<string, Pool>(
            pools.ToDictionary(p => p.PoolName, p => p));
        
        _adaptersById = new ConcurrentDictionary<RtEntityId, Adapter>(
            poolAdapterDescriptions.ToDictionary(p => p.AdapterRtEntityId, p => new Adapter(p.AdapterRtEntityId, p.PoolRtId, p.AdapterDto)));

        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        AdaptersById = new ReadOnlyDictionary<RtEntityId, Adapter>(_adaptersById);
    }

    public Pool AddPool(string poolName, OctoObjectId poolRtId, string connectionId)
    {
        var pool = new Pool(_poolCachePublish, poolRtId, poolName, connectionId);
        _poolsById.AddOrUpdate(poolRtId, _ => pool,
            (_, _) => pool);
        _poolsByName.AddOrUpdate(poolName, _ => pool,
            (_, _) => pool);
        _poolCachePublish.PublishConfiguration(TenantId);

        return pool;
    }

    public void RemovePool(OctoObjectId poolRtId)
    {
        if (_poolsById.TryRemove(poolRtId, out var adapterHubPool))
        {
            if (_poolsByName.TryRemove(adapterHubPool.PoolName, out _))
            {
                _poolCachePublish.PublishConfiguration(TenantId);
            }
        }
    }
    
    public IEnumerable<PoolAdapterDescription> GetPoolAdapterDescriptions()
    {
        return AdaptersById.Values.Select(p => p.GetPoolAdapterDescription()).ToArray();
    }
    
    public IEnumerable<PoolDescription> GetPoolDescriptions()
    {
        return PoolsById.Values.Select(p => p.GetPoolDescription()).ToArray();
    }

    public void AddAdapter(Adapter adapter)
    {
        _adaptersById.AddOrUpdate(adapter.AdapterRtEntityId,
            _ => adapter,
            (_, _) => adapter);
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }

    public void RemoveAdapter(RtEntityId adapterRtEntityId)
    {
        _adaptersById.Remove(adapterRtEntityId, out _);
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }
    
    public void RemoveAdapters(OctoObjectId poolRtId)
    {
        foreach (var adapter in AdaptersById.Values.Where(x => x.PoolRtId == poolRtId))
        {
            _adaptersById.Remove(adapter.AdapterRtEntityId, out _);
        }
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }

    public void Clear()
    {
        _poolsById.Clear();
        _poolsByName.Clear();
        _adaptersById.Clear();

        _poolCachePublish.PublishConfiguration(TenantId);
    }
}