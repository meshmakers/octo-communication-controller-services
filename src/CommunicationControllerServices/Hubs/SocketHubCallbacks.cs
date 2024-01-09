using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

internal class SocketHubCallbacks : ISocketHubCallbacks
{
    private readonly IHubContext<SocketHub> _socketHubContext;
    private readonly ISocketCache _socketCache;

    public SocketHubCallbacks(IHubContext<SocketHub> socketHubContext, ISocketCache socketCache)
    {
        _socketHubContext = socketHubContext;
        _socketCache = socketCache;
    }
    
    public async Task SocketConfigurationUpdatedAsync(string tenantId, SocketConfigurationDto socketConfiguration)
    {
        if (_socketCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.SocketsById.TryGetValue(socketConfiguration.SocketRtId, out var socket);
            if (socket != null)
            {
                await _socketHubContext.Clients.Client(socket.ConnectionId)
                    .SendAsync(nameof(ISocketHubCallbacks.SocketConfigurationUpdatedAsync), tenantId, socketConfiguration);
            }
        }
    }
}