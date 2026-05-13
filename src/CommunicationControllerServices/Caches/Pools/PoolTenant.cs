using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class PoolTenant
{
    private readonly IPoolCachePublish _poolCachePublish;
    public string TenantId { get; }

    private readonly ConcurrentDictionary<OctoObjectId, Pool> _poolsById;
    private readonly ConcurrentDictionary<string, Pool> _poolsByName;

    public IReadOnlyDictionary<OctoObjectId, Pool> PoolsById => _poolsById;
    public IReadOnlyDictionary<string, Pool> PoolsByName => _poolsByName;

    public PoolTenant(IPoolCachePublish poolCachePublish, string tenantId)
    {
        _poolCachePublish = poolCachePublish;

        TenantId = tenantId;
        _poolsById = new ConcurrentDictionary<OctoObjectId, Pool>();
        _poolsByName = new ConcurrentDictionary<string, Pool>();
    }

    public Pool AddPool(string poolName, OctoObjectId poolRtId, string connectionId)
    {
        var pool = new Pool(_poolCachePublish, poolRtId, poolName, connectionId);
        _poolsById.AddOrUpdate(poolRtId, _ => pool,
            (_, _) => pool);
        _poolsByName.AddOrUpdate(poolName, _ => pool,
            (_, _) => pool);
        _poolCachePublish.PublishConfigurationAsync(TenantId);

        return pool;
    }

    public void RemovePool(OctoObjectId poolRtId)
    {
        if (_poolsById.TryRemove(poolRtId, out var adapterHubPool))
        {
            if (_poolsByName.TryRemove(adapterHubPool.PoolName, out _))
            {
                _poolCachePublish.PublishConfigurationAsync(TenantId);
            }
        }
    }

    public void Clear()
    {
        _poolsById.Clear();
        _poolsByName.Clear();

        _poolCachePublish.PublishConfigurationAsync(TenantId);
    }
}
