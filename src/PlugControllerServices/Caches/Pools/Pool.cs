using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;

internal class Pool
{
    private readonly IPoolCachePublish _poolCachePublish;

    public Pool(IPoolCachePublish poolCachePublish, OctoObjectId plugPoolRtId, string poolName, string connectionId)
    {
        _poolCachePublish = poolCachePublish;
        PlugPoolRtId = plugPoolRtId;
        PoolName = poolName;
        ConnectionId = connectionId;
    }

    public Pool(IPoolCachePublish poolCachePublish, Descriptions.PoolDescription poolDescription)
    {
        _poolCachePublish = poolCachePublish;
        PlugPoolRtId = poolDescription.PlugPoolRtId;
        PoolName = poolDescription.PoolName;
        ConnectionId = poolDescription.ConnectionId;
    }

    public string PoolName { get; }
    public OctoObjectId PlugPoolRtId { get; }

    public string ConnectionId { get; private set; }
    
    public void UpdateConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
        _poolCachePublish.PublishConfiguration();
    }

    public Descriptions.PoolDescription GetPoolDescription()
    {
        return new Descriptions.PoolDescription
        {
            ConnectionId = ConnectionId,
            PoolName = PoolName,
            PlugPoolRtId = PlugPoolRtId
        };
    }
}