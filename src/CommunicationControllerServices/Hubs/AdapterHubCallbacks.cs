using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Implementation of <see cref="IAdapterHubCallbacks"/>
/// </summary>
internal class AdapterHubCallbacks : IAdapterHubCallbacks
{
    private readonly IHubContext<AdapterHub> _adapterContext;
    private readonly IAdapterCache _adapterCache;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    
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
        if (_adapterCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.AdapterById.TryGetValue(adapterConfiguration.AdapterRtId, out var adapter);
            if (adapter != null)
            {
                await _adapterContext.Clients.Client(adapter.ConnectionId)
                    .SendAsync(nameof(IAdapterHubCallbacks.AdapterConfigurationUpdatedAsync), tenantId, adapterConfiguration);
            }
        }
    }
}