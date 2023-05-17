using Meshmakers.Octo.Backend.DeviceManagementServices.Models;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Services;

public class PlugManagementService : IPlugManagementService
{
    private readonly Dictionary<OctoObjectId, string> _plugConnections = new();

    public PlugManagementService()
    {
        
    }
    
    public void RegisterPlug(OctoObjectId plugObjectId, string connectionId)
    {
        _plugConnections[plugObjectId] = connectionId;
    }

    public void PlugUnRegistered(OctoObjectId plugObjectId, string connectionId)
    {
        _plugConnections.Remove(plugObjectId);
    }

    public event EventHandler<UpdatedPlugConfigurationEventArgs>? PlugConfigurationUpdated;
}