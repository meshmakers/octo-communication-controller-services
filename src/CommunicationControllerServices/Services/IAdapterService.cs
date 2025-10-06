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
    /// <returns></returns>
    Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId);

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
    /// Deploys a data pipeline to its adapters
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataPipelineRtId">Runtime id of data pipeline</param>
    /// <returns></returns>
    Task DeployDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId);
    
    /// <summary>
    /// Undeploys a data pipeline from its adapters
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataPipelineRtId">Runtime id of data pipeline</param>
    /// <returns></returns>
    Task UndeployDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId);

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
}