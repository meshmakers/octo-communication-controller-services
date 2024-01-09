using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class Plug
{
    public Plug(OctoObjectId plugRtId, OctoObjectId poolRtId, PoolCommunicationAdapterDto poolCommunicationAdapterDto)
    {
        PlugRtId = plugRtId;
        PoolRtId = poolRtId;
        AdapterDto = poolCommunicationAdapterDto;
    }

    public Plug(PoolPlugDescription poolPlugDescription)
    {
        PlugRtId = poolPlugDescription.PlugRtId;
        PoolRtId = poolPlugDescription.PoolRtId;
        AdapterDto = poolPlugDescription.AdapterDto;
    }
    
    public PoolCommunicationAdapterDto AdapterDto { get; }
    public OctoObjectId PlugRtId { get; }
    public OctoObjectId PoolRtId { get; }


    public PoolPlugDescription GetPoolPlugDescription()
    {
        return new PoolPlugDescription { PlugRtId = PlugRtId, PoolRtId = PoolRtId, AdapterDto = AdapterDto };
    }
}