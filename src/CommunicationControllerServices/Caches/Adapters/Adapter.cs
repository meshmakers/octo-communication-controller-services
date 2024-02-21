using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class Adapter
{
    private readonly IAdapterCachePublish _adapterCachePublish;

    public Adapter(IAdapterCachePublish adapterCachePublish, OctoObjectId adapterRtId, string connectionId, AdapterConfigurationDto configuration)
    {
        _adapterCachePublish = adapterCachePublish;
        AdapterRtId = adapterRtId;
        ConnectionId = connectionId;
        Configuration = configuration;
    }

    public Adapter(IAdapterCachePublish adapterCachePublish, AdapterDescription adapterHubPoolDescription)
        : this(adapterCachePublish, adapterHubPoolDescription.AdapterRtId, adapterHubPoolDescription.ConnectionId, adapterHubPoolDescription.Configuration)
    {
    }

    public OctoObjectId AdapterRtId { get; }

    public string ConnectionId { get; private set; }
    
    public AdapterConfigurationDto Configuration { get; private set; }

    public void UpdateConfiguration(string tenantId, AdapterConfigurationDto adapterConfigurationDto)
    {
        Configuration = adapterConfigurationDto;
        _adapterCachePublish.PublishConfiguration(tenantId);
    }

    public void UpdateConnectionId(string connectionId)
    {
        ConnectionId = connectionId;
    }

    public AdapterDescription GetAdapterDescription()
    {
        return new AdapterDescription(AdapterRtId, ConnectionId, Configuration);
    }
}