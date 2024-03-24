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
    /// Deploys an adapter at a pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="poolCommunicationAdapter"></param>
    public async Task DeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto poolCommunicationAdapter)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsById.TryGetValue(poolCommunicationAdapter.PoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.DeployCommunicationAdapterAsync), tenantId, poolCommunicationAdapter);
            }
        }
    }

    /// <summary>
    /// Removes an adapter from a pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="poolCommunicationAdapter"></param>
    public async Task UndeployCommunicationAdapterAsync(string tenantId, PoolCommunicationAdapterDto poolCommunicationAdapter)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsById.TryGetValue(poolCommunicationAdapter.PoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await _hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.UndeployCommunicationAdapterAsync), tenantId, poolCommunicationAdapter);
            }
        }
    }
}