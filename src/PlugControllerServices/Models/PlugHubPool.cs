using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PlugHubPool
{
    public PlugHubPool(OctoObjectId plugPoolRtId, string poolName, string connectionId)
    {
        PlugPoolRtId = plugPoolRtId;
        PoolName = poolName;
        ConnectionId = connectionId;
    }

    public PlugHubPool(PlugHubPoolDescription plugHubPoolDescription)
    {
        PlugPoolRtId = plugHubPoolDescription.PlugPoolRtId;
        PoolName = plugHubPoolDescription.PoolName;
        ConnectionId = plugHubPoolDescription.ConnectionId;
    }

    public string PoolName { get; set; }
    public OctoObjectId PlugPoolRtId { get; set; }

    public string ConnectionId { get; set; }

    public PlugHubPoolDescription GetPoolDescription()
    {
        return new PlugHubPoolDescription
        {
            ConnectionId = ConnectionId,
            PoolName = PoolName,
            PlugPoolRtId = PlugPoolRtId
        };
    }
}