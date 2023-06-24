using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class ConfigurationService : IConfigurationService
{
    private readonly ISystemContext _systemContext;
    private readonly IDistributedWithPubSubCache _distributedCache;

    public ConfigurationService(ISystemContext systemContext, IDistributedWithPubSubCache distributedCache)
    {
        _systemContext = systemContext;
        _distributedCache = distributedCache;
        
    }

    public async Task<IEnumerable<CommunicationControllerStatusDto>> ReadConfig()
    {
        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();
        
        var config = await _systemContext.GetConfigurationAsync(systemSession, Statics.CommunicationControllerConfigurationName);
        var o = config?.Deserialize<IEnumerable<CommunicationControllerStatusDto>>() ?? new List<CommunicationControllerStatusDto>();
        
        await systemSession.CommitTransactionAsync();

        return o;
    }

    public async Task WriteConfig(IEnumerable<CommunicationControllerStatusDto> config, string tenantId)
    {
        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();
        
        var configString = config.Serialize();
        await _systemContext.SetConfigurationAsync(systemSession, Statics.CommunicationControllerConfigurationName, configString);
        
        await systemSession.CommitTransactionAsync();
        
        await _distributedCache.PublishAsync(CacheCommon.KeyCommunicationControllerPoolUpdate, tenantId);
    }
}