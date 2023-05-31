using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

/// <summary>
/// Manages plug pools for all tenants
/// </summary>
public interface IPlugPoolService
{
    /// <summary>
    /// Registers a hub to receive plug pool updates
    /// </summary>
    /// <param name="addPlug">Callback for adding plug events</param>
    /// <param name="removePlug">Callback for removing plug events</param>
    void RegisterHub(Func<string, PlugPoolPlugDto, Task> addPlug, Func<string, PlugPoolPlugDto, Task> removePlug);
    
    /// <summary>
    /// Registers a plug pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolName">Name of plug pool</param>
    /// <returns></returns>
    Task<OctoObjectId> RegisterPlugPoolOperatorAsync(string tenantId, string plugPoolName);
    
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
    Task ReloadTenant(string tenantId);
    
    /// <summary>
    /// Deploys a new plug to a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="rtPlug">The runtime entity object of the plug</param>
    /// <returns></returns>
    Task DeployPlugAsync(string tenantId, RtPlug rtPlug);
    
    /// <summary>
    /// Updates the deployment of a plug in a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="rtPlug">The runtime entity object of the plug</param>
    /// <returns></returns>
    Task UpdateDeploymentPlugAsync(string tenantId, RtPlug rtPlug);
    
    /// <summary>
    /// Undeploy a plug from a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="rtPlug">The runtime entity object of the plug</param>
    /// <returns></returns>
    Task UndeployPlugAsync(string tenantId, RtPlug rtPlug);

    /// <summary>
    /// Sets a plug pool offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">Identifier of plug pool</param>
    /// <returns></returns>
    Task SetPoolOfflineAsync(string tenantId, OctoObjectId plugPoolRtId);
    
    /// <summary>
    /// Sets a plug pool online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolRtId">Identifier of plug pool</param>
    /// <returns></returns>
    Task SetPoolOnlineAsync(string tenantId, OctoObjectId plugPoolRtId);
}