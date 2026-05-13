using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

internal class PoolHubCallbacks(IHubContext<PoolHub> hubContext, IPoolCache poolCache) : IPoolHubCallbacks
{
    /// <inheritdoc />
    public async Task PreUpdateTenantAsync(string tenantId)
    {
        if (poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            foreach (var pool in poolTenant.PoolsByName.Values)
            {
                if (!string.IsNullOrWhiteSpace(pool.ConnectionId))
                {
                    await hubContext.Clients.Client(pool.ConnectionId)
                        .SendAsync(nameof(IPoolHubCallbacks.PreUpdateTenantAsync), tenantId);
                }
            }
        }
    }
}
