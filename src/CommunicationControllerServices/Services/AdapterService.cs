using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterService : IAdapterServiceUpdates
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly ICommunicationRepository _communicationRepository;
    private readonly IAdapterCache _adapterCache;
    private readonly IAdapterHubCallbacks _adapterHubCallbacks;

    public AdapterService(ICommunicationRepository communicationRepository, IAdapterCache adapterCache, IAdapterHubCallbacks adapterHubCallbacks)
    {
        _communicationRepository = communicationRepository;
        _adapterCache = adapterCache;
        _adapterHubCallbacks = adapterHubCallbacks;
    }

    public async Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, OctoObjectId adapterRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' registered with connection id '{ConnectionId}'",
            tenantId, adapterRtId, connectionId);

        var adapterTenant = _adapterCache.AddOrUpdateTenant(tenantId);

        if (!adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
        {
            Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' not found in cache, fetching from repository",
                tenantId, adapterRtId);
            var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtId);
            adapter = adapterTenant.AddAdapter(adapterRtId, connectionId, configuration);
        }
        else
        {
            Logger.Warn("[{TenantId}] Adapter '{AdapterRtId}' already registered, updating connection id to '{ConnectionId}'",
                tenantId, adapterRtId, connectionId);

            adapter.UpdateConnectionId(connectionId);
        }

        await SetAdapterDeploymentStateAsync(tenantId, adapterRtId, RtDeploymentStateEnum.Deployed);    
        await SetAdapterCommunicationStateAsync(tenantId, adapterRtId, RtCommunicationStateEnum.Offline);    

        return adapter.Configuration;
    }

    public async Task AdapterUnRegisteredAsync(string tenantId, OctoObjectId adapterRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, adapterRtId, connectionId);

        var adapterTenant = _adapterCache.AddOrUpdateTenant(tenantId);
        adapterTenant.RemoveAdapter(adapterRtId);
        await SetAdapterDeploymentStateAsync(tenantId, adapterRtId, RtDeploymentStateEnum.Created);
    }

    private async Task SetAdapterDeploymentStateAsync(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum deploymentState)
    {
        Logger.Info("[{TenantId}] Setting deployment state of adapter '{AdapterRtId}' to '{DeploymentState}'",
            tenantId, adapterRtId, deploymentState);
        try
        {
            await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtId, deploymentState);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting deployment state of {{ '{AdapterRtId}' to '{DeploymentState}'",
                tenantId, adapterRtId, deploymentState);
            
            throw AdapterServiceException.CommonFailedSetAdapterDeploymentState(tenantId, adapterRtId, deploymentState, e);
        }
    }
    
    private async Task SetAdapterCommunicationStateAsync(string tenantId, OctoObjectId adapterRtId, RtCommunicationStateEnum communicationState)
    {
        Logger.Info("[{TenantId}] Setting communicaton state of adapter '{AdapterRtId}' to '{CommunicationState}'",
            tenantId, adapterRtId, communicationState);
        try
        {
            await _communicationRepository.SetAdapterCommunicationStateAsync(tenantId, adapterRtId, communicationState);
        }
        catch (Exception e)
        {
            Logger.Error(e, "[{TenantId}] Error setting communicaton state of adapter '{AdapterRtId}' to '{CommunicationState}'",
                tenantId, adapterRtId, communicationState);
            
            throw AdapterServiceException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtId, communicationState, e);
        }
    }

    public async Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId, OctoObjectId adapterRtId)
    {
        try
        {
            var adapter = await _communicationRepository.GetAdapterAsync(tenantId, adapterRtId);

            var dataPipelines = await _communicationRepository.GetDataPipelinesAsync(tenantId, adapterRtId);
            
            var dataPipelineConfigurations = dataPipelines.Select(dataPipeline =>
            {
                var dataPipelineConfiguration = new DataPipelineConfigurationDto(
                    dataPipeline.Name,
                    dataPipeline.RtId,
                    dataPipeline.AdapterPipelineConfiguration
                );
                return dataPipelineConfiguration;
            }).ToArray();

            var adapterConfigurationDto = new AdapterConfigurationDto(
                adapterRtId,
                adapter.Configuration,
                dataPipelineConfigurations.ToList()
            );
            return adapterConfigurationDto;
        }
        catch (Exception e)
        {
            throw AdapterServiceException.CommonFailedCannotLoadAdapterConfiguration(tenantId, adapterRtId, e);
        }
    }

    public async Task SetAdapterOnlineAsync(string tenantId, OctoObjectId adapterRtId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' online",
            tenantId, adapterRtId);
        
        var adapterTenant = _adapterCache.AddOrUpdateTenant(tenantId);
        if (adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
        {
            await SetAdapterCommunicationStateAsync(tenantId, adapter.AdapterRtId, RtCommunicationStateEnum.Online);
        }
    }

    public async Task SetAdapterOfflineAsync(string tenantId, OctoObjectId adapterRtId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline",
            tenantId, adapterRtId);
        
        if (_adapterCache.TryGetTenant(tenantId, out var adapterTenant) && adapterTenant != null)
        {
            await SetAdapterCommunicationStateAsync(tenantId, adapterRtId, RtCommunicationStateEnum.Online);
        }
    }

    public Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reload tenant", tenantId);
        
        // More handling is currently not implemented, because the pool service will react on this
        // and undeploys and deploys the communication adapters currently. 
        
        return Task.CompletedTask;
    }

    public async Task OnHandleDataPipelineUpdateAsync(string tenantId, IUpdateInfo<RtDataPipeline> info)
    {
        if (info.Document != null)
        {
            var adapterTenant = _adapterCache.AddOrUpdateTenant(tenantId);
            if (adapterTenant.AdapterById.TryGetValue(info.Document.RtId, out var adapter))
            {
                var rtAdapter = await _communicationRepository.GetAdapterByDataPipelineAsync(tenantId, info.Document.RtId);

                var configuration = await GetAdapterConfigurationAsync(tenantId, rtAdapter.RtId);

                if (!configuration.Equals(adapter.Configuration))
                {
                    adapter.UpdateConfiguration(tenantId, configuration);
                    await _adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }

    public async Task OnHandleAdapterUpdateAsync(string tenantId, IUpdateInfo<RtCommunicationAdapter> info)
    {
        if (info.Document != null)
        {
            var adapterTenant = _adapterCache.AddOrUpdateTenant(tenantId);
            if (adapterTenant.AdapterById.TryGetValue(info.Document.RtId, out var adapter))
            {
                var rtAdapter = await _communicationRepository.GetAdapterAsync(tenantId, info.Document.RtId);

                var configuration = await GetAdapterConfigurationAsync(tenantId, rtAdapter.RtId);

                if (!configuration.Equals(adapter.Configuration))
                {
                    adapter.UpdateConfiguration(tenantId, configuration);
                    await _adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
    }
}