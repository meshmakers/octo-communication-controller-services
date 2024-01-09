using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Interface for plug service, that is responsible for managing plugs and their state
/// </summary>
public interface IPlugService
{
    /// <summary>
    /// Registers a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object Id of plug</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task<PlugConfigurationDto> RegisterPlugAsync(string tenantId, OctoObjectId plugRtId, string connectionId);
    
    /// <summary>
    /// Unregisters a plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object Id of plug</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task PlugUnRegisteredAsync(string tenantId, OctoObjectId plugRtId, string connectionId);
    
    /// <summary>
    /// Gets a plug configuration for a given tenant and plug
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object Id of plug</param>
    /// <returns></returns>
    Task<PlugConfigurationDto> GetPlugConfigurationAsync(string tenantId, OctoObjectId plugRtId);

    /// <summary>
    /// Sets a plug online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object Id of plug</param>
    /// <returns></returns>
    Task SetPlugOnlineAsync(string tenantId, OctoObjectId plugRtId);
    
    /// <summary>
    /// Sets a plug offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="plugRtId">Object Id of plug</param>
    /// <returns></returns>
    Task SetPlugOfflineAsync(string tenantId, OctoObjectId plugRtId);
}