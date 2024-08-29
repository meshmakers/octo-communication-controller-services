using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

internal class PoolHubCallbacks(IHubContext<PoolHub> hubContext, IPoolCache poolCache) : IPoolHubCallbacks
{
    /// <inheritdoc />
    public async Task UpdatePoolConfigurationAsync(string tenantId, string poolName,
        PoolConfigurationDto poolConfigurationDto)
    {
        if (poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
            if (poolDescription != null)
            {
                await hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.UpdatePoolConfigurationAsync), tenantId, poolName,
                        poolConfigurationDto);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeployCommunicationAdapterAsync(string tenantId,
        PoolCommunicationAdapterDto poolCommunicationAdapter)
    {
        if (poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsByName.TryGetValue(poolCommunicationAdapter.PoolName, out var poolDescription);
            if (poolDescription != null)
            {
                await hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.DeployCommunicationAdapterAsync), tenantId,
                        poolCommunicationAdapter);
            }
        }
    }

    /// <inheritdoc />
    public async Task UndeployCommunicationAdapterAsync(string tenantId,
        PoolCommunicationAdapterDto poolCommunicationAdapter)
    {
        if (poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsByName.TryGetValue(poolCommunicationAdapter.PoolName, out var poolDescription);
            if (poolDescription != null)
            {
                await hubContext.Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.UndeployCommunicationAdapterAsync), tenantId,
                        poolCommunicationAdapter);
            }
        }
    }

    /// <inheritdoc />
    public async Task PreReloadTenantAsync(string tenantId)
    {
        if (poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            foreach (var pool in poolTenant.PoolsByName.Values)
            {
                await hubContext.Clients.Client(pool.ConnectionId)
                    .SendAsync(nameof(IPoolHubCallbacks.PreReloadTenantAsync), tenantId);
            }
        }
    }
}