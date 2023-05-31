using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

/// <summary>
/// Hub for plugPoolPlug pool operators
/// </summary>
public class PlugPoolHub : Hub, IPlugPoolHub, IPlugPoolHubCallbacks
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPlugHubContext _plugHubContext;
    private readonly IPlugPoolService _plugPoolService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="plugPoolService"></param>
    /// <param name="plugHubContext"></param>
    public PlugPoolHub(IPlugPoolService plugPoolService, IPlugHubContext plugHubContext)
    {
        _plugPoolService = plugPoolService;
        _plugHubContext = plugHubContext;

        plugPoolService.RegisterHub(DeployPlugAsync, UndeployPlugAsync);
    }

    /// <summary>
    /// Called when a client connects to the hub
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        
        var plugHubTenant = _plugHubContext.TryGetTenant(tenantId);
        if (plugHubTenant != null)
        {
            plugHubTenant.PoolsByConnectionId.TryGetValue(Context.ConnectionId, out var poolDescription);
            if (poolDescription != null)
            {
                await _plugPoolService.SetPoolOnlineAsync(tenantId, poolDescription.PlugPoolRtId);
            }
        }
        
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Called when a client disconnects from the hub
    /// </summary>
    /// <param name="exception"></param>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        
        var plugHubTenant = _plugHubContext.TryGetTenant(tenantId);
        if (plugHubTenant != null)
        {
            plugHubTenant.PoolsByConnectionId.TryGetValue(Context.ConnectionId, out var poolDescription);
            if (poolDescription != null)
            {
                await _plugPoolService.SetPoolOfflineAsync(tenantId, poolDescription.PlugPoolRtId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Registers a plugPoolPlug pool operator
    /// </summary>
    /// <param name="plugPoolName">Name of plugPoolPlug pool</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlugPoolServiceException"></exception>
    public async Task<PlugPoolConfigurationDto> RegisterPlugPoolOperatorAsync(string plugPoolName)
    {
        var tenantId = GetTenantId();

        try
        {
            var plugPoolRtId = await _plugPoolService.RegisterPlugPoolOperatorAsync(tenantId, plugPoolName);

            var plugHubTenant = _plugHubContext.AddOrUpdateTenant(tenantId);
            var plugHubPool = plugHubTenant.AddPool(plugPoolName, plugPoolRtId, Context.ConnectionId);
            
            await _plugPoolService.SetPoolOnlineAsync(tenantId, plugHubPool.PlugPoolRtId);

            return await _plugPoolService.GetCurrentPlugsAsync(tenantId, plugPoolRtId);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register plugPoolPlug pool operator");
            throw;
        }
    }

    /// <summary>
    /// Unregisters a plugPoolPlug pool operator
    /// </summary>
    /// <param name="plugPoolName">Name of plugPoolPlug pool</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlugPoolServiceException"></exception>
    public async Task UnregisterPlugPoolOperatorAsync(string plugPoolName)
    {
        var tenantId = GetTenantId();

        try
        {
            var plugPoolRtId = await _plugPoolService.UnregisterPlugPoolOperatorAsync(tenantId, plugPoolName);

            var plugHubTenant = _plugHubContext.TryGetTenant(tenantId);
            plugHubTenant?.RemovePool(plugPoolRtId);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot unregister plugPoolPlug pool operator");
            throw;
        }
    }

    /// <summary>
    /// Deploys a Plug at a Plug Pool
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="plugPoolPlug"></param>
    public async Task DeployPlugAsync(string tenantId, PlugPoolPlugDto plugPoolPlug)
    {
        var plugHubTenant = _plugHubContext.TryGetTenant(tenantId);
        if (plugHubTenant != null)
        {
            plugHubTenant.PoolsById.TryGetValue(plugPoolPlug.PlugPoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPlugPoolHubCallbacks.DeployPlugAsync), plugPoolPlug);
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
        var plugHubTenant = _plugHubContext.TryGetTenant(tenantId);
        if (plugHubTenant != null)
        {
            plugHubTenant.PoolsById.TryGetValue(plugPoolPlug.PlugPoolRtId, out var poolDescription);
            if (poolDescription != null)
            {
                await Clients.Client(poolDescription.ConnectionId)
                    .SendAsync(nameof(IPlugPoolHubCallbacks.UndeployPlugAsync), plugPoolPlug);
            }
        }
    }

    private string GetTenantId()
    {
        var tenantId = Context.GetHttpContext()?.GetTenantId();
        if (tenantId == null)
        {
            Context.Abort();
            throw new InvalidOperationException("TenantId is null");
        }

        return tenantId;
    }
}