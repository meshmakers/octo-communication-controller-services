using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
public interface IPlugRepository
{
    /// <summary>
    /// Get all plugs from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtPlug>> GetPlugsAsync(string tenantId, OctoObjectId poolRtId);

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
    /// Get pools for a tenant by name
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of the pool</param>
    /// <returns>List of pools with the given name</returns>
    Task<IReadOnlyCollection<RtCommunicationPool>> GetPoolByNameAsync(string tenantId, string poolName);

    /// <summary>
    /// Creates a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <exception cref="PlugRepositoryException"></exception>
    Task CreatePoolAsync(string tenantId, string poolName);
    
    /// <summary>
    /// Set the state of a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <param name="state">State of pool</param>
    /// <returns></returns>
    Task SetPoolStateAsync(string tenantId, OctoObjectId poolRtId, PoolStates state);

    /// <summary>
    /// Set the state of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object id of plug</param>
    /// <param name="state">State of pool</param>
    /// <returns></returns>
    Task SetPlugStateAsync(string tenantId, OctoObjectId plugRtId, PlugStates state);

    /// <summary>
    /// Gets the pool of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Plug id</param>
    /// <returns></returns>
    Task<RtCommunicationPool> GetPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId);

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

    /// <summary>
    /// Returns true if a tenant exists
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task<bool> IsTenantExistingAsync(string tenantId);
}