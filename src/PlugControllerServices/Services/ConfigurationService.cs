using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal class ConfigurationService : IConfigurationService
{
    private readonly ISystemContext _systemContext;
    private readonly IDistributedWithPubSubCache _distributedCache;

    public ConfigurationService(ISystemContext systemContext, IDistributedWithPubSubCache distributedCache)
    {
        _systemContext = systemContext;
        _distributedCache = distributedCache;
        
    }

    public async Task<IEnumerable<PlugControllerStatusDto>> ReadConfig()
    {
        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();
        
        var config = await _systemContext.GetConfigurationAsync(systemSession, Statics.PlugControllerConfigurationName);
        var o = config?.Deserialize<IEnumerable<PlugControllerStatusDto>>() ?? new List<PlugControllerStatusDto>();
        
        await systemSession.CommitTransactionAsync();

        return o;
    }

    public async Task WriteConfig(IEnumerable<PlugControllerStatusDto> config, string tenantId)
    {
        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();
        
        var configString = config.Serialize();
        await _systemContext.SetConfigurationAsync(systemSession, Statics.PlugControllerConfigurationName, configString);
        
        await systemSession.CommitTransactionAsync();
        
        await _distributedCache.PublishAsync(CacheCommon.KeyPlugControllerPoolUpdate, tenantId);
    }
}