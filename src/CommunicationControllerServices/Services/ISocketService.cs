using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Interface for socket service, that is responsible for managing sockets and their state
/// </summary>
public interface ISocketService
{
    /// <summary>
    /// Sets a socket online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object identifier of socket</param>
    /// <returns></returns>
    Task SetSocketOnlineAsync(string tenantId, OctoObjectId socketRtId);
    
    /// <summary>
    /// Sets a socket offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object identifier of socket</param>
    /// <returns></returns>
    Task SetSocketOfflineAsync(string tenantId, OctoObjectId socketRtId);

    /// <summary>
    /// Registers a socket
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object identifier of socket</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task<SocketConfigurationDto> RegisterSocketAsync(string tenantId, OctoObjectId socketRtId, string connectionId);

    /// <summary>
    /// Unregisters a socket
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="socketRtId">Object identifier of socket</param>
    /// <param name="connectionId">Identifier of connection</param>
    /// <returns></returns>
    Task SocketUnRegisteredAsync(string tenantId, OctoObjectId socketRtId, string connectionId);
}