using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Repository for pool related operations
/// </summary>
internal interface ICommunicationRepository
{
    /// <summary>
    /// Get all sockets from a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Object id of pool</param>
    /// <returns></returns>
    Task<IReadOnlyCollection<RtSocket>> GetSocketsAsync(string tenantId, OctoObjectId poolRtId);
    
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
    /// Gets a socket by object id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object id of socket</param>
    /// <returns></returns>
    Task<RtSocket> GetSocketAsync(string tenantId, OctoObjectId socketRtId);
    
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
    Task SetPoolStateAsync(string tenantId, OctoObjectId poolRtId, RtPoolStateEnum state);

    /// <summary>
    /// Set the state of a socket
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object id of socket</param>
    /// <param name="adapterState">State of adapter</param>
    /// <returns></returns>
    Task SetSocketStateAsync(string tenantId, OctoObjectId socketRtId, RtAdapterStateEnum adapterState);
    
    /// <summary>
    /// Set the state of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object id of plug</param>
    /// <param name="adapterState">State of adapter</param>
    /// <returns></returns>
    Task SetPlugStateAsync(string tenantId, OctoObjectId plugRtId, RtAdapterStateEnum adapterState);

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