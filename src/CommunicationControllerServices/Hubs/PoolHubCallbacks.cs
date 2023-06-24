using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

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
    /// Deploys a Plug at a pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="poolPlug"></param>
    public async Task DeployPlugAsync(string tenantId, PoolPlugDto poolPlug)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PoolsById.TryGetValue(poolPlug.PoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.DeployPlugAsync), tenantId, poolPlug);
            }
        }
    }

    /// <summary>
    /// Removes a Plug from a pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="poolPlug"></param>
    public async Task UndeployPlugAsync(string tenantId, PoolPlugDto poolPlug)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PoolsById.TryGetValue(poolPlug.PoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.UndeployPlugAsync), tenantId, poolPlug);
            }
        }
    }
}