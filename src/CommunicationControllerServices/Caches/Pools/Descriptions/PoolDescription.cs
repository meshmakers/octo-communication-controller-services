using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools.Descriptions;

internal class PoolDescription
{
    public string PoolName { get; set; } = null!;
    public OctoObjectId PoolRtId { get; set; }

    public string ConnectionId { get; set; } = null!;
}