using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools.Descriptions;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;

internal class PoolHubCache : IPoolCachePublish, IPoolCache
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, PoolTenant> _tenantDescriptions = new();
    private IChannel<string>? _channel;

    public PoolHubCache(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }
    
    public async Task InitializeAsync()
    {
        if (_channel == null)
        {
            _channel = SubscribeToPlugHubConfigurationUpdates();
            var configuration = await _distributedWithPubSubCache.GetLastMessageAsStringAsync(CacheCommon.KeyPlugControllerPoolUpdate);
            if (!string.IsNullOrWhiteSpace(configuration))
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

    private IChannel<string> SubscribeToPlugHubConfigurationUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyPlugControllerPoolUpdate);
        channel.OnMessage(message =>
        {
            if (!string.IsNullOrWhiteSpace(message.Message))
            {
                ReloadConfiguration(message.Message);
            }

            return Task.CompletedTask;
        });
        return channel;
    }

    private void ReloadConfiguration(string configuration)
    {
        var values = configuration.Deserialize<IEnumerable<PoolTenantDescription>>();
        
        _tenantDescriptions.Clear();
        foreach (var poolTenantDescription in values)
        {
            var plugHubTenant = new PoolTenant(this, poolTenantDescription.TenantId, 
                poolTenantDescription.Pools.ToList(), poolTenantDescription.Plugs.ToList());
            _tenantDescriptions.AddOrUpdate(poolTenantDescription.TenantId, _ => plugHubTenant, (_, _) => plugHubTenant);
        }
    }

    public void PublishConfiguration()
    {
        var tenantDescriptions= 
            _tenantDescriptions.Select(x => x.Value.GetTenantDescription());
        
        _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyPlugControllerPoolUpdate, tenantDescriptions.Serialize());
    }

}