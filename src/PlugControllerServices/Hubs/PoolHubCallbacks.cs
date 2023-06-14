using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

internal class PoolHubCallbacks : IPoolHubCallbacks
{
    private readonly IHubContext<PoolHub> _hubContext;
    private readonly IPoolCache _poolCache;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public PoolHubCallbacks(IHubContext<PoolHub> hubContext, IPoolCache poolCache)
    {
        _hubContext = hubContext;
        _poolCache = poolCache;
    }


    /// <summary>
    /// Deploys a Plug at a Plug Pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="plugPoolPlug"></param>
    public async Task DeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PoolsById.TryGetValue(plugPoolPlug.PlugPoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.DeployPlugAsync), tenantId, plugPoolPlug);
            }
        }
    }

    /// <summary>
    /// Removes a Plug from a Plug Pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="plugPoolPlug"></param>
    public async Task UndeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PoolsById.TryGetValue(plugPoolPlug.PlugPoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.UndeployPlugAsync), tenantId, plugPoolPlug);
            }
        }
    }
}