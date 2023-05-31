using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Models;

public interface IPlugHubContextPublish
{
    public void PublishConfiguration();
}

internal class PlugHubContext : IPlugHubContextPublish, IPlugHubContext
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, PlugHubTenant> _tenantDescriptions = new();
    private IChannel<string>? _channel;

    public PlugHubContext(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }
    
    public async Task InitializeAsync()
    {
        if (_channel == null)
        {
            _channel = SubscribeToPlugHubConfigurationUpdates();
            var configuration = await _distributedWithPubSubCache.GetLastMessageAsStringAsync(CacheCommon.KeyPlugControllerUpdate);
            if (!string.IsNullOrWhiteSpace(configuration))
            {
                ReloadConfiguration(configuration);
            }
        }
    }

    public PlugHubTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var tenantDescription))
        {
            var plugHubTenant = new PlugHubTenant(this, tenantId);
            tenantDescription = _tenantDescriptions.AddOrUpdate(tenantId, _ => plugHubTenant,
                (_, _) => plugHubTenant);
        }
        return tenantDescription;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
    }

    public PlugHubTenant? TryGetTenant(string tenantId)
    {
        _tenantDescriptions.TryGetValue(tenantId, out var tenantDescription);
        return tenantDescription;
    }
    
    private IChannel<string> SubscribeToPlugHubConfigurationUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<string>(CacheCommon.KeyPlugControllerUpdate);
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
        var values = configuration.Deserialize<IEnumerable<PlugHubTenantDescription>>();
        
        _tenantDescriptions.Clear();
        foreach (var tenantDescription in values)
        {
            var plugHubTenant = new PlugHubTenant(this, tenantDescription.TenantId, tenantDescription.Pools.ToList());
            _tenantDescriptions.AddOrUpdate(tenantDescription.TenantId, _ => plugHubTenant, (_, _) => plugHubTenant);
        }
    }

    public void PublishConfiguration()
    {
        var tenantDescriptions= 
            _tenantDescriptions.Select(x => x.Value.GetTenantDescription());
        
        _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyPlugControllerUpdate, tenantDescriptions.Serialize());
    }

}