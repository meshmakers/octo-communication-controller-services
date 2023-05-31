using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

internal class PlugHub : Hub, IPlugHub
{
    private readonly IPlugService _plugService;

    public PlugHub(IPlugService plugService)
    {
        _plugService = plugService;
    }

    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId != null)
        {
            // await _plugService.PlugOnline(tenantId, Context.ConnectionId);
        }
        else
        {
            Context.Abort();
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId != null)
        {
            // await _plugService.PlugOffline(tenantId, Context.ConnectionId);
        }
        else
        {
            Context.Abort();
        }
        await base.OnDisconnectedAsync(exception);
    }
    
    public async Task<PlugConfigurationDto> RegisterPlug(OctoObjectId plugObjectId)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }
        return await _plugService.RegisterPlug(tenantId, plugObjectId, Context.ConnectionId);
    }
}