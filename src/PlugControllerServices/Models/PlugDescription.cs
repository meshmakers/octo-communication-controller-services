using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public class PlugDescription
{
    public OctoObjectId PoolRtId { get; }
    public OctoObjectId PlugRtId { get; }

    public PlugStates State { get; set; } = PlugStates.Pending;

    public PlugDescription(OctoObjectId plugRtId, OctoObjectId poolRtId)
    {
        PoolRtId = poolRtId;
        PlugRtId = plugRtId;
    }
}