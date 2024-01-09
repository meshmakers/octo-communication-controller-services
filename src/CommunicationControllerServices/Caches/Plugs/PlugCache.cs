using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;

internal class PlugCache : IPlugCachePublish, IPlugCache
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ConcurrentDictionary<string, PlugTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PlugCache(IDistributionEventHubService distributionEventHubService)
    {
        _distributionEventHubService = distributionEventHubService;
    }

    public PlugTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var plugTenant))
        {
            var newPlugTenant = new PlugTenant(this, tenantId);
            plugTenant = _tenantDescriptions.AddOrUpdate(tenantId, _ => newPlugTenant,
                (_, _) => newPlugTenant);
            
            PublishConfiguration(tenantId);
        }
        return plugTenant;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration(tenantId);
    }

    public bool TryGetTenant(string tenantId, out PlugTenant? plugTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out plugTenant);
    }

    public void ReloadConfiguration(ComControllerPlugUpdate configuration)
    {
        Logger.Info("Reloading PlugCache configuration: {Configuration}", configuration.Serialize());

        _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
            _ => new PlugTenant(this, configuration.TenantId, configuration.Plugs.ToList()),
            (_, _) => new PlugTenant(this, configuration.TenantId, configuration.Plugs.ToList()));
    }

    public void PublishConfiguration(string tenantId)
    {
        Logger.Info("Publishing PlugCache configuration for tenant '{TenantId}'", tenantId);

        if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        {
            _distributionEventHubService.PublishAsync(new ComControllerPlugUpdate(tenantId, desc.GetPlugDescriptions()));
        }
    }

}