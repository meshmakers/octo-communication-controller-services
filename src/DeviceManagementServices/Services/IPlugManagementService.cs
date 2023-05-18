using Meshmakers.Octo.Backend.DeviceManagementServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.Services;

internal interface IPlugManagementService
{
    Task<PlugConfiguration> RegisterPlug(string tenantId, OctoObjectId plugObjectId, string connectionId);
    void PlugUnRegistered(string tenantId, OctoObjectId plugObjectId, string connectionId);

    event EventHandler<UpdatedPlugConfigurationEventArgs> PlugConfigurationUpdated;
    Task PlugOffline(string tenantId, string connectionId);
    Task PlugOnline(string tenantId, string contextConnectionId);
}