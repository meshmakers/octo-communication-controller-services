using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;

internal class AdapterCache
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
        // AB#5537: never dump the configuration — its pipeline configurations carry revealed
        // credentials (ConfigurationSecretSerializer). Identify the update, not its content.
        Logger.Debug("Reloading AdapterCache configuration for tenant '{TenantId}' ({AdapterCount} adapter(s))",
            configuration.TenantId, configuration.Adapters.Count());

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
    

    public Task PublishConfigurationAsync(string tenantId)
    {
        Logger.Info("Publishing AdapterCache configuration for tenant '{TenantId}'", tenantId);

        // 🔴 AB#5537: the cached adapter configurations hold REVEALED secrets (plaintext for the
        // adapter channel). Do not re-enable this publish without masking them first — it would put
        // credentials into the distributed cache and onto the message bus.
        // if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        // {
        //     using MemoryStream memoryStream = new MemoryStream();
        //     await JsonSerializer.SerializeAsync(memoryStream, desc.GetAdapterDescriptions());
        //     await memoryStream.FlushAsync();
        //     memoryStream.Position = 0;
        //     await distributedCacheService.CreateOrUpdateStreamAsync(tenantId, memoryStream, "application/json", Constants.CacheFileName);
        //     
        //     await distributionEventHubService.PublishAsync(new ComControllerAdapterUpdate(tenantId, desc.GetAdapterDescriptions()));
        // }
        return Task.CompletedTask;
    }
}