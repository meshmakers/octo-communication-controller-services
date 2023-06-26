using System.Collections.Concurrent;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets.Descriptions;
using Meshmakers.Octo.Common.DistributedCache;
using Meshmakers.Octo.Common.Shared;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal class SocketCache : ISocketCachePublish, ISocketCache
{
    private readonly IDistributedWithPubSubCache _distributedWithPubSubCache;
    private readonly ConcurrentDictionary<string, SocketTenant> _tenantDescriptions = new();
    private IChannel<IEnumerable<SocketTenantDescription>>? _channel;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SocketCache(IDistributedWithPubSubCache distributedWithPubSubCache)
    {
        _distributedWithPubSubCache = distributedWithPubSubCache;
    }
    
    public async Task InitializeAsync()
    {
        Logger.Debug("Initializing SocketCache");
        
        if (_channel == null)
        {
            _channel = SubscribeToSocketHubConfigurationUpdates();
            var configuration = await _distributedWithPubSubCache.GetLastMessageAsync<IEnumerable<SocketTenantDescription>>(CacheCommon.KeyCommunicationControllerSocketUpdate);
            if (configuration != null)
            {
                ReloadConfiguration(configuration);
            }
        }
    }

    public SocketTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var socketTenant))
        {
            var newPlugTenant = new SocketTenant(this, tenantId);
            socketTenant = _tenantDescriptions.AddOrUpdate(tenantId, _ => newPlugTenant,
                (_, _) => newPlugTenant);
            
            PublishConfiguration();
        }
        return socketTenant;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration();
    }

    public bool TryGetTenant(string tenantId, out SocketTenant? socketTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out socketTenant);
    }
    
    private IChannel<IEnumerable<SocketTenantDescription>> SubscribeToSocketHubConfigurationUpdates()
    {
        var channel = _distributedWithPubSubCache.Subscribe<IEnumerable<SocketTenantDescription>>(CacheCommon.KeyCommunicationControllerSocketUpdate);
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

    private void ReloadConfiguration(IEnumerable<SocketTenantDescription> configuration)
    {
        Logger.Info("Reloading SocketCache configuration: {Configuration}", configuration.Serialize());
        
        _tenantDescriptions.Clear();
        foreach (var tenantDescription in configuration)
        {
            var plugHubTenant = new SocketTenant(this, tenantDescription.TenantId, tenantDescription.Sockets.ToList());
            _tenantDescriptions.AddOrUpdate(tenantDescription.TenantId, _ => plugHubTenant, (_, _) => plugHubTenant);
        }
    }

    public void PublishConfiguration()
    {
        Logger.Info("Publishing PlugCache configuration");

        var tenantDescriptions= 
            _tenantDescriptions.Select(x => x.Value.GetTenantDescription()).ToArray();
        
        _distributedWithPubSubCache.PublishAsync(CacheCommon.KeyCommunicationControllerSocketUpdate, tenantDescriptions);
    }

}