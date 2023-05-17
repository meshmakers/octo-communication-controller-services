using Meshmakers.Octo.Backend.DeviceManagementServices.Models;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Services;

public interface IPlugManagementService
{
    void RegisterPlug(OctoObjectId plugObjectId, string connectionId);
    void PlugUnRegistered(OctoObjectId plugObjectId, string connectionId);

    event EventHandler<UpdatedPlugConfigurationEventArgs> PlugConfigurationUpdated;
}