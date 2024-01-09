using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class ConfigurationService : IConfigurationService
{
    private readonly ISystemContext _systemContext;
    private readonly IDistributionEventHubService _distributionEventHubService;

    public ConfigurationService(ISystemContext systemContext, IDistributionEventHubService distributionEventHubService)
    {
        _systemContext = systemContext;
        _distributionEventHubService = distributionEventHubService;
        
    }

    public async Task<IEnumerable<CommunicationControllerStatusDto>> ReadConfig()
    {
        using var systemSession = await _systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();
        
        var config = await _systemContext.GetConfigurationAsync(systemSession, Statics.CommunicationControllerConfigurationName);
        var o = config?.Deserialize<IEnumerable<CommunicationControllerStatusDto>>() ?? new List<CommunicationControllerStatusDto>();
        
        await systemSession.CommitTransactionAsync();

        return o;
    }

    public async Task WriteConfig(IEnumerable<CommunicationControllerStatusDto> config, string tenantId)
    {
        using var systemSession = await _systemContext.GetSystemSessionAsync();
        systemSession.StartTransaction();
        
        var configString = config.Serialize();
        await _systemContext.SetConfigurationAsync(systemSession, Statics.CommunicationControllerConfigurationName, configString);
        
        await systemSession.CommitTransactionAsync();
        
       // await _distributionEventHubService.PublishAsync(CacheCommon.KeyCommunicationControllerPoolUpdate, tenantId);
    }
}