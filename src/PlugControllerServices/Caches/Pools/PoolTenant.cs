using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Descriptions;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;

internal class PoolTenant
{
    private readonly IPoolCachePublish _poolCachePublish;
    public string TenantId { get; }
    
    private readonly ConcurrentDictionary<string, Pool> _poolsByConnectId;
    private readonly ConcurrentDictionary<OctoObjectId, Pool> _poolsById;
    private readonly ConcurrentDictionary<string, Pool> _poolsByName;
    private readonly ConcurrentDictionary<OctoObjectId, Plug> _plugsById;

    public IReadOnlyDictionary<string, Pool> PoolsByConnectionId { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Pool> PoolsById { get; private set; }
    public IReadOnlyDictionary<string, Pool> PoolsByName { get; private set; }
    public IReadOnlyDictionary<OctoObjectId, Plug> PlugsById { get; private set; }

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;
        _poolsByConnectId = new ConcurrentDictionary<string, Pool>();
        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>();
        _poolsByName = new ConcurrentDictionary<string, Pool>();
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>();
        
        PoolsByConnectionId = new ReadOnlyDictionary<string, Pool>(_poolsByConnectId);
        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
    }
    
    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId, IList<Pools.Descriptions.PoolDescription> poolDescriptions, IList<PoolPlugDescription> poolPlugDescriptions)
    {
        _poolCachePublish = poolCachePublish;
        
        TenantId = tenantId;
        _poolsByConnectId = new ConcurrentDictionary<string, Pool>(
            poolDescriptions.ToDictionary(p => p.ConnectionId, p => new Pool(p)));
        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>(
            poolDescriptions.ToDictionary(p => p.PlugPoolRtId, p => new Pool(p)));
        _poolsByName = new ConcurrentDictionary<string, Pool>(
            poolDescriptions.ToDictionary(p => p.PoolName, p => new Pool(p)));
        _plugsById = new ConcurrentDictionary<OctoObjectId, Plug>(
            poolPlugDescriptions.ToDictionary(p => p.PlugRtId, p => new Plug(p.PlugRtId, p.PoolRtId)));
        
        PoolsByConnectionId = new ReadOnlyDictionary<string, Pool>(_poolsByConnectId);
        PoolsById = new ReadOnlyDictionary<OctoObjectId, Pool>(_poolsById);
        PoolsByName = new ReadOnlyDictionary<string, Pool>(_poolsByName);
        PlugsById = new ReadOnlyDictionary<OctoObjectId, Plug>(_plugsById);
    }

    public Pool AddPool(string poolName, OctoObjectId plugPoolRtId, string connectionId)
    {
        var pool = new Pool(plugPoolRtId, poolName, connectionId);
        _poolsByConnectId.AddOrUpdate(connectionId, _ => pool,
            (_, _) => pool);
        _poolsById.AddOrUpdate(plugPoolRtId, _ => pool,
            (_, _) => pool);
        _poolsByName.AddOrUpdate(poolName, _ => pool,
            (_, _) => pool);
        _poolCachePublish.PublishConfiguration();

        return pool;
    }

    public void RemovePool(OctoObjectId plugPoolRtId)
    {
        if (_poolsById.TryRemove(plugPoolRtId, out var plugHubPool))
        {
            if (_poolsByConnectId.TryRemove(plugHubPool.ConnectionId, out _))
            {
                if (_poolsByName.TryRemove(plugHubPool.PoolName, out _))
                {
                    _poolCachePublish.PublishConfiguration();
                }
            }
        }
    }

    public PoolTenantDescription GetTenantDescription()
    {
        return new PoolTenantDescription
        {
            TenantId = TenantId,
            Plugs = PlugsById.Values.Select(p => p.GetPoolPlugDescription()),
            Pools = PoolsById.Values.Select(p => p.GetPoolDescription())
        };
    }

    public void AddPlug(Plug plug)
    {
        _plugsById.AddOrUpdate(plug.PlugRtId,
            _ => plug,
            (_, _) => plug);
    }

    public void ClearPlugs(OctoObjectId poolRtId)
    {
        foreach (var plug in _plugsById.Values.ToArray())
        {
            if (plug.PoolRtId == poolRtId)
            {
                _plugsById.Remove(plug.PlugRtId, out _);
            }
        }
    }
    
    public void RemovePlug(OctoObjectId plugRtId)
    {
        _plugsById.Remove(plugRtId, out _);
    }
}