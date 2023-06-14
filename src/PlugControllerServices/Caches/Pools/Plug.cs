using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Descriptions;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;

internal class Plug
{
    public Plug(OctoObjectId plugRtId, OctoObjectId poolRtId)
    {
        PlugRtId = plugRtId;
        PoolRtId = poolRtId;
    }

    public Plug(PoolPlugDescription poolPlugDescription)
    {
        PlugRtId = poolPlugDescription.PlugRtId;
    }

    public OctoObjectId PlugRtId { get; }
    public OctoObjectId PoolRtId { get; }


    public PoolPlugDescription GetPoolPlugDescription()
    {
        return new PoolPlugDescription { PlugRtId = PlugRtId, PoolRtId = PoolRtId };
    }
}