using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools.Descriptions;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DistributedCache;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;

internal class PoolHubCache : IPoolCachePublish, IPoolCache
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, PoolTenant> _tenantDescriptions = new();
    private IChannel<IEnumerable<PoolTenantDescription>>? _channel;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PoolHubCache(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }
    
    public async Task InitializeAsync()
    {
        Logger.Debug("Initializing PoolHubCache");
        
        if (_channel == null)
        {
            _channel = SubscribeToPlugHubConfigurationUpdates();
            var configuration = await _distributedWithPubSubCache.GetLastMessageAsync<IEnumerable<PoolTenantDescription>>(CacheCommon.KeyCommunicationControllerPoolUpdate);
            if (configuration != null)
            {
                ReloadConfiguration(configuration);
            }
        }
    }

    public PoolTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            var plugHubTenant = new PoolTenant(this, tenantId);
            tenantDescription = _tenantDescriptions.AddOrUpdate(tenantId, _ => plugHubTenant,
                (_, _) => plugHubTenant);
            
            PublishConfiguration();
        }
        return tenantDescription;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration();
    }

    public bool TryGetTenant(string tenantId, out PoolTenant? poolTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out poolTenant);
    }

    public bool HasTenant(string tenantId)
    {
        return _tenantDescriptions.ContainsKey(tenantId);
    }

    private IChannel<IEnumerable<PoolTenantDescription>> SubscribeToPlugHubConfigurationUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<IEnumerable<PoolTenantDescription>>(CacheCommon.KeyCommunicationControllerPoolUpdate);
        channel.OnMessage(channelMessage =>
        {
            if (channelMessage.Message != null)
            {
                ReloadConfiguration(channelMessage.Message);
            }

            return Task.CompletedTask;
        });
        return channel;
    }

    private void ReloadConfiguration(IEnumerable<PoolTenantDescription> configuration)
    {
        Logger.Info("Reloading PoolHubCache configuration: {Configuration}", configuration.Serialize());
        
        _tenantDescriptions.Clear();
        foreach (var poolTenantDescription in configuration)
        {
            var plugHubTenant = new PoolTenant(this, poolTenantDescription.TenantId, 
                poolTenantDescription.Pools.ToList(), poolTenantDescription.Plugs.ToList());
            _tenantDescriptions.AddOrUpdate(poolTenantDescription.TenantId, _ => plugHubTenant, (_, _) => plugHubTenant);
        }
    }

    public void PublishConfiguration()
    {
        Logger.Info("Publishing PoolHubCache configuration");

        var tenantDescriptions= 
            _tenantDescriptions.Select(x => x.Value.GetTenantDescription()).ToArray();

        _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyCommunicationControllerPoolUpdate, tenantDescriptions);
        Logger.Info("Published PoolHubCache configuration: {Configuration}", tenantDescriptions.Serialize());
    }

}