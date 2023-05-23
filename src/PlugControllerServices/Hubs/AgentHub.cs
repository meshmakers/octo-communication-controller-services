using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

public class AgentHub : Hub
{
    private readonly IAgentService _agentService;

    public AgentHub(IAgentService agentService)
    {
        _agentService = agentService;
    }
    
    public override async Task OnConnectedAsync()
    {


        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {

        await base.OnDisconnectedAsync(exception);
    }

    public async Task RegisterAgent(string tenantId, string agentName)
    {
        
    }

}