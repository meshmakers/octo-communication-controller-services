using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Hubs;

/// <summary>
/// Hub for plugPoolPlug pool operators
/// </summary>
public class PoolHub : Hub, IPoolHub
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPoolService _poolService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="poolService"></param>
    public PoolHub(IPoolService poolService)
    {
        _poolService = poolService;
    }

    /// <summary>
    /// Called when a client connects to the hub
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();

        try
        {
            await _poolService.SetPoolOnlineAsync(tenantId, Context.ConnectionId);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{TenantId}] Failed to set pool online", tenantId);
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

        try
        {
            await _poolService.SetPoolOfflineAsync(tenantId, Context.ConnectionId);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{TenantId}] Failed to set pool offline", tenantId);
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
            var plugPoolRtId = await _poolService.RegisterPlugPoolOperatorAsync(tenantId, plugPoolName, Context.ConnectionId);

            await _poolService.SetPoolOnlineAsync(tenantId, plugPoolRtId);

            return await _poolService.GetCurrentPlugsAsync(tenantId, plugPoolRtId);
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
            await _poolService.UnregisterPlugPoolOperatorAsync(tenantId, plugPoolName);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot unregister plugPoolPlug pool operator");
            throw;
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