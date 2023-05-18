using Meshmakers.Octo.Backend.DeviceManagementServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Services;

internal class PlugManagementService : IPlugManagementService
{
    private readonly Dictionary<OctoObjectId, string> _plugConnections = new();

    public PlugManagementService()
    {
        
    }
    
    public async Task<PlugConfiguration> RegisterPlug(string tenantId, OctoObjectId plugObjectId, string connectionId)
    {
        _plugConnections[plugObjectId] = connectionId;
        return Statics.PlugTestConfig;
    }

    public void PlugUnRegistered(string tenantId, OctoObjectId plugObjectId, string connectionId)
    {
        _plugConnections.Remove(plugObjectId);
    }

    public event EventHandler<UpdatedPlugConfigurationEventArgs>? PlugConfigurationUpdated;
    public Task PlugOffline(string tenantId, string connectionId)
    {
        return Task.CompletedTask;
    }

    public Task PlugOnline(string tenantId, string contextConnectionId)
    {
        return Task.CompletedTask;
    }
}