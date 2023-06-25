using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class SocketService : ISocketService
{
    public Task SetSocketOnlineAsync(string tenantId, OctoObjectId socketRtId)
    {
        return Task.CompletedTask;
    }

    public Task SetSocketOfflineAsync(string tenantId, OctoObjectId socketRtId)
    {
        return Task.CompletedTask;
    }

    public Task<SocketConfigurationDto> RegisterSocketAsync(string tenantId, OctoObjectId socketRtId, string connectionId)
    {
        return Task.FromResult(new SocketConfigurationDto{ SocketRtId = socketRtId });
    }

    public Task SocketUnRegisteredAsync(string tenantId, OctoObjectId socketRtId, string connectionId)
    {
        return Task.CompletedTask;
    }
}