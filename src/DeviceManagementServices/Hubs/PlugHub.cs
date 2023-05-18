using Meshmakers.Octo.Backend.DeviceManagementServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Hubs;

internal class PlugHub : Hub
{
    private readonly IPlugManagementService _plugManagementService;

    public PlugHub(IPlugManagementService plugManagementService)
    {
        _plugManagementService = plugManagementService;
    }

    public override async Task OnConnectedAsync()
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId != null)
        {
            await _plugManagementService.PlugOnline(tenantId, Context.ConnectionId);
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
            await _plugManagementService.PlugOffline(tenantId, Context.ConnectionId);
        }
        else
        {
            Context.Abort();
        }
        await base.OnDisconnectedAsync(exception);
    }
    
    public async Task<PlugConfiguration> RegisterPlug(OctoObjectId plugObjectId)
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }
        return await _plugManagementService.RegisterPlug(tenantId, plugObjectId, Context.ConnectionId);
    }
}