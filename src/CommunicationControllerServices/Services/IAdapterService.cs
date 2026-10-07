using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Interface for adapter service, that is responsible for managing adapters and their state
/// </summary>
public interface IAdapterService
{
    /// <summary>
    /// Updates an entire tenant before a tenant is deleted or disabled for communication.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task PreUpdateTenantAsync(string tenantId);
    
    /// <summary>
    /// Loads an entire tenant after a tenant has been created or enabled.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task PosUpdateTenantAsync(string tenantId);

    /// <summary>
    /// Notifies all connected adapters of the tenant that the Construction Kit model may have
    /// changed (CK model import, cache clear) so they invalidate their in-process CK caches (AB#4456).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task CkModelChangedAsync(string tenantId);
    
    /// <summary>
    /// Registers an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);

    /// <summary>
    /// Registers an adapter with node descriptors
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <param name="nodeDescriptors">Pipeline node descriptors provided by the adapter</param>
    /// <returns></returns>
    Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId, IReadOnlyList<NodeDescriptorDto> nodeDescriptors);

    /// <summary>
    /// Registers an adapter with node descriptors and a pipeline schema
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <param name="nodeDescriptors">Pipeline node descriptors provided by the adapter</param>
    /// <param name="pipelineSchemaJson">Composite JSON Schema for the full pipeline definition</param>
    /// <returns></returns>
    Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId, IReadOnlyList<NodeDescriptorDto> nodeDescriptors, string pipelineSchemaJson);

    /// <summary>
    /// Gets the pipeline schema for a specific adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <returns>The pipeline schema JSON, or null if not available</returns>
    string? GetPipelineSchema(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Gets aggregated node descriptors from all connected adapters for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of node descriptors from all connected adapters</returns>
    IReadOnlyList<NodeDescriptorDto> GetAllNodeDescriptors(string tenantId);
    
    /// <summary>
    /// Unregisters an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task UnregisterAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);

    /// <summary>
    /// Gets an adapter configuration for a given tenant and adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="onlyDeployedPipelines">Include only deployed pipelines</param>
    /// <returns></returns>
    Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId,
        bool onlyDeployedPipelines);

    /// <summary>
    /// Sets an adapter online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">The connection identifier</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateOnlineAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);
    
    /// <summary>
    /// Sets an adapter offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">The connection identifier of the disconnecting connection</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);

    /// <summary>
    /// Reconciles adapters of <paramref name="tenantId"/> that are persisted as <c>Online</c> but
    /// have no live SignalR connection on this pod, marking them <c>Offline</c> (AB#4699).
    /// Liveness is judged against <see cref="IAdapterConnectionTracker"/> (not the config cache,
    /// which is flushed by tenant updates while connections survive), and re-checked immediately
    /// before each write to avoid racing a concurrent reconnect. Callers must respect a startup
    /// grace before invoking so adapters have time to (re)connect after a controller restart.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>The number of adapters reconciled to Offline.</returns>
    Task<int> ReconcileOrphanedOnlineAdaptersAsync(string tenantId);

    /// <summary>
    /// Deploys the db version  an adapter configuration
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <returns></returns>
    Task DeployAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Deploys a pipeline to the given adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of adapter</param>
    /// <param name="pipelineRtEntityId">Object id of pipeline</param>
    /// <param name="pipelineDefinition">Temporary pipeline definition</param>
    /// <returns></returns>
    Task DeployPipelineAsync(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId, string? pipelineDefinition = null);

    /// <summary>
    /// Deploys a data flow to its adapters
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Runtime id of data flow</param>
    /// <returns></returns>
    Task DeployDataFlowAsync(string tenantId, OctoObjectId dataFlowRtId);

    /// <summary>
    /// Enables or disables debug capture for a single pipeline. Persists the flag on the pipeline
    /// RT entity and, when the owning adapter is online, re-pushes the data flow configuration so the
    /// change takes effect on the running adapter (without altering the deploy force-enable behavior).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">Object id of pipeline</param>
    /// <param name="isEnabled">true to enable debug capture, false to disable</param>
    /// <returns>true if the change was applied to a live adapter; false if only persisted.</returns>
    Task<bool> SetPipelineDebuggingAsync(string tenantId, RtEntityId pipelineRtEntityId, bool isEnabled);

    /// <summary>
    /// Undeploys a data flow from its adapters
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Runtime id of data flow</param>
    /// <returns></returns>
    Task UndeployDataFlowAsync(string tenantId, OctoObjectId dataFlowRtId);

    /// <summary>
    /// Updates the configuration state of an adapter
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">ID of the adapter</param>
    /// <param name="deploymentResult"></param>
    /// <returns></returns>
    Task UpdateConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId, DeploymentResult deploymentResult);

    /// <summary>
    /// Writes the status line a trigger node reported for one of the adapter's pipelines to that
    /// pipeline's <c>StatusMessage</c> (AB#5385). The pipeline must be one of the pipelines
    /// deployed to the reporting adapter — the adapter identity comes from the hub connection, so
    /// that check is what keeps an adapter from writing another adapter's pipeline; a report for a
    /// pipeline the adapter does not run is rejected and logged, never written. The line is
    /// truncated to <see cref="AdapterService.MaxPipelineStatusMessageLength"/>.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object id of the reporting adapter</param>
    /// <param name="status">The reported status line</param>
    /// <returns>True when the line was written, false when the report was rejected</returns>
    Task<bool> ReportPipelineStatusAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineStatusReportDto status);

    /// <summary>
    /// Gets the deployment state of a pipeline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="pipelineRtEntityId">ID of the pipeline</param>
    /// <returns></returns>
    Task<DeploymentResultDto> GetPipelineDeploymentStateAsync(string tenantId, RtEntityId pipelineRtEntityId);

    /// <summary>
    /// Returns a summary list of all adapters for a tenant with typed enum states.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of adapter summaries with typed communication, configuration, and deployment states</returns>
    Task<IReadOnlyList<AdapterSummaryDto>> GetAdapterSummariesAsync(string tenantId);

    /// <summary>
    /// Appends a resource-utilisation sample reported by the adapter to the in-memory
    /// ring buffer. Silently no-ops if the tenant or adapter is not currently cached
    /// (e.g. mid-reconnect) — the next sample will succeed.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="sample">The metrics sample from the adapter</param>
    void RecordMetricsSample(string tenantId, AdapterMetricsSampleDto sample);

    /// <summary>
    /// AB#5827: records a metrics sample that arrived on <paramref name="connectionId"/> and tells
    /// the caller whether that connection lost its registration. A connection whose registration
    /// this controller accepted, but whose adapter is no longer in the tenant's adapter cache — and
    /// which was never told to restart — is "deaf": every push goes nowhere while the adapter
    /// believes it is registered. Once that has lasted
    /// the orphaned-registration grace period (30 s), the result is
    /// <see cref="MetricsSampleOutcome.RegistrationLost" /> (exactly once per connection) and the
    /// hub asks the adapter to register again.
    /// </summary>
    /// <param name="tenantId">Tenant identifier of the connection</param>
    /// <param name="connectionId">The SignalR connection the sample arrived on</param>
    /// <param name="sample">The metrics sample from the adapter</param>
    MetricsSampleOutcome RecordMetricsSample(string tenantId, string connectionId, AdapterMetricsSampleDto sample);

    /// <summary>
    /// Returns the buffered metrics samples for an adapter in chronological order.
    /// Throws <see cref="AdapterServiceException"/> when the tenant or adapter is not
    /// known so the REST controller can surface a 404.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="since">When provided, only samples newer than this UTC timestamp are returned.</param>
    /// <returns>The list of samples, oldest first.</returns>
    IReadOnlyList<AdapterMetricsSampleDto> GetMetricsSamples(string tenantId, RtEntityId adapterRtEntityId, DateTime? since);
}