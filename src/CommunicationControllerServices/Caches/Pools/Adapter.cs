using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class Adapter
{
    public Adapter(RtEntityId adapterRtEntityId, OctoObjectId poolRtId, PoolCommunicationAdapterDto poolCommunicationAdapterDto)
    {
        AdapterRtEntityId = adapterRtEntityId;
        PoolRtId = poolRtId;
        AdapterDto = poolCommunicationAdapterDto;
    }

    public Adapter(PoolAdapterDescription poolAdapterDescription)
    {
        AdapterRtEntityId = poolAdapterDescription.AdapterRtEntityId;
        PoolRtId = poolAdapterDescription.PoolRtId;
        AdapterDto = poolAdapterDescription.AdapterDto;
    }
    
    public PoolCommunicationAdapterDto AdapterDto { get; }
    public RtEntityId AdapterRtEntityId { get; }
    public OctoObjectId PoolRtId { get; }


    public PoolAdapterDescription GetPoolAdapterDescription()
    {
        return new PoolAdapterDescription { AdapterRtEntityId = AdapterRtEntityId, PoolRtId = PoolRtId, AdapterDto = AdapterDto };
    }
}