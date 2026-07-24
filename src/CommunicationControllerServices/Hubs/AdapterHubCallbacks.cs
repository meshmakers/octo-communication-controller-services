using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Implementation of <see cref="IAdapterHubCallbacks"/>
/// </summary>
internal class AdapterHubCallbacks : IAdapterHubCallbacks
{
    private readonly IHubContext<AdapterHub> _adapterContext;
    private readonly IAdapterCache _adapterCache;
    
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="adapterContext"></param>
    /// <param name="adapterCache"></param>
    public AdapterHubCallbacks(IHubContext<AdapterHub> adapterContext, IAdapterCache adapterCache)
    {
        _adapterContext = adapterContext;
        _adapterCache = adapterCache;
    }

    /// <inheritdoc />
    public async Task AdapterConfigurationUpdatedAsync(string tenantId, AdapterConfigurationDto adapterConfiguration)
    {
        if (_adapterCache.TryGetTenant(tenantId, out var poolTenant))
        {
            if (poolTenant.AdapterById.TryGetValue(adapterConfiguration.AdapterRtEntityId, out var adapter)
                && !string.IsNullOrWhiteSpace(adapter.ConnectionId))
            {
                
                await _adapterContext.Clients.Client(adapter.ConnectionId)
                    .SendAsync(nameof(IAdapterHubCallbacks.AdapterConfigurationUpdatedAsync),
                        tenantId, adapterConfiguration, CancellationToken.None);
                return;
            }

            throw AdapterHubCallbackException.AdapterNotOnline(tenantId, adapterConfiguration.AdapterRtEntityId);

        }
        throw AdapterHubCallbackException.TenantNotFound(tenantId);
    }

    /// <inheritdoc />
    public async Task PreUpdateTenantAsync(string tenantId)
    {
        if (_adapterCache.TryGetTenant(tenantId, out var poolTenant))
        {
            foreach (var adapter in poolTenant.AdapterById.Values)
            {
                if (!string.IsNullOrWhiteSpace(adapter.ConnectionId))
                {
                    await _adapterContext.Clients.Client(adapter.ConnectionId)
                        .SendAsync(nameof(IAdapterHubCallbacks.PreUpdateTenantAsync), tenantId);
                }
            }
        }
    }

    /// <inheritdoc />
    public async Task CkModelChangedAsync(string tenantId)
    {
        // Deliberately broadcast to every adapter connection instead of routing through the
        // adapter cache: the cache is wiped during a tenant pre-update, and an adapter whose
        // re-registration failed stays connected but uncached — exactly the case where its
        // stale CK model cache must be flushed (AB#4456). Adapters filter by tenant themselves.
        await _adapterContext.Clients.All
            .SendAsync(nameof(IAdapterHubCallbacks.CkModelChangedAsync), tenantId);
    }
}