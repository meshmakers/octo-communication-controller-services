using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Manages pools for all tenants and their state including configuration (plugs loaded in which pools).
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
    /// Gets the current plugs in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool runtime entity</param>
    /// <returns></returns>
    Task<PoolConfigurationDto> GetCurrentAdapterAsync(string tenantId, OctoObjectId poolRtId);
    
    /// <summary>
    /// Reloads an entire tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Deploys a new plug to a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="plugRtId">The object id of the plug</param>
    /// <returns></returns>
    Task DeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId);
    
    /// <summary>
    /// Undeploy a plug from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    /// <param name="plugRtId">The object id of the plug</param>
    /// <returns></returns>
    Task UndeployAdapterAsync(string tenantId, OctoObjectId poolRtId, OctoObjectId plugRtId);

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