using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PoolDescription
{
    public string PoolName { get; }
    public OctoObjectId PlugPoolRtId { get; }
    
    

    public PoolDescription(OctoObjectId plugPoolRtId, string poolName)
    {
        PlugPoolRtId = plugPoolRtId;
        PoolName = poolName;
    }
}