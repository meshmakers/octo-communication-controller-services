using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Sockets.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Sockets.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

public class SocketHub : Hub, ISocketHub
{
    private readonly ISocketService _socketService;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SocketHub(ISocketService socketService)
    {
        _socketService = socketService;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var socketRtId = GetSocketRtId();

        await _socketService.SetSocketOnlineAsync(tenantId, socketRtId);

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        var socketRtId = GetSocketRtId();

        await _socketService.SetSocketOfflineAsync(tenantId, socketRtId);

        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<SocketConfigurationDto> RegisterSocketAsync(OctoObjectId socketRtId)
    {
        var tenantId = GetTenantId();
        
        try
        {
            var configurationDto = await _socketService.RegisterSocketAsync(tenantId, socketRtId, Context.ConnectionId);

            await _socketService.SetSocketOnlineAsync(tenantId, socketRtId);

            return configurationDto;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register socket");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnRegisterSocketAsync(OctoObjectId socketRtId)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        await _socketService.SocketUnRegisteredAsync(tenantId, socketRtId, Context.ConnectionId);
    }
    
    private string GetTenantId()
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        return tenantId;
    }
    
    private OctoObjectId GetSocketRtId()
    {
        var socketRtId = Context.GetHttpContext()?.GetSocketRtId();
        if (socketRtId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("SocketRtId is null");
        }

        return socketRtId.Value;
    }
}