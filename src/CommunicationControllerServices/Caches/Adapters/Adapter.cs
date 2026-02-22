using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages.Payloads;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class Adapter(
    IAdapterCachePublish adapterCachePublish,
    RtEntityId adapterRtEntityId,
    string? connectionId,
    AdapterConfigurationDto configuration)
{
    public Adapter(IAdapterCachePublish adapterCachePublish, AdapterDescription adapterHubPoolDescription)
        : this(adapterCachePublish, adapterHubPoolDescription.AdapterRtEntityId, adapterHubPoolDescription.ConnectionId, adapterHubPoolDescription.Configuration)
    {
    }

    public RtEntityId AdapterRtEntityId { get; } = adapterRtEntityId;

    public string? ConnectionId { get; private set; } = connectionId;

    public AdapterConfigurationDto Configuration { get; private set; } = configuration;

    /// <summary>
    /// Node descriptors reported by this adapter during registration.
    /// Null if the adapter did not provide any (older adapter version).
    /// </summary>
    public IReadOnlyList<NodeDescriptorDto>? NodeDescriptors { get; private set; }

    /// <summary>
    /// Composite pipeline JSON Schema reported by this adapter during registration.
    /// Null if the adapter did not provide one.
    /// </summary>
    public string? PipelineSchemaJson { get; private set; }

    public void UpdateConfiguration(string tenantId, AdapterConfigurationDto adapterConfigurationDto)
    {
        Configuration = adapterConfigurationDto;
        adapterCachePublish.PublishConfiguration(tenantId);
    }

    public void SetNodeDescriptors(IReadOnlyList<NodeDescriptorDto>? nodeDescriptors)
    {
        NodeDescriptors = nodeDescriptors;
    }

    public void SetPipelineSchema(string? pipelineSchemaJson)
    {
        PipelineSchemaJson = pipelineSchemaJson;
    }

    public AdapterDescription GetAdapterDescription()
    {
        return new AdapterDescription(AdapterRtEntityId, ConnectionId, Configuration);
    }

    public void SetConnectionId(string? connectionId)
    {
        ConnectionId = connectionId;
    }
}