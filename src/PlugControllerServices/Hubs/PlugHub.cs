using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

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
            // await _plugManagementService.PlugOnline(tenantId, Context.ConnectionId);
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
            // await _plugManagementService.PlugOffline(tenantId, Context.ConnectionId);
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
        return await _plugManagementService.RegisterPlug(tenantId, plugObjectId, Context.ConnectionId);
    }
}