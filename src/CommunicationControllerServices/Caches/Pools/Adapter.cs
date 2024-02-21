using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class Adapter
{
    public Adapter(OctoObjectId adapterRtId, OctoObjectId poolRtId, PoolCommunicationAdapterDto poolCommunicationAdapterDto)
    {
        AdapterRtId = adapterRtId;
        PoolRtId = poolRtId;
        AdapterDto = poolCommunicationAdapterDto;
    }

    public Adapter(PoolAdapterDescription poolAdapterDescription)
    {
        AdapterRtId = poolAdapterDescription.AdapterRtId;
        PoolRtId = poolAdapterDescription.PoolRtId;
        AdapterDto = poolAdapterDescription.AdapterDto;
    }
    
    public PoolCommunicationAdapterDto AdapterDto { get; }
    public OctoObjectId AdapterRtId { get; }
    public OctoObjectId PoolRtId { get; }


    public PoolAdapterDescription GetPoolAdapterDescription()
    {
        return new PoolAdapterDescription { AdapterRtId = AdapterRtId, PoolRtId = PoolRtId, AdapterDto = AdapterDto };
    }
}