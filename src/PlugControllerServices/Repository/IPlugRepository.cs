using Meshmakers.Octo.Backend.PlugControllerServices.CkModelEntities;
using Meshmakers.Octo.Common.Shared;

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
    /// Gets the plug pool of a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Plug id</param>
    /// <returns></returns>
    Task<RtPlugPool> GetPlugPoolOfPlugAsync(string tenantId, OctoObjectId plugRtId);
}