using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PlugService : IPlugServiceUpdates
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPlugCache _plugCache;
    private readonly IPlugHubCallbacks _plugHubCallbacks;

    public PlugService(ICommunicationRepository communicationRepository, IPlugCache plugCache, IPlugHubCallbacks plugHubCallbacks)
    {
        _communicationRepository = communicationRepository;
        _plugCache = plugCache;
        _plugHubCallbacks = plugHubCallbacks;
    }

    public async Task<PlugConfigurationDto> RegisterPlugAsync(string tenantId, OctoObjectId plugRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Plug '{PlugRtId}' registered with connection id '{ConnectionId}'",
            tenantId, plugRtId, connectionId);

        var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);

        if (!plugTenant.PlugsById.TryGetValue(plugRtId, out var plug))
        {
            Logger.Info("[{TenantId}] Plug '{PlugRtId}' not found in cache, fetching from repository",
                tenantId, plugRtId);
            var configuration = await GetPlugConfigurationAsync(tenantId, plugRtId);
            plug = plugTenant.AddPlug(plugRtId, connectionId, configuration);
        }
        else
        {
            Logger.Warn("[{TenantId}] Plug '{PlugRtId}' already registered, updating connection id to '{ConnectionId}'",
                tenantId, plugRtId, connectionId);

            plug.UpdateConnectionId(connectionId);
        }

        await SetPlugInStateAsync(tenantId, plugRtId, RtAdapterStateEnum.Offline);    

        return plug.Configuration;
    }

    public async Task PlugUnRegisteredAsync(string tenantId, OctoObjectId plugRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Plug '{PlugRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, plugRtId, connectionId);

        var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
        plugTenant.RemovePlug(plugRtId);
        await SetPlugInStateAsync(tenantId, plugRtId, RtAdapterStateEnum.Deployed);
    }

    private async Task SetPlugInStateAsync(string tenantId, OctoObjectId plugRtId, RtAdapterStateEnum adapterState)
    {
        Logger.Info("[{TenantId}] Setting state of plug '{PlugRtId}' to '{PlugState}'",
            tenantId, plugRtId, adapterState);
        try
        {
            await _communicationRepository.SetPlugStateAsync(tenantId, plugRtId, adapterState);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting state of plug '{PlugObjectId}' to '{PlugState}'",
                tenantId, plugRtId, adapterState);
            
            throw PlugServiceException.CommonFailedSetPlugState(tenantId, plugRtId, adapterState, e);
        }
    }

    public async Task<PlugConfigurationDto> GetPlugConfigurationAsync(string tenantId, OctoObjectId plugRtId)
    {
        try
        {
            var plugEntity = await _communicationRepository.GetPlugAsync(tenantId, plugRtId);

            var persistentServerSettings =
                plugEntity.Configuration?.Deserialize<PersistentServerSettings>() ?? new PersistentServerSettings();

            var plugGroupConfigurations = await _communicationRepository.GetPlugGroupConfigurationAsync(tenantId, plugRtId);

            var plugConfiguration = new PlugConfigurationDto(
                plugRtId,
                new[]
                {
                    new ServerConfigurationDto(persistentServerSettings.Server, plugGroupConfigurations)
                }
            );
            return plugConfiguration;
        }
        catch (Exception e)
        {
            throw PlugServiceException.CommonFailedCannotLoadPlugConfiguration(tenantId, plugRtId, e);
        }
    }

    public async Task SetPlugOnlineAsync(string tenantId, OctoObjectId plugRtId)
    {
        Logger.Info("[{TenantId}] plug rt id '{PlugRtId}' online",
            tenantId, plugRtId);
        
        var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
        if (plugTenant.PlugsById.TryGetValue(plugRtId, out var plug))
        {
            await SetPlugInStateAsync(tenantId, plug.PlugRtId, RtAdapterStateEnum.Online);
        }
    }

    public async Task SetPlugOfflineAsync(string tenantId, OctoObjectId plugRtId)
    {
        if (_plugCache.TryGetTenant(tenantId, out var plugTenant) && plugTenant != null)
        {
            await _communicationRepository.SetPlugStateAsync(tenantId, plugRtId, RtAdapterStateEnum.Offline);
        }
    }

    public Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reload tenant", tenantId);
        
        // More handling is currently not implemented, because the pool service will react on this
        // and undeploys and deploys the communication adapters currently. 
        
        return Task.CompletedTask;
    }

    public async Task OnHandlePlugMappingUpdateAsync(string tenantId, IUpdateInfo<RtPlugMapping> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId, out var plug))
            {
                var rtPlug = await _communicationRepository.GetPlugByMappingAsync(tenantId, info.Document.RtId);

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId);

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(tenantId, configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }

    public async Task OnHandlePlugGroupUpdateAsync(string tenantId, IUpdateInfo<RtPlugGroup> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId, out var plug))
            {
                var rtPlug = await _communicationRepository.GetPlugByGroupAsync(tenantId, info.Document.RtId);

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId);

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(tenantId, configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }

    public async Task OnHandlePlugUpdateAsync(string tenantId, IUpdateInfo<RtPlug> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId, out var plug))
            {
                var rtPlug = await _communicationRepository.GetPlugAsync(tenantId, info.Document.RtId);

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId);

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(tenantId, configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }
}