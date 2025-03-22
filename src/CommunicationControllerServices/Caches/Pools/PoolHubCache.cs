using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class PoolHubCache : IPoolCachePublish, IPoolCache
{
    private readonly ConcurrentDictionary<string, PoolTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PoolTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            var adapterHubTenant = new PoolTenant(this, tenantId);
            tenantDescription = _tenantDescriptions.AddOrUpdate(tenantId, _ => adapterHubTenant,
                (_, _) => adapterHubTenant);
            
            PublishConfigurationAsync(tenantId);
        }
        return tenantDescription;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfigurationAsync(tenantId);
    }

    public bool TryGetTenant(string tenantId, [NotNullWhen(true)] out PoolTenant? poolTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out poolTenant);
    }

    public bool HasTenant(string tenantId)
    {
        return _tenantDescriptions.ContainsKey(tenantId);
    }

    public Task ReloadConfigurationAsync(ComControllerPoolUpdate configuration)
    {
        Logger.Debug("Reloading PoolHubCache configuration: {Configuration}", configuration.Serialize());
        
        // _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
        //     _ => new PoolTenant(this, configuration.TenantId, configuration.Pools.ToList(), configuration.Adapters.ToList()),
        //     (_, _) => new PoolTenant(this, configuration.TenantId, configuration.Pools.ToList(), configuration.Adapters.ToList()));
        return Task.CompletedTask;
    }

    public Task PublishConfigurationAsync(string tenantId)
    {
        Logger.Info("Publishing PoolHubCache configuration '{TenantId}'", tenantId);

        // if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        // {
        //     var poolDescriptions = desc.GetPoolDescriptions();
        //     var poolAdapterDescriptions = desc.GetPoolAdapterDescriptions();
        //     _distributionEventHubService.PublishAsync(new ComControllerPoolUpdate(tenantId, poolDescriptions, poolAdapterDescriptions));
        // }

        return Task.CompletedTask;
    }

}