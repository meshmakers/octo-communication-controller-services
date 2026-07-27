using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class AdapterCache(IDistributionEventHubService distributionEventHubService)
    : IAdapterCachePublish, IAdapterCache
{
    private readonly ConcurrentDictionary<string, AdapterTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

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

    public bool TryGetTenant(string tenantId, [NotNullWhen(true)] out AdapterTenant? adapterTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out adapterTenant);
    }

    public IReadOnlyList<string> GetEnabledTenantIds()
    {
        return _tenantDescriptions.Keys.ToList();
    }

    public Task ReloadConfigurationAsync(ComControllerAdapterUpdate configuration)
    {
        Logger.Debug("Reloading AdapterCache configuration: {Configuration}", configuration.Serialize());

        _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
            _ => new AdapterTenant(this, configuration.TenantId, configuration.Adapters.ToList()),
            (_, _) => new AdapterTenant(this, configuration.TenantId, configuration.Adapters.ToList()));

        return Task.CompletedTask;
    }
    
    public Task LoadConfigurationAsync(string tenantId)
    {
        Logger.Info("Loading AdapterCache configuration from cache for tenant id '{TenantId}'", tenantId);

        return Task.CompletedTask;
        // var cacheStream = await distributedCacheService.GetCacheStreamByFileNameAsync(tenantId, Constants.CacheFileName);
        // if (cacheStream != null)
        // {
        //     using var reader = new StreamReader(cacheStream.Stream);
        //     var adapterDescriptions = JsonSerializer.Deserialize<AdapterDescription[]>(reader.BaseStream);
        //     if (adapterDescriptions != null)
        //     {
        //         var adapterTenant = new AdapterTenant(this, tenantId, adapterDescriptions);
        //         _tenantDescriptions.AddOrUpdate(tenantId, _ => adapterTenant, (_, _) => adapterTenant);   
        //     }
        // }
    }

    public void PublishConfiguration(string tenantId)
    {
        PublishConfigurationAsync(tenantId).Wait();
    }
    

    public async Task PublishConfigurationAsync(string tenantId)
    {
        Logger.Info("Publishing AdapterCache configuration for tenant '{TenantId}'", tenantId);

        if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        {
            await distributionEventHubService.PublishAsync(
                new ComControllerAdapterUpdate(tenantId, Guid.NewGuid(), DateTime.UtcNow,
                    desc.GetAdapterDescriptions().ToList()));
        }
    }
}