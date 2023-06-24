using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Implementation of <see cref="IPlugHubCallbacks"/>
/// </summary>
internal class PlugHubCallbacks : IPlugHubCallbacks
{
    private readonly IHubContext<PlugHub> _plugContext;
    private readonly IPlugCache _plugCache;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    
    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugContext"></param>
    /// <param name="plugCache"></param>
    public PlugHubCallbacks(IHubContext<PlugHub> plugContext, IPlugCache plugCache)
    {
        _plugContext = plugContext;
        _plugCache = plugCache;
    }

    /// <inheritdoc />
    public async Task PlugConfigurationUpdatedAsync(string tenantId, PlugConfigurationDto plugConfiguration)
    {
        if (_plugCache.TryGetTenant(tenantId, out var poolTenant) && poolTenant != null)
        {
            poolTenant.PlugsById.TryGetValue(plugConfiguration.PlugRtId, out var plug);
            if (plug != null)
            {
                await _plugContext.Clients.Client(plug.ConnectionId)
                    .SendAsync(nameof(IPlugHubCallbacks.PlugConfigurationUpdatedAsync), tenantId, plugConfiguration);
            }
        }
    }
}