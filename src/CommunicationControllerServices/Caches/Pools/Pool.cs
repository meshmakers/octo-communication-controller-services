using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class Pool
{
    private readonly IPoolCachePublish _poolCachePublish;

    public Pool(IPoolCachePublish poolCachePublish, OctoObjectId poolRtId, string poolName, string connectionId)
    {
        _poolCachePublish = poolCachePublish;
        PoolRtId = poolRtId;
        PoolName = poolName;
        ConnectionId = connectionId;
    }

    public Pool(IPoolCachePublish poolCachePublish, Descriptions.PoolDescription poolDescription)
    {
        _poolCachePublish = poolCachePublish;
        PoolRtId = poolDescription.PoolRtId;
        PoolName = poolDescription.PoolName;
        ConnectionId = poolDescription.ConnectionId;
    }

    public string PoolName { get; }
    public OctoObjectId PoolRtId { get; }

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
            PoolRtId = PoolRtId
        };
    }
}