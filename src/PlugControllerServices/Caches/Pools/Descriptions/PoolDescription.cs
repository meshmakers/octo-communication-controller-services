using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Descriptions;

public class PoolDescription
{
    public string PoolName { get; set; } = null!;
    public OctoObjectId PlugPoolRtId { get; set; }

    public string ConnectionId { get; set; } = null!;
}