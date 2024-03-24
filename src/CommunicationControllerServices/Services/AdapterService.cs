using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    IAdapterHubCallbacks adapterHubCallbacks)
    : IAdapterService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public async Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' registered with connection id '{ConnectionId}'",
            tenantId, adapterRtEntityId, connectionId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (!adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' not found in cache, fetching from repository",
                    tenantId, adapterRtEntityId);
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId);
                adapter = adapterTenant.AddAdapter(adapterRtEntityId, connectionId, configuration);
            }

            await SetAdapterOnlineAsync(tenantId, adapterRtEntityId, connectionId);

            return adapter.Configuration;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UnregisterAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId)
    {
        Logger.Info("[{TenantId}] Adapter '{AdapterRtEntityId}' unregistered with connection id '{ConnectionId}'",
            tenantId, adapterRtEntityId, connectionId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            adapterTenant.RemoveAdapter(adapterRtEntityId);
            await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, RtCommunicationStateEnum.Unregistered);
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

    public async Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw AdapterServiceException.TenantNotEnabled(tenantId);
            }

            var adapter = await communicationRepository.GetAdapterAsync(tenantId, adapterRtEntityId);

            var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, adapterRtEntityId);

            var dataPipelineConfigurations = pipelines
                .Where(dp => !string.IsNullOrWhiteSpace(dp.PipelineDefinition)).Select(pipeline =>
                {
                    var dataPipelineConfiguration = new PipelineConfigurationDto(
                        pipeline.RtId,
                        false,
                        pipeline.PipelineDefinition
                    );
                    return dataPipelineConfiguration;
                }).ToArray();

            var adapterConfigurationDto = new AdapterConfigurationDto(
                adapterRtEntityId,
                adapter.Configuration,
                dataPipelineConfigurations.ToList()
            );
            return adapterConfigurationDto;
        }
        catch (Exception e)
        {
            throw AdapterServiceException.CommonFailedCannotLoadAdapterConfiguration(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task SetAdapterOnlineAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' online",
            tenantId, adapterRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                adapterTenant.UpdateConnectionId(adapterRtEntityId, connectionId);
                await SetAdapterCommunicationStateAsync(tenantId, adapter.AdapterRtEntityId, RtCommunicationStateEnum.Online);
            }

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task SetAdapterOfflineAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline",
            tenantId, adapterRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                adapterTenant.RemoveConnectionId(adapter.AdapterRtEntityId);
                await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, RtCommunicationStateEnum.Offline);
            }

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task DeployAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] AdapterRtId='{AdapterRtId}' deploy configuration",
            tenantId, adapterRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId);

                if (!configuration.Equals(adapter.Configuration))
                {
                    adapter.UpdateConfiguration(tenantId, configuration);
                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task DeployPipelineAsync(string tenantId, RtEntityId adapterRtEntityId, OctoObjectId pipelineRtId)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}', PipelineRtId='{PipelineRtId}' deploy debug configuration",
            tenantId, adapterRtEntityId, pipelineRtId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                var pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtId);
                if (pipeline == null)
                {
                    throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtId);
                }
                
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId);

                var deployedPipeline = configuration.Pipelines.FirstOrDefault(p => p.PipelineRtId == pipelineRtId);
                if (deployedPipeline != null)
                {
                    configuration.Pipelines.Remove(deployedPipeline);
                }
                configuration.Pipelines.Add(new PipelineConfigurationDto(
                    pipeline.RtId,
                    true,
                    pipeline.PipelineDefinition
                ));

                if (!configuration.Equals(adapter.Configuration))
                {
                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task ReloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Reloading tenant", tenantId);

        var adapters = await communicationRepository.GetAdaptersAsync(tenantId);
        foreach (var adapter in adapters)
        {
            if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
            {
                if (adapterTenant.AdapterById.ContainsKey(adapter.ToRtEntityId()))
                {
                    await SetAdapterOfflineAsync(tenantId, adapter.ToRtEntityId());
                    continue;
                }
            }
            else
            {
                adapterCache.AddOrUpdateTenant(tenantId);
            }

            await SetAdapterCommunicationStateAsync(tenantId, adapter.ToRtEntityId(), RtCommunicationStateEnum.Unregistered);
        }
    }

    public Task UnloadTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Unload tenant", tenantId);

        adapterCache.RemoveTenant(tenantId);

        return Task.CompletedTask;
    }

    private async Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState)
    {
        Logger.Info("[{TenantId}] Setting communicaton state of adapter '{AdapterRtEntityId}' to '{CommunicationState}'",
            tenantId, adapterRtEntityId, communicationState);
        try
        {
            await communicationRepository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, communicationState);
        }
        catch (Exception e)
        {
            Logger.Error(e,
                "[{TenantId}] Error setting communicaton state of adapter '{AdapterRtEntityId}' to '{CommunicationState}'",
                tenantId, adapterRtEntityId, communicationState);

            throw AdapterServiceException.CommonFailedSetAdapterCommunicationState(tenantId, adapterRtEntityId,
                communicationState, e);
        }
    }
}