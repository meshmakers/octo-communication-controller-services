using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

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
    Task<OctoObjectId> UnregisterPoolOperatorAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Gets the current adapters in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool runtime entity</param>
    /// <returns></returns>
    Task<PoolConfigurationDto> GetPoolConfigurationAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// (Re)loads an entire tenant during update or during enabling 
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Unloads an entire tenant if a tenant gets deleted or disabled.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task UnloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Deploys an new adapter to a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="adapterRtId">The object id of the adapter</param>
    /// <returns></returns>
    Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId adapterRtId);
    
    /// <summary>
    /// Undeploy an adapter from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="adapterRtId">The object id of the adapter</param>
    /// <returns></returns>
    Task UndeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId adapterRtId);

    /// <summary>
    /// Sets a pool offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetPoolOfflineAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// Sets a pool offline using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <returns></returns>
    Task SetPoolOfflineAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Sets a pool online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetPoolOnlineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool online using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="connectionId">Connection id of pool</param>
    /// <returns></returns>
    Task SetPoolOnlineAsync(string tenantId, string poolName, string connectionId);
}