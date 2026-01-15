using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for pool operators
/// </summary>
public class PoolHub : Hub, IPoolHub
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IPoolService _poolService;
    private readonly ICommunicationEventService _eventService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="poolService">The responsible pool service</param>
    /// <param name="eventService">Service for storing system events</param>
    public PoolHub(IPoolService poolService, ICommunicationEventService eventService)
    {
        _poolService = poolService;
        _eventService = eventService;
    }

    /// <inheritdoc />
    public override async Task OnConnectedAsync()
    {
        var tenantId = GetTenantId();
        var poolName = GetPoolName();

        try
        {
            Logger.Info("[{TenantId}] Pool {PoolName} with connection id '{ConnectionId}' connected", tenantId, poolName, Context.ConnectionId);
            await _poolService.SetCommunicationStateOnlineAsync(tenantId, poolName, Context.ConnectionId);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{TenantId}] Failed to set pool online", tenantId);
        }

        await base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var tenantId = GetTenantId();
        var poolName = GetPoolName();

        try
        {
            Logger.Info("[{TenantId}] Pool {PoolName} with connection id '{ConnectionId}' disconnected", tenantId, poolName, Context.ConnectionId);

            await _poolService.SetCommunicationStateOfflineAsync(tenantId, Context.ConnectionId);
        }
        catch (Exception e)
        {
            Logger.Warn(e, "[{TenantId}] Failed to set pool offline", tenantId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public async Task<PoolConfigurationDto> RegisterPoolOperatorAsync(string poolName)
    {
        var tenantId = GetTenantId();

        try
        {
            var poolRtId = await _poolService.RegisterPoolOperatorAsync(tenantId, poolName, Context.ConnectionId);

            await _poolService.SetCommunicationStateOnlineAsync(tenantId, poolRtId);

            var configuration = await _poolService.GetPoolConfigurationAsync(tenantId, poolRtId);
            
            await _poolService.SetAdapterDeploymentStateAsync(tenantId, poolName,
                configuration.CommunicationAdapterList.Select(x=>x.AdapterRtEntityId).ToList(), 
                RtDeploymentStateEnum.Pending);
            
            return configuration;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot register pool operator");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to register pool operator for pool '{poolName}': {e.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnregisterPoolOperatorAsync(string poolName)
    {
        var tenantId = GetTenantId();

        try
        {
            await _poolService.UnregisterPoolOperatorAsync(tenantId, poolName);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot unregister pool operator");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to unregister pool operator for pool '{poolName}': {e.Message}");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UpdateAdapterDeploymentStateAsync(string poolName, RtEntityId adapterRtEntityId, bool deployed)
    {
        var tenantId = GetTenantId();

        try
        {
            await _poolService.SetAdapterDeploymentStateAsync(tenantId, poolName, adapterRtEntityId,
                RtDeploymentStateEnum.Deployed);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Cannot update adapter deployment state");
            await _eventService.StoreErrorEventAsync(tenantId,
                $"Failed to update adapter deployment state for adapter '{adapterRtEntityId}' in pool '{poolName}': {e.Message}");
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
    
    private string GetPoolName()
    {
        var poolName = Context.GetHttpContext()?.GetPoolName();
        if (poolName == null)
        {
            Context.Abort();
            throw new InvalidOperationException("PoolName is null");
        }

        return poolName;
    }
}