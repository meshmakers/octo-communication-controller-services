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
    // 30 minutes at the SDK's default 10s sampling interval. The buffer is
    // process-local and not propagated across controller pods — Phase 1 of
    // the adapter telemetry feature accepts loss of history on restart.
    private const int MetricsCapacity = 180;

    private readonly AdapterMetricsRingBuffer _metricsBuffer = new(MetricsCapacity);

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

    /// <summary>
    /// Appends a resource-utilisation sample to the in-memory ring buffer.
    /// Old samples are dropped once the buffer is full.
    /// </summary>
    public void AddMetricsSample(AdapterMetricsSampleDto sample)
    {
        _metricsBuffer.Add(sample);
    }

    /// <summary>
    /// Returns the buffered metrics samples in chronological order. When
    /// <paramref name="since"/> is provided, only samples with a strictly later
    /// timestamp are returned (used by the REST endpoint to support incremental
    /// polling from the UI).
    /// </summary>
    public IReadOnlyList<AdapterMetricsSampleDto> GetMetricsSamples(DateTime? since = null)
    {
        return _metricsBuffer.Snapshot(since);
    }
}