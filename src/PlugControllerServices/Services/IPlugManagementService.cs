using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

public interface IPlugManagementService
{
    Task<PlugConfiguration> RegisterPlug(string tenantId, OctoObjectId plugObjectId, string connectionId);
    Task PlugUnRegistered(string tenantId, OctoObjectId plugObjectId, string connectionId);
    
    Task<PlugConfiguration> GetPlugConfiguration(string tenantId, OctoObjectId plugObjectId);

    event EventHandler<UpdatedPlugConfigurationEventArgs> PlugConfigurationUpdated;
    Task PlugOffline(string tenantId, string connectionId);
    Task PlugOnline(string tenantId, string contextConnectionId);
    Task<IEnumerable<OctoObjectId>> GetPlugs(string tenantId);

}