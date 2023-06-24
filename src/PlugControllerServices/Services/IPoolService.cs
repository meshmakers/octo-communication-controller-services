using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

/// <summary>
/// Manages plug pools for all tenants and their state including configuration (plugs loaded in which pools).
/// </summary>
public interface IPoolService
{
    /// <summary>
    /// Registers a plug pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolName">Name of plug pool</param>
    /// <param name="connectionId">Connection id to client</param>
    /// <returns></returns>
    Task<OctoObjectId> RegisterPlugPoolOperatorAsync(string tenantId, string plugPoolName, string connectionId);
    
    /// <summary>
    /// Unregisters a plug pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolName">Name of plug pool</param>
    /// <returns></returns>
    Task<OctoObjectId> UnregisterPlugPoolOperatorAsync(string tenantId, string plugPoolName);
    
    /// <summary>
    /// Gets the current plugs in a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">The object id of the plug pool runtime entity</param>
    /// <returns></returns>
    Task<PlugPoolConfigurationDto> GetCurrentPlugsAsync(string tenantId, OctoObjectId plugPoolRtId);
    
    /// <summary>
    /// Reloads an entire tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task ReloadTenantAsync(string tenantId);
    
    /// <summary>
    /// Deploys a new plug to a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">The object id of the plug pool</param>
    /// <param name="plugRtId">The object id of the plug</param>
    /// <returns></returns>
    Task DeployPlugAsync(string tenantId, OctoObjectId plugPoolRtId, OctoObjectId plugRtId);
    
    /// <summary>
    /// Undeploy a plug from a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">The object id of the plug pool</param>
    /// <param name="plugRtId">The object id of the plug</param>
    /// <returns></returns>
    Task UndeployPlugAsync(string tenantId, OctoObjectId plugPoolRtId, OctoObjectId plugRtId);

    /// <summary>
    /// Sets a plug pool offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">Identifier of plug pool</param>
    /// <returns></returns>
    Task SetPoolOfflineAsync(string tenantId, OctoObjectId plugPoolRtId);
    
    /// <summary>
    /// Sets a plug pool offline using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of plug pool</param>
    /// <returns></returns>
    Task SetPoolOfflineAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Sets a plug pool online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">Identifier of plug pool</param>
    /// <returns></returns>
    Task SetPoolOnlineAsync(string tenantId, OctoObjectId plugPoolRtId);

    /// <summary>
    /// Sets a plug pool online using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of plug pool</param>
    /// <param name="connectionId">Connection id of pool</param>
    /// <returns></returns>
    Task SetPoolOnlineAsync(string tenantId, string poolName, string connectionId);

    /// <summary>
    /// Handles a plug pool update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePoolUpdateAsync(string tenantId, UpdateInfo<RtPlugPool> info);

    /// <summary>
    /// Handles a plug update
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="info">Update information object</param>
    /// <returns></returns>
    Task OnHandlePlugUpdateAsync(string tenantId, UpdateInfo<RtPlug> info);
}