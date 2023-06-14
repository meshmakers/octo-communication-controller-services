using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;

internal class Pool
{
    public Pool(OctoObjectId plugPoolRtId, string poolName, string connectionId)
    {
        PlugPoolRtId = plugPoolRtId;
        PoolName = poolName;
        ConnectionId = connectionId;
    }

    public Pool(Descriptions.PoolDescription poolDescription)
    {
        PlugPoolRtId = poolDescription.PlugPoolRtId;
        PoolName = poolDescription.PoolName;
        ConnectionId = poolDescription.ConnectionId;
    }

    public string PoolName { get; }
    public OctoObjectId PlugPoolRtId { get; }

    public string ConnectionId { get; }

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