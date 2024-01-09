using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class SocketService : ISocketServiceUpdates
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ICommunicationRepository _communicationRepository;
    private readonly ISocketCache _socketCache;
    private readonly ISocketHubCallbacks _socketHubCallbacks;

    public SocketService(ICommunicationRepository communicationRepository, ISocketCache socketCache, ISocketHubCallbacks socketHubCallbacks)
    {
        _communicationRepository = communicationRepository;
        _socketCache = socketCache;
        _socketHubCallbacks = socketHubCallbacks;
    }
    
    public async Task<SocketConfigurationDto> RegisterSocketAsync(string tenantId, OctoObjectId socketRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Socket '{SocketRtId}' registered with connection id '{ConnectionId}'",
            tenantId, socketRtId, connectionId);

        var plugTenant = _socketCache.AddOrUpdateTenant(tenantId);

        if (!plugTenant.SocketsById.TryGetValue(socketRtId, out var plug))
        {
            Logger.Info("[{TenantId}] Socket '{SocketRtId}' not found in cache, fetching from repository",
                tenantId, socketRtId);
            var configuration = await GetSocketConfigurationAsync(tenantId, socketRtId);
            plug = plugTenant.AddSocket(socketRtId, connectionId, configuration);
        }
        else
        {
            Logger.Warn("[{TenantId}] Socket '{SocketRtId}' already registered, updating connection id to '{ConnectionId}'",
                tenantId, socketRtId, connectionId);

            plug.UpdateConnectionId(connectionId);
        }

        await SetSocketStateAsync(tenantId, socketRtId, RtAdapterStateEnum.Offline);    

        return plug.Configuration;
    }
    
    public async Task SocketUnRegisteredAsync(string tenantId, OctoObjectId socketRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Socket '{SocketRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, socketRtId, connectionId);

        var plugTenant = _socketCache.AddOrUpdateTenant(tenantId);
        plugTenant.RemoveSocket(socketRtId);
        await SetSocketStateAsync(tenantId, socketRtId, RtAdapterStateEnum.Deployed);
    }

    private async Task SetSocketStateAsync(string tenantId, OctoObjectId socketRtId, RtAdapterStateEnum adapterState)
    {
        Logger.Info("[{TenantId}] Setting state of socket '{SocketRtId}' to '{AdapterState}'",
            tenantId, socketRtId, adapterState);
        try
        {
            await _communicationRepository.SetSocketStateAsync(tenantId, socketRtId, adapterState);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting state of socket '{SocketRtId}' to '{AdapterState}'",
                tenantId, socketRtId, adapterState);
            
            throw SocketServiceException.CommonFailedSetSocketState(tenantId, socketRtId, adapterState, e);
        }
    }

    public Task<SocketConfigurationDto> GetSocketConfigurationAsync(string tenantId, OctoObjectId socketRtId)
    {
        try
        {
            // TODO: Get configuration from repository
            //var socket = await _communicationRepository.GetSocketAsync(tenantId, socketRtId);

            var plugConfiguration = new SocketConfigurationDto(socketRtId);
            return Task.FromResult(plugConfiguration);
        }
        catch (Exception e)
        {
            throw SocketServiceException.CommonFailedCannotLoadSocketConfiguration(tenantId, socketRtId, e);
        }
    }

    public async Task SetSocketOnlineAsync(string tenantId, OctoObjectId socketRtId)
    {
        Logger.Info("[{TenantId}] Socket '{SocketRti}' online",
            tenantId, socketRtId);
        
        var plugTenant = _socketCache.AddOrUpdateTenant(tenantId);
        if (plugTenant.SocketsById.TryGetValue(socketRtId, out var socket))
        {
            await SetSocketStateAsync(tenantId, socket.SocketRtId, RtAdapterStateEnum.Online);
        }
    }

    public async Task SetSocketOfflineAsync(string tenantId, OctoObjectId socketRtId)
    {
        if (_socketCache.TryGetTenant(tenantId, out var socketTenant) && socketTenant != null)
        {
            await _communicationRepository.SetSocketStateAsync(tenantId, socketRtId, RtAdapterStateEnum.Offline);
        }
    }
    
    public Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reload tenant", tenantId);
        
        // More handling is currently not implemented, because the pool service will react on this
        // and undeploys and deploys the communication adapters currently. 
        
        return Task.CompletedTask;
    }
}