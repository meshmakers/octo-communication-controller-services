using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Services;

internal class ConfigurationService : IConfigurationService
{
    private readonly ISystemContext _systemContext;
    private readonly IDistributedWithPubSubCache _distributedCache;
    private readonly Dictionary<string, TenantDescription> _tenantDescriptions = new();

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
        
        await _distributedCache.PublishAsync(CacheCommon.KeyPlugControllerUpdate, tenantId);
    }

    public TenantDescription GetOrAddTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            tenantDescription = new TenantDescription(tenantId);
            _tenantDescriptions.Add(tenantId, tenantDescription);
        }

        return tenantDescription;
    }
}