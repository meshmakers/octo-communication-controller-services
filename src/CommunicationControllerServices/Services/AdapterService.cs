using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.ConstructionKit.Generated.System.Communication.v1;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    IAdapterHubCallbacks adapterHubCallbacks)
    : IAdapterService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, OctoObjectId adapterRtId,
        string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' registered with connection id '{ConnectionId}'",
            tenantId, adapterRtId, connectionId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (!adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' not found in cache, fetching from repository",
                    tenantId, adapterRtId);
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtId);
                adapter = adapterTenant.AddAdapter(adapterRtId, connectionId, configuration);
            }

            await SetAdapterOnlineAsync(tenantId, adapterRtId, connectionId);

            return adapter.Configuration;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UnregisterAsync(string tenantId, OctoObjectId adapterRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' unregistered with connection id '{ConnectionId}'",
            tenantId, adapterRtId, connectionId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            adapterTenant.RemoveAdapter(adapterRtId);
            await SetAdapterCommunicationStateAsync(tenantId, adapterRtId, RtCommunicationStateEnum.Unregistered);
            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    // private async Task SetAdapterDeploymentStateAsync(string tenantId, OctoObjectId adapterRtId, RtDeploymentStateEnum deploymentState)
    // {
    //     Logger.Info("[{TenantId}] Setting deployment state of adapter '{AdapterRtId}' to '{DeploymentState}'",
    //         tenantId, adapterRtId, deploymentState);
    //     try
    //     {
    //         await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtId, deploymentState);
    //     }
    //     catch (Exception e)
    //     {
    //         Logger.Error(e, "[{TenantId}] Error setting deployment state of {{ '{AdapterRtId}' to '{DeploymentState}'",
    //             tenantId, adapterRtId, deploymentState);
    //         
    //         throw AdapterServiceException.CommonFailedSetAdapterDeploymentState(tenantId, adapterRtId, deploymentState, e);
    //     }
    // }

    public async Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId, OctoObjectId adapterRtId)
    {
        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw AdapterServiceException.TenantNotEnabled(tenantId);
            }
            
            var adapter = await communicationRepository.GetAdapterAsync(tenantId, adapterRtId);

            var dataPipelines = await communicationRepository.GetDataPipelinesAsync(tenantId, adapterRtId);

            var dataPipelineConfigurations = dataPipelines
                .Where(dp => !string.IsNullOrWhiteSpace(dp.AdapterPipelineConfiguration)).Select(dataPipeline =>
                {
                    var dataPipelineConfiguration = new DataPipelineConfigurationDto(
                        dataPipeline.Name,
                        dataPipeline.RtId,
                        dataPipeline.AdapterPipelineConfiguration!
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

    public async Task SetAdapterOnlineAsync(string tenantId, OctoObjectId adapterRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' online",
            tenantId, adapterRtId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
            {
                adapterTenant.UpdateConnectionId(adapterRtId, connectionId);
                await SetAdapterCommunicationStateAsync(tenantId, adapter.AdapterRtId, RtCommunicationStateEnum.Online);
            }
            return;
        }
        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task SetAdapterOfflineAsync(string tenantId, OctoObjectId adapterRtId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline",
            tenantId, adapterRtId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
            {
                adapterTenant.RemoveConnectionId(adapter.AdapterRtId);
                await SetAdapterCommunicationStateAsync(tenantId, adapterRtId, RtCommunicationStateEnum.Offline);
            }
            return;
        }
        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UpdateAdapterConfigurationAsync(string tenantId, OctoObjectId adapterRtId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' update configuration",
            tenantId, adapterRtId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtId, out var adapter))
            {
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtId);

                if (!configuration.Equals(adapter.Configuration))
                {
                    adapter.UpdateConfiguration(tenantId, configuration);
                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                }
            }
        }
        else
        {
            throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtId);
        }
    }

    public async Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reloading tenant", tenantId);

        var adapters = await communicationRepository.GetAdaptersAsync(tenantId);
        foreach (var adapter in adapters)
        {
            if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
            {
                if (adapterTenant.AdapterById.ContainsKey(adapter.RtId))
                {
                    await SetAdapterOfflineAsync(tenantId, adapter.RtId);
                    continue;
                }
            }
            else
            {
                adapterCache.AddOrUpdateTenant(tenantId);
            }

            await SetAdapterCommunicationStateAsync(tenantId, adapter.RtId, RtCommunicationStateEnum.Unregistered);
        }
    }

    public Task UnloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Unload tenant", tenantId);

        adapterCache.RemoveTenant(tenantId);

        return Task.CompletedTask;
    }

    private async Task SetAdapterCommunicationStateAsync(string tenantId, OctoObjectId adapterRtId,
        RtCommunicationStateEnum communicationState)
    {
        Logger.Info("[{TenantId}] Setting communicaton state of adapter '{AdapterRtId}' to '{CommunicationState}'",
            tenantId, adapterRtId, communicationState);
        try
        {
            await communicationRepository.SetAdapterCommunicationStateAsync(tenantId, adapterRtId, communicationState);
        }
        catch (Exception e)
        {
            Logger.Error(e,
                "[{TenantId}] Error setting communicaton state of adapter '{AdapterRtId}' to '{CommunicationState}'",
                tenantId, adapterRtId, communicationState);

            throw AdapterServiceException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtId,
                communicationState, e);
        }
    }
}