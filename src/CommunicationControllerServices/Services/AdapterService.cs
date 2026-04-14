using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class AdapterService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    IAdapterHubCallbacks adapterHubCallbacks,
    ICommunicationEventService eventService,
    IPipelineSchemaValidator pipelineSchemaValidator,
    IOptions<CommunicationControllerOptions> communicationControllerOptions)
    : IAdapterService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private static readonly TimeSpan DeploymentTimeout = TimeSpan.FromSeconds(120);

    private readonly ConcurrentDictionary<RtEntityId, TaskCompletionSource<DeploymentResult>>
        _pendingDeployments = new();

    public Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId)
    {
        return RegisterAdapterInternalAsync(tenantId, adapterRtEntityId, connectionId, null);
    }

    public Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId, IReadOnlyList<NodeDescriptorDto> nodeDescriptors)
    {
        return RegisterAdapterInternalAsync(tenantId, adapterRtEntityId, connectionId, nodeDescriptors);
    }

    public Task<AdapterConfigurationDto> RegisterAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId, IReadOnlyList<NodeDescriptorDto> nodeDescriptors, string pipelineSchemaJson)
    {
        return RegisterAdapterInternalAsync(tenantId, adapterRtEntityId, connectionId, nodeDescriptors, pipelineSchemaJson);
    }

    public string? GetPipelineSchema(string tenantId, RtEntityId adapterRtEntityId)
    {
        if (!adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            throw AdapterServiceException.TenantNotEnabled(tenantId);
        }

        if (!adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
        {
            throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
        }

        return adapter.PipelineSchemaJson;
    }

    public IReadOnlyList<NodeDescriptorDto> GetAllNodeDescriptors(string tenantId)
    {
        if (!adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            throw AdapterServiceException.TenantNotEnabled(tenantId);
        }

        // Aggregate node descriptors from all connected adapters, deduplicating by NodeName+Version
        var seen = new HashSet<(string, int)>();
        var result = new List<NodeDescriptorDto>();

        foreach (var adapter in adapterTenant.AdapterById.Values)
        {
            if (adapter.NodeDescriptors == null) continue;

            foreach (var descriptor in adapter.NodeDescriptors)
            {
                if (seen.Add((descriptor.NodeName, descriptor.Version)))
                {
                    result.Add(descriptor);
                }
            }
        }

        return result;
    }

    private async Task<AdapterConfigurationDto> RegisterAdapterInternalAsync(string tenantId,
        RtEntityId adapterRtEntityId, string connectionId, IReadOnlyList<NodeDescriptorDto>? nodeDescriptors,
        string? pipelineSchemaJson = null)
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

            if (nodeDescriptors != null)
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' reported {NodeCount} node descriptors",
                    tenantId, adapterRtEntityId, nodeDescriptors.Count);
                adapter.SetNodeDescriptors(nodeDescriptors);
            }

            if (pipelineSchemaJson != null)
            {
                Logger.Info("[{TenantId}] Adapter '{AdapterRtId}' reported pipeline schema ({SchemaLength} chars)",
                    tenantId, adapterRtEntityId, pipelineSchemaJson.Length);
                adapter.SetPipelineSchema(pipelineSchemaJson);
            }

            return adapter.Configuration;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UnregisterAsync(string tenantId, RtEntityId adapterRtEntityId, string connectionId)
    {
        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                // Check if the unregistering connection is still the current one.
                // If a newer connection has already replaced it, this is a stale unregister
                // from an old connection and should be ignored.
                if (!string.IsNullOrWhiteSpace(adapter.ConnectionId) && adapter.ConnectionId != connectionId)
                {
                    Logger.Warn(
                        "[{TenantId}] AdapterRtId='{AdapterRtId}' ignoring stale unregister for connection '{OldConnectionId}' " +
                        "(current connection: '{CurrentConnectionId}')",
                        tenantId, adapterRtEntityId, connectionId, adapter.ConnectionId);
                    return;
                }

                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }

                await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
                    RtCommunicationStateEnum.Unregistered);
            }

            Logger.Info("[{TenantId}] Adapter '{AdapterRtEntityId}' unregistered with connection id '{ConnectionId}'",
                tenantId, adapterRtEntityId, connectionId);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' unregistered with connection id '{connectionId}'.",
                adapterRtEntityId);

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

                // Skip disabled pipelines
                if (rtPipeline.Enabled == false)
                {
                    continue;
                }

                var dataFlow =
                    await communicationRepository.GetDataFlowByPipelineAsync(tenantId, rtPipeline.RtId);
                if (dataFlow == null)
                {
                    Logger.Warn("[{TenantId}] Data flow for pipeline '{PipelineRtId}' not found, skipping",
                        tenantId, rtPipeline.ToRtEntityId());
                    continue;
                }

                if (!onlyDeployedPipelines || rtPipeline.DeploymentState == RtDeploymentStateEnum.Pending ||
                    rtPipeline.DeploymentState == RtDeploymentStateEnum.Deployed)
                {
                    pipelineConfigurations.Add(
                        await CreatePipelineConfigurationAsync(tenantId, dataFlow.RtId, rtPipeline));
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

        var oldConnectionId = existingAdapter?.ConnectionId;

        // Update cache connection ID immediately BEFORE any async operations.
        // This ensures that a concurrent OnDisconnectedAsync from the old connection
        // will see the new connection ID in the cache and correctly identify itself
        // as a stale disconnect, preventing it from overwriting the Online state.
        adapterTenant.UpdateConnectionId(adapterRtEntityId, connectionId);

        if (wasAlreadyOnline)
        {
            Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' reconnected (previous connection: '{OldConnectionId}', new connection: '{NewConnectionId}')",
                tenantId, adapterRtEntityId, oldConnectionId, connectionId);

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

        // Always update DB state, even if adapter is not in cache yet
        await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId,
            RtCommunicationStateEnum.Online);
    }

    public async Task SetAdapterCommunicationStateOfflineAsync(string tenantId, RtEntityId adapterRtEntityId,
        string connectionId)
    {
        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                // Check if the disconnecting connection is still the current one.
                // If a newer connection has already replaced it, this is a stale disconnect
                // event and should be ignored to avoid overwriting the Online state.
                if (!string.IsNullOrWhiteSpace(adapter.ConnectionId) && adapter.ConnectionId != connectionId)
                {
                    Logger.Warn(
                        "[{TenantId}] AdapterRtId='{AdapterRtId}' ignoring stale disconnect for connection '{OldConnectionId}' " +
                        "(current connection: '{CurrentConnectionId}')",
                        tenantId, adapterRtEntityId, connectionId, adapter.ConnectionId);
                    return;
                }

                adapterTenant.RemoveConnectionId(adapter.AdapterRtEntityId);
                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }
            }

            Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline (connection '{ConnectionId}')",
                tenantId, adapterRtEntityId, connectionId);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Adapter '{adapterRtEntityId}' is now offline.",
                adapterRtEntityId);

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

                    await SendConfigurationAndWaitForResultAsync(tenantId, adapterRtEntityId, configuration);

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
                if (pipelineDefinition != null)
                {
                    ValidatePipelineDefinition(tenantId, adapterRtEntityId, adapter, pipelineDefinition);
                }

                var pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
                if (pipeline == null)
                {
                    throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);
                }

                var dataFlow =
                    await communicationRepository.GetDataFlowByPipelineAsync(tenantId, pipeline.RtId);
                if (dataFlow == null)
                {
                    throw AdapterServiceException.DataFlowNotFound(tenantId, pipelineRtEntityId);
                }

                // Persist the pipeline definition to the RT entity so it is visible in the UI
                if (pipelineDefinition != null)
                {
                    // SetPipelineDefinitionAsync also syncs SendsDataTo associations
                    await communicationRepository.SetPipelineDefinitionAsync(tenantId, pipelineRtEntityId,
                        pipelineDefinition);
                }
                else if (!string.IsNullOrEmpty(pipeline.PipelineDefinition))
                {
                    // Sync SendsDataTo associations from existing definition (e.g. after import)
                    await communicationRepository.SyncPipelineDataConnectionsAsync(tenantId, pipelineRtEntityId,
                        pipeline.PipelineDefinition);
                }

                // Enable debugging when deploying via the pipeline deploy endpoint.
                // The IsDebuggingEnabled state is persisted on the RT entity so it survives page reloads.
                if (pipeline.IsDebuggingEnabled != true)
                {
                    await communicationRepository.SetPipelineDebuggingEnabledAsync(tenantId, pipelineRtEntityId, true);
                    pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId)
                               ?? throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);
                }

                // Start from the cached adapter configuration to preserve debug state
                // of already-deployed pipelines. Fall back to DB if no cache exists.
                AdapterConfigurationDto adapterConfiguration;
                if (adapter.Configuration.Pipelines.Count > 0)
                {
                    adapterConfiguration = new AdapterConfigurationDto(
                        adapter.Configuration.AdapterRtEntityId,
                        adapter.Configuration.AdapterConfiguration,
                        new List<PipelineConfigurationDto>(adapter.Configuration.Pipelines));
                }
                else
                {
                    adapterConfiguration = await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, true);
                }

                var deployedPipeline =
                    adapterConfiguration.Pipelines.FirstOrDefault(p => p.PipelineRtEntityId == pipelineRtEntityId);
                if (deployedPipeline != null)
                {
                    adapterConfiguration.Pipelines.Remove(deployedPipeline);
                }

                adapterConfiguration.Pipelines.Add(
                    await CreatePipelineConfigurationAsync(tenantId, dataFlow.RtId, pipeline,
                        pipelineDefinition));

                if (!adapterConfiguration.Equals(adapter.Configuration))
                {
                    foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                    {
                        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                    }

                    await SendConfigurationAndWaitForResultAsync(tenantId, adapterRtEntityId, adapterConfiguration);

                    // Update the cached configuration so UpdateConfigurationStateAsync
                    // sees the correct pipelines for deployment state updates
                    adapter.UpdateConfiguration(tenantId, adapterConfiguration);
                }

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task DeployDataFlowAsync(string tenantId, OctoObjectId dataFlowRtId)
    {
        Logger.Info(
            "[{TenantId}] DataFlowRtId='{DataFlowRtId}' deploy pipelines to adapter",
            tenantId, dataFlowRtId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Deploying data flow '{dataFlowRtId}' to adapters.");

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            var adapterConfigurations = new Dictionary<RtEntityId, AdapterConfigurationDto>();
            var rtDeployPipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataFlowRtId);
            if (!rtDeployPipelines.Any())
            {
                throw AdapterServiceException.DataFlowHasNoPipelines(tenantId, dataFlowRtId);
            }

            foreach (var rtDeployPipeline in rtDeployPipelines)
            {
                // Skip disabled pipelines
                if (rtDeployPipeline.Enabled == false)
                {
                    continue;
                }

                var rtAdapter = await communicationRepository
                    .GetAdapterByPipelineAsync(tenantId, rtDeployPipeline.ToRtEntityId());

                if (rtAdapter == null)
                {
                    throw AdapterServiceException.PipelineAdapterNotAssigned(tenantId, rtDeployPipeline.ToRtEntityId());
                }

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
                        if (deployedPipelineConfigurationDto.DataFlowRtId == dataFlowRtId)
                        {
                            adapterConfig.Pipelines.Remove(deployedPipelineConfigurationDto);
                        }
                        else if (!adapterConfig.Pipelines.Contains(deployedPipelineConfigurationDto))
                        {
                            adapterConfig.Pipelines.Add(deployedPipelineConfigurationDto);
                        }
                    }

                    adapterConfig.Pipelines.Add(
                        await CreatePipelineConfigurationAsync(tenantId, dataFlowRtId, rtDeployPipeline));
                }
                else
                {
                    throw AdapterServiceException.AdapterNotLoaded(tenantId, rtAdapter.ToRtEntityId());
                }
            }

            await UpdateAdapterConfigurationAsync(tenantId, adapterConfigurations.Values.ToList());

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task UndeployDataFlowAsync(string tenantId, OctoObjectId dataFlowRtId)
    {
        Logger.Info(
            "[{TenantId}] DataFlowRtId='{DataFlowRtId}' undeploy pipelines from adapter",
            tenantId, dataFlowRtId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Undeploying data flow '{dataFlowRtId}' from adapters.",
            new RtEntityId(SystemCommunicationCkIds.RtCkDataFlowTypeId, dataFlowRtId));

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            var adapterConfigurations = new Dictionary<RtEntityId, AdapterConfigurationDto>();

            var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, dataFlowRtId);
            if (!pipelines.Any())
            {
                throw AdapterServiceException.DataFlowHasNoPipelines(tenantId, dataFlowRtId);
            }

            foreach (var rtUndeployPipeline in pipelines)
            {
                var rtAdapter = await communicationRepository
                    .GetAdapterByPipelineAsync(tenantId, rtUndeployPipeline.ToRtEntityId());

                if (rtAdapter == null)
                {
                    Logger.Warn("[{TenantId}] Pipeline '{PipelineRtEntityId}' has no adapter assigned, skipping undeploy",
                        tenantId, rtUndeployPipeline.ToRtEntityId());
                    continue;
                }

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
                        if (deployedPipelineConfigurationDto.DataFlowRtId != dataFlowRtId &&
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

            await UpdateAdapterConfigurationAsync(tenantId, adapterConfigurations.Values.ToList());

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }


    private async Task SendConfigurationAndWaitForResultAsync(string tenantId, RtEntityId adapterRtEntityId,
        AdapterConfigurationDto adapterConfiguration)
    {
        var tcs = new TaskCompletionSource<DeploymentResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDeployments[adapterRtEntityId] = tcs;

        try
        {
            await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, adapterConfiguration);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(DeploymentTimeout));
            if (completedTask != tcs.Task)
            {
                _pendingDeployments.TryRemove(adapterRtEntityId, out _);
                throw AdapterServiceException.DeploymentTimedOut(tenantId, adapterRtEntityId, DeploymentTimeout);
            }

            var result = await tcs.Task;
            if (!result.IsSuccess)
            {
                throw AdapterServiceException.DeploymentFailed(tenantId, adapterRtEntityId,
                    GenerateAdapterMessages(result));
            }
        }
        catch
        {
            _pendingDeployments.TryRemove(adapterRtEntityId, out _);
            throw;
        }
    }

    public async Task UpdateConfigurationStateAsync(string tenantId, RtEntityId adapterRtEntityId,
        DeploymentResult deploymentResult)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}' update configuration state '{DeploymentResult}'",
            tenantId, adapterRtEntityId, deploymentResult.IsSuccess);

        // Complete any pending deployment wait so the caller of DeployPipelineAsync/DeployAdapterConfigurationAsync
        // gets unblocked before we update the database state.
        if (_pendingDeployments.TryRemove(adapterRtEntityId, out var tcs))
        {
            tcs.TrySetResult(deploymentResult);
        }

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

            // Adapter not yet in cache - this can happen during service restart when the adapter
            // sends deployment results before re-registering. The adapter will re-register and
            // receive a new configuration, so this deployment result can be safely ignored.
            Logger.Warn(
                "[{TenantId}] AdapterRtId='{AdapterRtId}' received deployment update but adapter is not loaded in cache. " +
                "This can occur during service restart. The deployment result will be ignored.",
                tenantId, adapterRtEntityId);
            return;
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
        OctoObjectId dataFlowRtId, RtPipeline rtPipeline,
        string? pipelineDefinition = null)
    {
        var pipelineConfigurations = await communicationRepository
            .GetConfigurationsByPipelineAsync(tenantId, rtPipeline.RtId);

        var configurationsDto = pipelineConfigurations.Select(c => new ConfigurationDto(c.RtId,
            c.CkTypeId ?? throw AdapterServiceException.CkTypeIdUndefined(),
            c.RtWellKnownName ?? throw AdapterServiceException.RtWellKnownNameUndefined(),
            c.Serialize()));

        return new PipelineConfigurationDto(
            dataFlowRtId,
            rtPipeline.ToRtEntityId(),
            rtPipeline.IsDebuggingEnabled ?? false,
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

            var adapters = await communicationRepository.GetAdaptersAsync(tenantId);
            foreach (var adapter in adapters)
            {
                await communicationRepository.SetAdapterCommunicationStateAsync(tenantId, adapter.ToRtEntityId(),
                    RtCommunicationStateEnum.Offline);
            }

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

    private void ValidatePipelineDefinition(string tenantId, RtEntityId adapterRtEntityId,
        Adapter adapter, string pipelineDefinition)
    {
        if (!communicationControllerOptions.Value.EnablePipelineSchemaValidation) return;
        if (adapter.PipelineSchemaJson == null) return;

        var errors = pipelineSchemaValidator.Validate(pipelineDefinition, adapter.PipelineSchemaJson);
        if (errors.Count > 0)
        {
            Logger.Warn("[{TenantId}] Pipeline schema validation failed for adapter '{AdapterRtId}': {Errors}",
                tenantId, adapterRtEntityId, string.Join("; ", errors));
            throw AdapterServiceException.PipelineSchemaValidationFailed(tenantId, adapterRtEntityId, errors);
        }
    }

    public async Task<IReadOnlyList<AdapterSummaryDto>> GetAdapterSummariesAsync(string tenantId)
    {
        var adapters = await communicationRepository.GetAdaptersAsync(tenantId);
        return adapters.Select(a => new AdapterSummaryDto
        {
            RtId = a.RtId.ToString(),
            Name = a.Name ?? string.Empty,
            Description = a.Description,
            CommunicationState = (CommunicationState)(int)a.CommunicationState,
            ConfigurationState = (ConfigurationState)(int)a.ConfigurationState,
            DeploymentState = (EntityDeploymentState)(int)a.DeploymentState,
            CommunicationStateTimestamp = a.CommunicationStateTimestamp,
            ImageName = a.ImageName,
            ImageVersion = a.ImageVersion,
            StatusMessage = a.StatusMessage
        }).ToList();
    }
}