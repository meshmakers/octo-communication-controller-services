using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs.Descriptions;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs;

internal class PlugCache : IPlugCachePublish, IPlugCache
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, PlugTenant> _tenantDescriptions = new();
    private IChannel<IEnumerable<PlugTenantDescription>>? _channel;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PlugCache(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }
    
    public async Task InitializeAsync()
    {
        Logger.Debug("Initializing PlugCache");
        
        if (_channel == null)
        {
            _channel = SubscribeToPlugHubConfigurationUpdates();
            var configuration = await _distributedWithPubSubCache.GetLastMessageAsync<IEnumerable<PlugTenantDescription>>(CacheCommon.KeyPlugControllerPlugUpdate);
            if (configuration != null)
            {
                ReloadConfiguration(configuration);
            }
        }
    }

    public PlugTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var plugTenant))
        {
            var newPlugTenant = new PlugTenant(this, tenantId);
            plugTenant = _tenantDescriptions.AddOrUpdate(tenantId, _ => newPlugTenant,
                (_, _) => newPlugTenant);
            
            PublishConfiguration();
        }
        return plugTenant;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration();
    }

    public bool TryGetTenant(string tenantId, out PlugTenant? plugTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out plugTenant);
    }
    
    private IChannel<IEnumerable<PlugTenantDescription>> SubscribeToPlugHubConfigurationUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<IEnumerable<PlugTenantDescription>>(CacheCommon.KeyPlugControllerPlugUpdate);
        channel.OnMessage(message =>
        {
            if (message.Message != null)
            {
                ReloadConfiguration(message.Message);
            }

            return Task.CompletedTask;
        });
        return channel;
    }

    private void ReloadConfiguration(IEnumerable<PlugTenantDescription> configuration)
    {
        Logger.Info("Reloading PlugCache configuration: {Configuration}", configuration.Serialize());
        
        _tenantDescriptions.Clear();
        foreach (var tenantDescription in configuration)
        {
            var plugHubTenant = new PlugTenant(this, tenantDescription.TenantId, tenantDescription.Plugs.ToList());
            _tenantDescriptions.AddOrUpdate(tenantDescription.TenantId, _ => plugHubTenant, (_, _) => plugHubTenant);
        }
    }

    public void PublishConfiguration()
    {
        Logger.Info("Publishing PlugCache configuration");

        var tenantDescriptions= 
            _tenantDescriptions.Select(x => x.Value.GetTenantDescription()).ToArray();
        
        _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyPlugControllerPlugUpdate, tenantDescriptions);
    }

}