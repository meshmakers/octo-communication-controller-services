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

            await SetAdapterCommunicationStateOnlineAsync(tenantId, adapterRtEntityId, connectionId);

            foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
            {
                await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                    pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Deployed);
            }

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
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending);
                }

                await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                    RtCommunicationStateEnum.Unregistered);
            }

            adapterTenant.RemoveAdapter(adapterRtEntityId);

            return;
        }
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

    public async Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId,
        RtEntityId adapterRtEntityId)
    {
        try
        {
            if (!adapterCache.TryGetTenant(tenantId, out _))
            {
                throw AdapterServiceException.TenantNotEnabled(tenantId);
            }

            var adapter = await communicationRepository.GetAdapterAsync(tenantId, adapterRtEntityId);

            var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, adapterRtEntityId);

            var pipelineConfigurations = new List<PipelineConfigurationDto>();
            foreach (var rtPipeline in pipelines)
            {
                if (string.IsNullOrWhiteSpace(rtPipeline.PipelineDefinition))
                {
                    continue;
                }

                var dataPipeline =
                    await communicationRepository.GetDataPipelineByPipelineAsync(tenantId, rtPipeline.RtId);
                if (dataPipeline == null)
                {
                    throw AdapterServiceException.DataPipelineNotFound(tenantId, rtPipeline.ToRtEntityId());
                }

                var pipelineConfiguration = new PipelineConfigurationDto(
                    dataPipeline.RtId,
                    rtPipeline.ToRtEntityId(),
                    false,
                    rtPipeline.PipelineDefinition
                );
                pipelineConfigurations.Add(pipelineConfiguration);
            }

            var adapterConfigurationDto = new AdapterConfigurationDto(
                adapterRtEntityId,
                adapter.Configuration,
                pipelineConfigurations
            );
            return adapterConfigurationDto;
        }
        catch (Exception e)
        {
            throw AdapterServiceException.CommonFailedCannotLoadAdapterConfiguration(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task SetAdapterCommunicationStateOnlineAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' online",
            tenantId, adapterRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                adapterTenant.UpdateConnectionId(adapterRtEntityId, connectionId);
                await SetAdapterCommunicationStateAsync(tenantId, adapter.AdapterRtEntityId,
                    RtCommunicationStateEnum.Online);
            }

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId)
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
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Deployed);
                    }
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task DeployPipelineAsync(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId,
        string? pipelineDefinition = null)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}', PipelineRtEntityId='{PipelineRtEntityId}' deploy debug configuration",
            tenantId, adapterRtEntityId, pipelineRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                var pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
                if (pipeline == null)
                {
                    throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);
                }

                var dataPipeline =
                    await communicationRepository.GetDataPipelineByPipelineAsync(tenantId, pipeline.RtId);
                if (dataPipeline == null)
                {
                    throw AdapterServiceException.DataPipelineNotFound(tenantId, pipelineRtEntityId);
                }

                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId);

                var deployedPipeline =
                    configuration.Pipelines.FirstOrDefault(p => p.PipelineRtEntityId == pipelineRtEntityId);
                if (deployedPipeline != null)
                {
                    configuration.Pipelines.Remove(deployedPipeline);
                }

                configuration.Pipelines.Add(new PipelineConfigurationDto(
                    dataPipeline.RtId,
                    pipeline.ToRtEntityId(),
                    true,
                    pipelineDefinition ?? pipeline.PipelineDefinition
                ));

                if (!configuration.Equals(adapter.Configuration))
                {
                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Deployed);
                    }
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task DeployDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId)
    {
        Logger.Info(
            "[{TenantId}] DataPipelineRtId='{PipelineRtEntityId}' deploy edge and mesh pipeline to adapter",
            tenantId, dataPipelineRtId);

        var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataPipelineRtId);
        foreach (var rtPipeline in pipelines)
        {
            var adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId, rtPipeline.ToRtEntityId());
            if (adapter != null)
            {
                await DeployAdapterConfigurationAsync(tenantId, adapter.ToRtEntityId());
            }
        }
    }

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Pre update tenant", tenantId);

        try
        {
            if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
            {
                // Inform all adapters that tenant is going to be updated
                await adapterHubCallbacks.PreUpdateTenantAsync(tenantId);
                // Remove all adapters from cache, so we skip the possibility to communicate with them
                adapterCache.RemoveTenant(tenantId);

                foreach (var adapter in adapterTenant.AdapterById.Values)
                {
                    await SetAdapterCommunicationStateAsync(tenantId, adapter.AdapterRtEntityId,
                        RtCommunicationStateEnum.Unregistered);
                }
            }
        }
        catch (Exception e)
        {
            throw AdapterServiceException.PreUpdateTenantFailed(tenantId, e);
        }
    }

    public Task PosUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Pos update tenant", tenantId);

        try
        {
            adapterCache.AddOrUpdateTenant(tenantId);
            return Task.CompletedTask;
        }
        catch (Exception e)
        {
            throw AdapterServiceException.PosUpdateTenantFailed(tenantId, e);
        }
    }

    private async Task SetAdapterCommunicationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtCommunicationStateEnum communicationState)
    {
        Logger.Info(
            "[{TenantId}] Setting communicaton state of adapter '{AdapterRtEntityId}' to '{CommunicationState}'",
            tenantId, adapterRtEntityId, communicationState);
        try
        {
            await communicationRepository.SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                communicationState);
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