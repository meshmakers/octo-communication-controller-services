using Meshmakers.Octo.Backend.DeviceManagementServices.Services;
using Meshmakers.Octo.Common.Shared;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Hubs;

public class PlugHub : Hub
{
    private readonly IPlugManagementService _plugManagementService;

    public PlugHub(IPlugManagementService plugManagementService)
    {
        _plugManagementService = plugManagementService;
    }

    public override Task OnConnectedAsync()
    {
        if (Context.Items.TryGetValue("PlugObjectId", out var item) && item != null)
        {
            var plugObjectId = (OctoObjectId)item;
            _plugManagementService.RegisterPlug(plugObjectId, Context.ConnectionId);
        }
        else
        {
            Context.Abort();
        }
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
      
        return base.OnDisconnectedAsync(exception);
    }
    
    public async Task ConnectPlugAsync(OctoObjectId plugObjectId)
    {
        _plugManagementService.RegisterPlug(plugObjectId, Context.ConnectionId);
    }
}