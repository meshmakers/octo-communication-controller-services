using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Repository;

/// <summary>
/// Repository for plug pool related operations
/// </summary>
public interface IPlugRepository
{
    /// <summary>
    /// Get all plugs from a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolId">Object id of plug pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId, OctoObjectId plugPoolId);

    /// <summary>
    /// Gets a list of initialized plugs of the given tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId);
    
    /// <summary>
    /// Gets a plug by object id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object id of plug</param>
    /// <returns></returns>
    Task<RtPlug> GetPlugAsync(string tenantId, OctoObjectId plugRtId);

    /// <summary>
    /// Get plug pools for a tenant by name
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolName">Name of the plug pool</param>
    /// <returns>List of plug pools with the given name</returns>
    Task<IReadOnlyCollection<RtPlugPool>> GetPlugPoolByNameAsync(string tenantId, string plugPoolName);

    /// <summary>
    /// Creates a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of plug pool</param>
    /// <exception cref="PlugRepositoryException"></exception>
    Task CreatePlugPoolAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Set the state of a plug pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugPoolId">Object id of plug pool</param>
    /// <param name="state">State of plug pool</param>
    /// <returns></returns>
    Task SetPlugPoolStateAsync(string tenantId, OctoObjectId plugPoolId, PlugPoolStates state);

    /// <summary>
    /// Set the state of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object id of plug</param>
    /// <param name="state">State of plug pool</param>
    /// <returns></returns>
    Task SetPlugStateAsync(string tenantId, OctoObjectId plugRtId, PlugStates state);

    /// <summary>
    /// Gets the plug pool of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Plug id</param>
    /// <returns></returns>
    Task<RtPlugPool> GetPlugPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId);

    /// <summary>
    /// Gets the corresponding plug of a plug mapping
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugMappingRtId">Object identifier of plug mapping</param>
    /// <returns></returns>
    Task<RtPlug> GetPlugByMappingAsync(string tenantId, OctoObjectId plugMappingRtId);

    /// <summary>
    /// Gets the group/mapping configuration of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object identifier of plug</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<GroupConfigurationDto>> GetPlugGroupConfigurationAsync(string tenantId, OctoObjectId plugRtId);

    /// <summary>
    /// Gets the corresponding plug of a plug group
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugGroupRtId">Object identifier of plug group</param>
    /// <returns></returns>
    Task<RtPlug> GetPlugByGroupAsync(string tenantId, OctoObjectId plugGroupRtId);
}