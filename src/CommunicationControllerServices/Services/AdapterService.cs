using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    IAdapterHubCallbacks adapterHubCallbacks,
    ICommunicationEventService eventService)
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
            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' registered with connection id '{connectionId}'.",
                adapterRtEntityId);

            if (!adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' not found in cache, fetching from repository",
                    tenantId, adapterRtEntityId);
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, true);
                adapter = adapterTenant.AddAdapter(adapterRtEntityId, connectionId, configuration);
                // Note: Online state is already set in OnConnectedAsync, no need to set it again here
            }
            else
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' found in cache, checking for updates",
                    tenantId, adapterRtEntityId);
                var configuration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, true);
                if (!configuration.Equals(adapter.Configuration))
                {
                    adapterTenant.RemoveAdapter(adapterRtEntityId);
                    adapter = adapterTenant.AddAdapter(adapterRtEntityId, connectionId, configuration);
                    // Note: Online state is already set in OnConnectedAsync, no need to set it again here
                }
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
            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' unregistered with connection id '{connectionId}'.",
                adapterRtEntityId);

            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }

                await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                    RtCommunicationStateEnum.Unregistered);
            }

            adapterTenant.RemoveAdapter(adapterRtEntityId);
        }
    }

    public async Task<AdapterConfigurationDto> GetAdapterConfigurationAsync(string tenantId,
        RtEntityId adapterRtEntityId, bool onlyDeployedPipelines)
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
                    Logger.Warn("[{TenantId}] Data pipeline for pipeline '{PipelineRtId}' not found, skipping",
                        tenantId, rtPipeline.ToRtEntityId());
                    continue;
                }

                if (!onlyDeployedPipelines || rtPipeline.DeploymentState == RtDeploymentStateEnum.Pending ||
                    rtPipeline.DeploymentState == RtDeploymentStateEnum.Deployed)
                {
                    pipelineConfigurations.Add(
                        await CreatePipelineConfigurationAsync(tenantId, dataPipeline.RtId, rtPipeline, false));
                }
            }

            var adapterConfigurationDto = new AdapterConfigurationDto(
                adapterRtEntityId,
                adapter.Configuration,
                pipelineConfigurations
            );
            return adapterConfigurationDto;
        }
        catch (CommunicationRepositoryException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw AdapterServiceException.CommonFailedCannotLoadAdapterConfiguration(tenantId, adapterRtEntityId, e);
        }
    }

    public async Task SetAdapterCommunicationStateOnlineAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId)
    {
        if (!adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            throw AdapterServiceException.TenantNotEnabled(tenantId);
        }

        // Check if adapter was already online (has an existing connection)
        var wasAlreadyOnline = adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var existingAdapter)
                               && !string.IsNullOrWhiteSpace(existingAdapter.ConnectionId);

        if (wasAlreadyOnline)
        {
            Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' reconnected (previous connection: '{OldConnectionId}', new connection: '{NewConnectionId}')",
                tenantId, adapterRtEntityId, existingAdapter!.ConnectionId, connectionId);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' reconnected (new connection id: '{connectionId}').",
                adapterRtEntityId);
        }
        else
        {
            Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' online",
                tenantId, adapterRtEntityId);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' is now online.",
                adapterRtEntityId);
        }

        adapterTenant.UpdateConnectionId(adapterRtEntityId, connectionId);

        // Always update DB state, even if adapter is not in cache yet
        await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
            RtCommunicationStateEnum.Online);
    }

    public async Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline",
            tenantId, adapterRtEntityId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Adapter '{adapterRtEntityId}' is now offline.",
            adapterRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                adapterTenant.RemoveConnectionId(adapter.AdapterRtEntityId);
                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }
            }

            // Always update DB state, even if adapter is not in cache yet
            await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, RtCommunicationStateEnum.Offline);
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
                var configuration =
                    await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, false);

                if (!configuration.Equals(adapter.Configuration))
                {
                    Logger.Info("[{TenantId}] AdapterRtId='{AdapterRtId}' configuration is outdated, updating",
                        tenantId, adapterRtEntityId);
                    adapter.UpdateConfiguration(tenantId, configuration);

                    await communicationRepository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                        RtConfigurationStateEnum.Pending, null);
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                    }

                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);

                    await eventService.StoreInformationEventAsync(tenantId,
                        $"Configuration deployed to adapter '{adapterRtEntityId}' with {configuration.Pipelines.Count} pipeline(s).",
                        adapterRtEntityId);
                }
                else
                {
                    Logger.Info("[{TenantId}] AdapterRtId='{AdapterRtId}' configuration is up to date",
                        tenantId, adapterRtEntityId);
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

                var adapterConfiguration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, true);

                var deployedPipeline =
                    adapterConfiguration.Pipelines.FirstOrDefault(p => p.PipelineRtEntityId == pipelineRtEntityId);
                if (deployedPipeline != null)
                {
                    adapterConfiguration.Pipelines.Remove(deployedPipeline);
                }

                adapterConfiguration.Pipelines.Add(
                    await CreatePipelineConfigurationAsync(tenantId, dataPipeline.RtId, pipeline, true,
                        pipelineDefinition));

                if (!adapterConfiguration.Equals(adapter.Configuration))
                {
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                    }

                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, adapterConfiguration);
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

        await eventService.StoreInformationEventAsync(tenantId,
            $"Deploying data pipeline '{dataPipelineRtId}' to adapters.");

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            var adapterConfigurations = new Dictionary<RtEntityId, AdapterConfigurationDto>();
            var rtDeployPipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataPipelineRtId);
            if (!rtDeployPipelines.Any())
            {
                throw AdapterServiceException.DataPipelineAdapterNotFound(tenantId, dataPipelineRtId);
            }

            foreach (var rtDeployPipeline in rtDeployPipelines)
            {
                var rtAdapter = await communicationRepository
                    .GetAdapterByPipelineAsync(tenantId, rtDeployPipeline.ToRtEntityId());

                if (rtAdapter != null)
                {
                    if (adapterTenant.AdapterById.TryGetValue(rtAdapter.ToRtEntityId(), out var adapter))
                    {
                        if (!adapterConfigurations.TryGetValue(rtAdapter.ToRtEntityId(), out var adapterConfig))
                        {
                            adapterConfig = new AdapterConfigurationDto(rtAdapter.ToRtEntityId(),
                                rtAdapter.Configuration, new List<PipelineConfigurationDto>());

                            adapterConfigurations.Add(adapterConfig.AdapterRtEntityId, adapterConfig);
                        }

                        foreach (var deployedPipelineConfigurationDto in adapter.Configuration.Pipelines)
                        {
                            // If the pipeline is already deployed, we remove it so the new version can be added
                            if (deployedPipelineConfigurationDto.DataPipelineRtId == dataPipelineRtId)
                            {
                                adapterConfig.Pipelines.Remove(deployedPipelineConfigurationDto);
                            }
                            else if (!adapterConfig.Pipelines.Contains(deployedPipelineConfigurationDto))
                            {
                                adapterConfig.Pipelines.Add(deployedPipelineConfigurationDto);
                            }
                        }

                        adapterConfig.Pipelines.Add(
                            await CreatePipelineConfigurationAsync(tenantId, dataPipelineRtId, rtDeployPipeline,
                                false));
                    }
                    else
                    {
                        throw AdapterServiceException.AdapterNotLoaded(tenantId, rtAdapter.ToRtEntityId());
                    }
                }
            }

            await UpdateAdapterConfigurationAsync(tenantId, adapterConfigurations.Values.ToList());

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UndeployDataPipelineAsync(string tenantId, OctoObjectId dataPipelineRtId)
    {
        Logger.Info(
            "[{TenantId}] DataPipelineRtId='{PipelineRtEntityId}' undeploy edge and mesh pipeline from adapter",
            tenantId, dataPipelineRtId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Undeploying data pipeline '{dataPipelineRtId}' from adapters.",
            new RtEntityId(SystemCommunicationCkIds.RtCkDataPipelineTriggerTypeId, dataPipelineRtId));

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            var adapterConfigurations = new Dictionary<RtEntityId, AdapterConfigurationDto>();

            var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataPipelineRtId);
            if (!pipelines.Any())
            {
                throw AdapterServiceException.DataPipelineAdapterNotFound(tenantId, dataPipelineRtId);
            }

            foreach (var rtUndeployPipeline in pipelines)
            {
                var rtAdapter = await communicationRepository
                    .GetAdapterByPipelineAsync(tenantId, rtUndeployPipeline.ToRtEntityId());

                if (rtAdapter != null)
                {
                    if (adapterTenant.AdapterById.TryGetValue(rtAdapter.ToRtEntityId(), out var adapter))
                    {
                        // Ensure adapter configuration is created only once per adapter
                        if (!adapterConfigurations.TryGetValue(rtAdapter.ToRtEntityId(), out var adapterConfig))
                        {
                            adapterConfig = new AdapterConfigurationDto(rtAdapter.ToRtEntityId(),
                                rtAdapter.Configuration,
                                new List<PipelineConfigurationDto>());
                            adapterConfigurations.Add(adapterConfig.AdapterRtEntityId, adapterConfig);
                        }

                        foreach (var deployedPipelineConfigurationDto in adapter.Configuration.Pipelines)
                        {
                            if (deployedPipelineConfigurationDto.DataPipelineRtId != dataPipelineRtId &&
                                !adapterConfig.Pipelines.Contains(deployedPipelineConfigurationDto))
                            {
                                adapterConfig.Pipelines.Add(deployedPipelineConfigurationDto);
                            }
                            else
                            {
                                await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                                    deployedPipelineConfigurationDto.PipelineRtEntityId,
                                    RtDeploymentStateEnum.Undeployed,
                                    null);
                            }
                        }
                    }
                    else
                    {
                        throw AdapterServiceException.AdapterNotLoaded(tenantId, rtAdapter.ToRtEntityId());
                    }
                }
            }

            await UpdateAdapterConfigurationAsync(tenantId, adapterConfigurations.Values.ToList());

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UpdateConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        DeploymentResult deploymentResult)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}' update configuration state '{DeploymentResult}'",
            tenantId, adapterRtEntityId, deploymentResult.IsSuccess);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                if (deploymentResult.IsSuccess)
                {
                    await communicationRepository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                        RtConfigurationStateEnum.Configured, null);

                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Deployed, null);
                    }

                    await eventService.StoreInformationEventAsync(tenantId,
                        $"Adapter '{adapterRtEntityId}' configuration deployed successfully with {adapter.Configuration.Pipelines.Count} pipeline(s).",
                        adapterRtEntityId);
                }
                else
                {
                    var message = GenerateAdapterMessages(deploymentResult);
                    await communicationRepository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                        RtConfigurationStateEnum.Error, message);

                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        var pipelineMessage = GeneratePipelineMessages(deploymentResult, pipelineConfigurationDto);
                        var state = RtDeploymentStateEnum.Deployed;
                        if (pipelineMessage != null)
                        {
                            state = RtDeploymentStateEnum.Error;
                        }

                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, state, pipelineMessage);
                    }

                    await eventService.StoreErrorEventAsync(tenantId,
                        $"Adapter '{adapterRtEntityId}' configuration deployment failed: {message}",
                        adapterRtEntityId);
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task<DeploymentResultDto> GetPipelineDeploymentStateAsync(string tenantId,
        RtEntityId pipelineRtEntityId)
    {
        Logger.Info(
            "[{TenantId}] GetPipelineDeploymentStateAsync PipelineRtEntityId='{PipelineRtEntityId}'",
            tenantId, pipelineRtEntityId);

        if (adapterCache.TryGetTenant(tenantId, out _))
        {
            var r = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            if (r == null)
            {
                throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);
            }

            DeploymentState state;
            switch (r.DeploymentState)
            {
                case RtDeploymentStateEnum.Deployed:
                    state = DeploymentState.Success;
                    break;
                case RtDeploymentStateEnum.Undeployed:
                case RtDeploymentStateEnum.Pending:
                    state = DeploymentState.Processing;
                    break;
                case RtDeploymentStateEnum.Error:
                    state = DeploymentState.Failed;
                    break;
                default:
                    throw AdapterServiceException.DeploymentStateNotSupported(r.DeploymentState);
            }

            return new DeploymentResultDto(r.ToRtEntityId(), state, r.StatusMessage);
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    private async Task<PipelineConfigurationDto> CreatePipelineConfigurationAsync(string tenantId,
        OctoObjectId dataPipelineRtId, RtPipeline rtPipeline, bool isDebuggingEnabled,
        string? pipelineDefinition = null)
    {
        var pipelineConfigurations = await communicationRepository
            .GetConfigurationsByPipelineAsync(tenantId, rtPipeline.RtId);

        var configurationsDto = pipelineConfigurations.Select(c => new ConfigurationDto(c.RtId,
            c.CkTypeId ?? throw AdapterServiceException.CkTypeIdUndefined(),
            c.RtWellKnownName ?? throw AdapterServiceException.RtWellKnownNameUndefined(),
            c.Serialize()));

        return new PipelineConfigurationDto(
            dataPipelineRtId,
            rtPipeline.ToRtEntityId(),
            isDebuggingEnabled,
            pipelineDefinition ?? rtPipeline.PipelineDefinition,
            configurationsDto);
    }

    private async Task UpdateAdapterConfigurationAsync(string tenantId,
        List<AdapterConfigurationDto> adapterConfigurations)
    {
        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            foreach (var adapterConfigurationDto in adapterConfigurations)
            {
                if (adapterTenant.AdapterById.TryGetValue(adapterConfigurationDto.AdapterRtEntityId, out var adapter))
                {
                    adapter.UpdateConfiguration(tenantId, adapterConfigurationDto);

                    await communicationRepository.SetAdapterConfigurationStateAsync(tenantId,
                        adapter.AdapterRtEntityId, RtConfigurationStateEnum.Pending, null);
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                    }

                    await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, adapterConfigurationDto);
                }
            }
        }
    }

    private static string? GeneratePipelineMessages(DeploymentResult deploymentResult,
        PipelineConfigurationDto pipelineConfigurationDto)
    {
        var messages = deploymentResult.ErrorMessages?.Where(x =>
                           x.PipelineRtEntityId == pipelineConfigurationDto.PipelineRtEntityId).ToList() ??
                       [];

        var message = messages.Any()
            ? string.Join(Environment.NewLine, messages.Select(x => x.ErrorMessage))
            : null;
        return message;
    }

    private static string GenerateAdapterMessages(DeploymentResult deploymentResult)
    {
        var message = deploymentResult.ErrorMessages != null
            ? string.Join(Environment.NewLine,
                deploymentResult.ErrorMessages.Select(x => $"{x.PipelineRtEntityId?.ToString() ?? "ADAPTER:"}: {x.ErrorMessage}"))
            : CommunicationControllerTexts.DeploymentUnknownAdapterError;
        return message;
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Pre update tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();
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

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Tenant pre-update completed. {adapterTenant.AdapterById.Count} adapter(s) disconnected.");
            }
        }
        catch (Exception e)
        {
            throw AdapterServiceException.PreUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
            Logger.Info("[{TenantId}] Pre update tenant completed", tenantId);
        }
    }

    public async Task PosUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Pos update tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();
            adapterCache.AddOrUpdateTenant(tenantId);

            await eventService.StoreInformationEventAsync(tenantId,
                "Tenant post-update completed. Adapter cache re-initialized.");
        }
        catch (Exception e)
        {
            throw AdapterServiceException.PosUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
            Logger.Info("[{TenantId}] Pos update tenant completed", tenantId);
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