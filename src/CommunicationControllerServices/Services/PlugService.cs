using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Plugs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.CkModelEntities;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PlugService : IPlugService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IPlugRepository _plugRepository;
    private readonly IPlugCache _plugCache;
    private readonly IPlugHubCallbacks _plugHubCallbacks;

    public PlugService(IPlugRepository plugRepository, IPlugCache plugCache, IPlugHubCallbacks plugHubCallbacks)
    {
        _plugRepository = plugRepository;
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

        await SetPlugInStateAsync(tenantId, plugRtId, PlugStates.Offline);    

        return plug.Configuration;
    }

    public async Task PlugUnRegisteredAsync(string tenantId, OctoObjectId plugRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Plug '{PlugRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, plugRtId, connectionId);

        var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
        plugTenant.RemovePlug(plugRtId);
        await SetPlugInStateAsync(tenantId, plugRtId, PlugStates.Deployed);
    }

    private async Task SetPlugInStateAsync(string tenantId, OctoObjectId plugRtId, PlugStates plugState)
    {
        Logger.Info("[{TenantId}] Setting state of plug '{PlugObjectId}' to '{PlugState}'",
            tenantId, plugRtId, plugState);
        try
        {
            await _plugRepository.SetPlugStateAsync(tenantId, plugRtId, plugState);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting state of plug '{PlugObjectId}' to '{PlugState}'",
                tenantId, plugRtId, plugState);
            
            throw PlugServiceException.CommonFailedSetPlugState(tenantId, plugRtId, plugState, e);
        }
    }

    public async Task<PlugConfigurationDto> GetPlugConfigurationAsync(string tenantId, OctoObjectId plugRtId)
    {
        try
        {
            var plugEntity = await _plugRepository.GetPlugAsync(tenantId, plugRtId);

            var persistentServerSettings =
                plugEntity.Configuration?.Deserialize<PersistentServerSettings>() ?? new PersistentServerSettings();

            var plugGroupConfigurations = await _plugRepository.GetPlugGroupConfigurationAsync(tenantId, plugRtId);
       
            var plugConfiguration = new PlugConfigurationDto
            {
                PlugRtId = plugRtId,
                ServerConfigurations = new[]
                {
                    new ServerConfigurationDto
                    {
                        Server = persistentServerSettings.Server,
                        Groups = plugGroupConfigurations
                    }
                }
            };
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
            await SetPlugInStateAsync(tenantId, plug.PlugRtId, PlugStates.Online);
        }
    }

    public async Task SetPlugOfflineAsync(string tenantId, OctoObjectId plugRtId)
    {
        if (_plugCache.TryGetTenant(tenantId, out var plugTenant) && plugTenant != null)
        {
            await _plugRepository.SetPlugStateAsync(tenantId, plugRtId, PlugStates.Offline);
        }
    }

    public Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reload tenant", tenantId);
        
        // More handling is currently not implemented, because the pool service will react on this
        // and undeploys and deploys the communication adapters currently. 
        
        return Task.CompletedTask;
    }

    public async Task OnHandlePlugMappingUpdateAsync(string tenantId, UpdateInfo<RtPlugMapping> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId.ToOctoObjectId(), out var plug))
            {
                var rtPlug = await _plugRepository.GetPlugByMappingAsync(tenantId, info.Document.RtId.ToOctoObjectId());

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId.ToOctoObjectId());

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }

    public async Task OnHandlePlugGroupUpdateAsync(string tenantId, UpdateInfo<RtPlugGroup> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId.ToOctoObjectId(), out var plug))
            {
                var rtPlug = await _plugRepository.GetPlugByGroupAsync(tenantId, info.Document.RtId.ToOctoObjectId());

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId.ToOctoObjectId());

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }

    public async Task OnHandlePlugUpdateAsync(string tenantId, UpdateInfo<RtPlug> info)
    {
        if (info.Document != null)
        {
            var plugTenant = _plugCache.AddOrUpdateTenant(tenantId);
            if (plugTenant.PlugsById.TryGetValue(info.Document.RtId.ToOctoObjectId(), out var plug))
            {
                var rtPlug = await _plugRepository.GetPlugAsync(tenantId, info.Document.RtId.ToOctoObjectId());

                var configuration = await GetPlugConfigurationAsync(tenantId, rtPlug.RtId.ToOctoObjectId());

                if (!configuration.Equals(plug.Configuration))
                {
                    plug.UpdateConfiguration(configuration);
                    await _plugHubCallbacks.PlugConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }
}