using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class AdapterCache : IAdapterCachePublish, IAdapterCache
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ConcurrentDictionary<string, AdapterTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public AdapterCache(IDistributionEventHubService distributionEventHubService)
    {
        _distributionEventHubService = distributionEventHubService;
    }

    public AdapterTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var adapterTenant))
        {
            var newAdapterTenant = new AdapterTenant(this, tenantId);
            adapterTenant = _tenantDescriptions.AddOrUpdate(tenantId, _ => newAdapterTenant,
                (_, _) => newAdapterTenant);
            
            PublishConfiguration(tenantId);
        }
        return adapterTenant;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration(tenantId);
    }

    public bool TryGetTenant(string tenantId, out AdapterTenant? adapterTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out adapterTenant);
    }

    public void ReloadConfiguration(ComControllerAdapterUpdate configuration)
    {
        Logger.Info("Reloading AdapterCache configuration: {Configuration}", configuration.Serialize());

        _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
            _ => new AdapterTenant(this, configuration.TenantId, configuration.Adapters.ToList()),
            (_, _) => new AdapterTenant(this, configuration.TenantId, configuration.Adapters.ToList()));
    }

    public void PublishConfiguration(string tenantId)
    {
        Logger.Info("Publishing AdapterCache configuration for tenant '{TenantId}'", tenantId);

        if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        {
            _distributionEventHubService.PublishAsync(new ComControllerAdapterUpdate(tenantId, desc.GetAdapterDescriptions()));
        }
    }

}