using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class PoolHubCache : IPoolCachePublish, IPoolCache
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ConcurrentDictionary<string, PoolTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PoolHubCache(IDistributionEventHubService distributionEventHubService)
    {
        _distributionEventHubService = distributionEventHubService;
    }

    public PoolTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            var plugHubTenant = new PoolTenant(this, tenantId);
            tenantDescription = _tenantDescriptions.AddOrUpdate(tenantId, _ => plugHubTenant,
                (_, _) => plugHubTenant);
            
            PublishConfiguration(tenantId);
        }
        return tenantDescription;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration(tenantId);
    }

    public bool TryGetTenant(string tenantId, out PoolTenant? poolTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out poolTenant);
    }

    public bool HasTenant(string tenantId)
    {
        return _tenantDescriptions.ContainsKey(tenantId);
    }

    public void ReloadConfiguration(ComControllerPoolUpdate configuration)
    {
        Logger.Info("Reloading PoolHubCache configuration: {Configuration}", configuration.Serialize());
        
        _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
            _ => new PoolTenant(this, configuration.TenantId, configuration.Pools.ToList(), configuration.Plugs.ToList()),
            (_, _) => new PoolTenant(this, configuration.TenantId, configuration.Pools.ToList(), configuration.Plugs.ToList()));
    }

    public void PublishConfiguration(string tenantId)
    {
        Logger.Info("Publishing PoolHubCache configuration '{TenantId}'", tenantId);

        if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        {
            var poolDescriptions = desc.GetPoolDescriptions();
            var poolPlugDescriptions = desc.GetPoolPlugDescriptions();
            _distributionEventHubService.PublishAsync(new ComControllerPoolUpdate(tenantId, poolDescriptions, poolPlugDescriptions));
        }
    }

}