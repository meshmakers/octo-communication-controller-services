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
    private readonly ConcurrentDictionary<OctoObjectId, Plug> _plugsById;

    public IReadOnlyDictionary<OctoObjectId, Pool> PoolsById { get; }
    public IReadOnlyDictionary<string, Pool> PoolsByName { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Plug> PlugsById { get; }

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;
        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>();
        _poolsByName = new ConcurrentDictionary<string, Pool>();
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>();

        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
    }

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId, IList<PoolDescription> poolDescriptions,
        IList<PoolPlugDescription> poolPlugDescriptions)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;

        var pools = poolDescriptions.Select(p => new Pool(_poolCachePublish, p)).ToArray();

        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>(
            pools.ToDictionary(p => p.PoolRtId, p => p));
        _poolsByName = new ConcurrentDictionary<string, Pool>(
            pools.ToDictionary(p => p.PoolName, p => p));
        
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>(
            poolPlugDescriptions.ToDictionary(p => p.PlugRtId, p => new Plug(p.PlugRtId, p.PoolRtId, p.AdapterDto)));

        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
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
        if (_poolsById.TryRemove(poolRtId, out var plugHubPool))
        {
            if (_poolsByName.TryRemove(plugHubPool.PoolName, out _))
            {
                _poolCachePublish.PublishConfiguration(TenantId);
            }
        }
    }
    
    public IEnumerable<PoolPlugDescription> GetPoolPlugDescriptions()
    {
        return PlugsById.Values.Select(p => p.GetPoolPlugDescription()).ToArray();
    }
    
    public IEnumerable<PoolDescription> GetPoolDescriptions()
    {
        return PoolsById.Values.Select(p => p.GetPoolDescription()).ToArray();
    }

    public void AddPlug(Plug plug)
    {
        _plugsById.AddOrUpdate(plug.PlugRtId,
            _ => plug,
            (_, _) => plug);
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }

    public void RemovePlug(OctoObjectId plugRtId)
    {
        _plugsById.Remove(plugRtId, out _);
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }
    
    public void RemovePlugs(OctoObjectId poolRtId)
    {
        foreach (var plug in PlugsById.Values.Where(x => x.PoolRtId == poolRtId))
        {
            _plugsById.Remove(plug.PlugRtId, out _);
        }
        
        _poolCachePublish.PublishConfiguration(TenantId);
    }

    public void Clear()
    {
        _poolsById.Clear();
        _poolsByName.Clear();
        _plugsById.Clear();

        _poolCachePublish.PublishConfiguration(TenantId);
    }
}