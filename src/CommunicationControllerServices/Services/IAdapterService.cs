using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Interface for adapter service, that is responsible for managing adapters and their state
/// </summary>
public interface IAdapterService
{
    /// <summary>
    /// (Re)loads an entire tenant during update or during enabling 
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Unloads an entire tenant if a tenant gets deleted or disabled
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task UnloadTenantAsync(string tenantId);
    
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
    /// <returns></returns>
    Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Sets an adapter online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <param name="connectionId">The connection identifier</param>
    /// <returns></returns>
    Task SetAdapterOnlineAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId);
    
    /// <summary>
    /// Sets an adapter offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="adapterRtEntityId">Object Id of adapter</param>
    /// <returns></returns>
    Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId);
    
    /// <summary>
    /// Deployes the db version  an adapter configuration
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
}