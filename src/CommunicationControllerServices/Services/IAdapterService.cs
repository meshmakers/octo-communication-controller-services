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
}