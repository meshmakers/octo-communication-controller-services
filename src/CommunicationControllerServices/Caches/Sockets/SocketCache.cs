using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;

internal class SocketCache : ISocketCachePublish, ISocketCache
{
    private readonly IDistributionEventHubService _distributionEventHubService;
    private readonly ConcurrentDictionary<string, SocketTenant> _tenantDescriptions = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public SocketCache(IDistributionEventHubService distributionEventHubService)
    {
        _distributionEventHubService = distributionEventHubService;
    }
    
    public SocketTenant AddOrUpdateTenant(string tenantId)
    {
        if (!_tenantDescriptions.TryGetValue(tenantId, out var socketTenant))
        {
            var newPlugTenant = new SocketTenant(this, tenantId);
            socketTenant = _tenantDescriptions.AddOrUpdate(tenantId, _ => newPlugTenant,
                (_, _) => newPlugTenant);
            
            PublishConfiguration(tenantId);
        }
        return socketTenant;
    }
    
    public void RemoveTenant(string tenantId)
    {
        _tenantDescriptions.TryRemove(tenantId, out _);
        
        PublishConfiguration(tenantId);
    }

    public bool TryGetTenant(string tenantId, out SocketTenant? socketTenant)
    {
        return _tenantDescriptions.TryGetValue(tenantId, out socketTenant);
    }
    
    public void ReloadConfiguration(ComControllerSocketUpdate configuration)
    {
        Logger.Info("Reloading SocketCache configuration: {Configuration}", configuration.Serialize());
        
        _tenantDescriptions.AddOrUpdate(configuration.TenantId, 
            _ => new SocketTenant(this, configuration.TenantId, configuration.Sockets.ToList()),
            (_, _) => new SocketTenant(this, configuration.TenantId, configuration.Sockets.ToList()));
        _tenantDescriptions.Clear();
    }

    public void PublishConfiguration(string tenantId)
    {
        Logger.Info("Publishing SocketCache configuration '{TenantId}'", tenantId);
        
        if (_tenantDescriptions.TryGetValue(tenantId, out var desc))
        {
            _distributionEventHubService.PublishAsync(new ComControllerSocketUpdate(tenantId, desc.GetSocketDescriptions()));
        }
    }

}