using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

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

    public Pool(IPoolCachePublish poolCachePublish, PoolDescription poolDescription)
    {
        _poolCachePublish = poolCachePublish;
        PoolRtId = poolDescription.PoolRtId;
        PoolName = poolDescription.PoolName;
        ConnectionId = poolDescription.ConnectionId;
    }

    public string PoolName { get; }
    public OctoObjectId PoolRtId { get; }

    public string? ConnectionId { get; private set; }
    
    public void UpdateConnectionId(string tenantId, string connectionId)
    {
        ConnectionId = connectionId;
        _poolCachePublish.PublishConfiguration(tenantId);
    }
    
    public void RemoveConnectionId(string tenantId)
    {
        ConnectionId = null;
        _poolCachePublish.PublishConfiguration(tenantId);
    }

    public PoolDescription GetPoolDescription()
    {
        return new PoolDescription
        {
            ConnectionId = ConnectionId,
            PoolName = PoolName,
            PoolRtId = PoolRtId
        };
    }
}