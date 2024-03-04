using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class Adapter(
    IAdapterCachePublish adapterCachePublish,
    OctoObjectId adapterRtId,
    string? connectionId,
    AdapterConfigurationDto configuration)
{
    public Adapter(IAdapterCachePublish adapterCachePublish, AdapterDescription adapterHubPoolDescription)
        : this(adapterCachePublish, adapterHubPoolDescription.AdapterRtId, adapterHubPoolDescription.ConnectionId, adapterHubPoolDescription.Configuration)
    {
    }

    public OctoObjectId AdapterRtId { get; } = adapterRtId;

    public string? ConnectionId { get; private set; } = connectionId;

    public AdapterConfigurationDto Configuration { get; private set; } = configuration;

    public void UpdateConfiguration(string tenantId, AdapterConfigurationDto adapterConfigurationDto)
    {
        Configuration = adapterConfigurationDto;
        adapterCachePublish.PublishConfiguration(tenantId);
    }

    public AdapterDescription GetAdapterDescription()
    {
        return new AdapterDescription(AdapterRtId, ConnectionId, Configuration);
    }
    
    public void SetConnectionId(string? connectionId)
    {
        ConnectionId = connectionId;
    }
}