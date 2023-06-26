using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools.Descriptions;

internal class PoolPlugDescription
{
    public OctoObjectId PlugRtId { get; set; }
    public OctoObjectId PoolRtId { get; set; }
    
    public PoolCommunicationAdapterDto AdapterDto { get; set; }
}