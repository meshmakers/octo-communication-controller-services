using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Manages pools for all tenants and their state including configuration (adapters loaded in which pools).
/// </summary>
public interface IPoolService
{
    /// <summary>
    /// Registers a pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="connectionId">Connection id to client</param>
    /// <returns></returns>
    Task<OctoObjectId> RegisterPoolOperatorAsync(string tenantId, string poolName, string connectionId);
    
    /// <summary>
    /// Unregisters a pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <returns></returns>
    Task UnregisterPoolOperatorAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Gets the current adapters in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool runtime entity</param>
    /// <returns></returns>
    Task<PoolConfigurationDto> GetPoolConfigurationAsync(string tenantId, OctoObjectId poolRtId);
    
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
    /// Deploys all adapters of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <returns></returns>
    Task DeployAdaptersAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// Undeploys all adapters of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <returns></returns>
    Task UndeployAdaptersAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// Deploys a new adapter to a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="adapterRtEntityId">The object id of the adapter</param>
    /// <returns></returns>
    Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, RtEntityId adapterRtEntityId);
    
    /// <summary>
    /// Undeploy an adapter from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="adapterRtEntityId">The object id of the adapter</param>
    /// <returns></returns>
    Task UndeployAdapterAsync(string tenantId, OctoObjectId poolRtId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Sets a pool offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// Sets a pool offline using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOfflineAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Sets a pool online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool online using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="connectionId">Connection id of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOnlineAsync(string tenantId, string poolName, string connectionId);
    
    /// <summary>
    /// Updates the deployment state of an adapter in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="adapterRtEntityId">The object id of the adapter</param>
    /// <param name="deploymentState">The new deployment state</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, RtEntityId adapterRtEntityId, 
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Updates the deployment state of an adapter in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="adapterRtEntityIds">The object id of the adapters</param>
    /// <param name="deploymentState">The new deployment state</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, ICollection<RtEntityId> adapterRtEntityIds, 
        RtDeploymentStateEnum deploymentState);
}