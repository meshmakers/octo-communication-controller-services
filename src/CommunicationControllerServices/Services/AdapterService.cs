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
    IPipelineDefinitionService pipelineDefinitionService,
    IAdapterConnectionTracker connectionTracker,
    IOptions<CommunicationControllerOptions> communicationControllerOptions,
    IWorkloadLifecycleService workloadLifecycleService)
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
                else if (adapter.ConnectionId != connectionId)
                {
                    // The adapter reconnected on a NEW SignalR connection (e.g. after a
                    // CkModelChanged / PreUpdateTenant restart) but its configuration is
                    // unchanged. Registration always arrives on the adapter's current live
                    // connection, so it is the authoritative source of the ConnectionId.
                    // Refresh it unconditionally here — relying solely on
                    // SetAdapterCommunicationStateOnlineAsync (OnConnectedAsync) is not enough,
                    // because that update can be raced or no-op'd (e.g. if the adapter was
                    // momentarily removed from the cache). If the cached ConnectionId is left
                    // stale, AdapterConfigurationUpdatedAsync keeps sending config to the dead
                    // connection via Clients.Client(adapter.ConnectionId) and every deploy
                    // silently times out after 120s while the adapter stays Online (AB#4594).
                    Logger.Info(
                        "[{TenantId}] Adapter '{AdapterRtId}' reconnected with unchanged configuration; " +
                        "refreshing cached connection id '{OldConnectionId}' -> '{NewConnectionId}'",
                        tenantId, adapterRtEntityId, adapter.ConnectionId, connectionId);
                    adapterTenant.UpdateConnectionId(adapterRtEntityId, connectionId);
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

            // AB#4594: reconcile the live adapter by actively re-pushing its deployed
            // configuration onto the freshly registered connection. Returning the config DTO
            // above is not sufficient on its own — during a coordinated controller+adapter
            // rollout an adapter can come up Online with none of its pipeline routes registered
            // (every FromHttpRequest endpoint 404s) while the controller still believes the
            // pipelines are Deployed, and nothing re-drives the config onto the new connection.
            await ReconcileAdapterConfigurationAsync(tenantId, adapterRtEntityId, adapter.Configuration);

            return adapter.Configuration;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    /// <summary>
    /// AB#4594: re-push an adapter's deployed configuration to a freshly (re-)registered
    /// connection so the live adapter self-heals its pipeline routes.
    /// </summary>
    /// <remarks>
    /// The registration return value alone is not enough. During a coordinated
    /// controller+adapter rollout (both pods restarting within seconds) an adapter can come up
    /// Online while none of its pipelines' routes are registered — every FromHttpRequest
    /// endpoint returns 404 — even though the controller still reports the pipelines as Deployed.
    /// Nothing re-drives the configuration onto the new connection, so the outage persists until
    /// a manual workload recreate.
    ///
    /// Re-pushing here reconciles the live adapter, and the adapter's ack
    /// (<see cref="UpdateConfigurationStateAsync"/>) transitions the pipelines to Deployed — which
    /// also clears the long-standing "stuck Pending after restart" drift, since a registration
    /// delivered purely via the return value was never acked.
    ///
    /// Best-effort by contract: it MUST NOT fail the registration (the return value still carries
    /// the configuration and the next deploy/reconnect retries), and it intentionally uses the raw,
    /// non-waiting <see cref="IAdapterHubCallbacks.AdapterConfigurationUpdatedAsync"/> send rather
    /// than <see cref="SendConfigurationAndWaitForResultAsync"/> so the register RPC is never
    /// blocked for the 120s deploy-ack window.
    /// </remarks>
    private async Task ReconcileAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId,
        AdapterConfigurationDto configuration)
    {
        if (configuration.Pipelines.Count == 0)
        {
            // Nothing deployed to this adapter — a push would be a no-op that only adds noise.
            return;
        }

        try
        {
            Logger.Info(
                "[{TenantId}] Adapter '{AdapterRtId}' re-pushing {PipelineCount} deployed pipeline(s) on registration",
                tenantId, adapterRtEntityId, configuration.Pipelines.Count);
            await adapterHubCallbacks.AdapterConfigurationUpdatedAsync(tenantId, configuration);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{TenantId}] Adapter '{AdapterRtId}' reconcile push on registration failed; the registration " +
                "return value still carries the configuration and the next deploy/reconnect will retry",
                tenantId, adapterRtEntityId);
        }
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

            // Remove ONLY if this connection is still the current one. The stale-unregister
            // guard above early-returns when the adapter has *already* reconnected before we
            // entered — but the adapter can also re-register on a new connection DURING the
            // awaits between that guard and here. An unconditional RemoveAdapter would then
            // delete the freshly registered live connection, leaving every subsequent deploy
            // failing with AdapterNotLoaded ("no live SignalR connection") until a pod restart
            // (AB#4594). The compare-and-remove is atomic under the cache's connection lock.
            adapterTenant.RemoveAdapterIfConnection(adapterRtEntityId, connectionId);
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

        // Record liveness in the reconciliation tracker (AB#4699). Unlike the config cache above,
        // this survives a tenant pre/post-update flush, so the offline-reconciliation sweep can
        // trust a tracker-miss to mean "no live connection".
        connectionTracker.TrackConnected(tenantId, adapterRtEntityId, connectionId);

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

                // Clear the connection only if it is still the current one — atomic
                // compare-and-clear closes the residual TOCTOU between the guard above and
                // here (a reconnect that lands in the gap must keep its live connection).
                adapterTenant.RemoveConnectionIdIfConnection(adapter.AdapterRtEntityId, connectionId);
                foreach (var pipelineConfigurationDto in adapter.Configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }
            }

            // Clear the reconciliation tracker (AB#4699). Compare-and-remove: a stale disconnect
            // whose connection has already been replaced no-ops, keeping the live entry.
            connectionTracker.TrackDisconnected(tenantId, adapterRtEntityId, connectionId);

            Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline (connection '{ConnectionId}')",
                tenantId, adapterRtEntityId, connectionId);

            // AB#4919: a hibernating workload disconnects because we asked it to. Recording that as
            // an offline event would fill the tenant's audit trail with one entry per idle cycle and
            // make a real outage indistinguishable from routine scale-to-zero. The state write below
            // still happens — Offline is factually true while hibernated, and Studio reads it
            // through LifecycleState.
            if (await workloadLifecycleService.IsIntentionallyDownAsync(tenantId, adapterRtEntityId.RtId))
            {
                Logger.Info("[{TenantId}] adapter rt id '{AdapterRtId}' offline as part of hibernation; " +
                            "skipping the offline audit event", tenantId, adapterRtEntityId);
            }
            else
            {
                await eventService.StoreInformationEventAsync(tenantId,
                    $"Adapter '{adapterRtEntityId}' is now offline.",
                    adapterRtEntityId);
            }

            await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, RtCommunicationStateEnum.Offline);
            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    public async Task<int> ReconcileOrphanedOnlineAdaptersAsync(string tenantId)
    {
        var adapters = await communicationRepository.GetAdaptersAsync(tenantId);

        var reconciled = 0;
        foreach (var adapter in adapters)
        {
            if (adapter.CommunicationState != RtCommunicationStateEnum.Online)
            {
                continue;
            }

            if (adapter.CkTypeId is null)
            {
                // Defensive: a runtime adapter should always carry its concrete type id.
                Logger.Warn("[{TenantId}] Skipping offline reconciliation for adapter '{AdapterRtId}' with no CkTypeId",
                    tenantId, adapter.RtId);
                continue;
            }

            var adapterRtEntityId = new RtEntityId(adapter.CkTypeId, adapter.RtId);

            // Re-check liveness immediately before writing so a reconnect that landed after the
            // sweep started is not clobbered. The tracker (unlike the config cache) is not flushed
            // by tenant updates, so a miss here reliably means "no live SignalR connection".
            if (connectionTracker.HasLiveConnection(tenantId, adapterRtEntityId))
            {
                continue;
            }

            // AB#4919: same reasoning as in SetAdapterCommunicationStateOfflineAsync — a hibernating
            // workload has no connection by design. The reconciliation itself must still run (a
            // stale Online would show the workload as healthy), but reporting it as an anomaly
            // would page someone for a scale-down.
            if (await workloadLifecycleService.IsIntentionallyDownAsync(tenantId, adapter.RtId))
            {
                Logger.Info(
                    "[{TenantId}] Adapter '{AdapterRtId}' is hibernating and persisted Online; reconciling to Offline",
                    tenantId, adapterRtEntityId);
            }
            else
            {
                Logger.Warn(
                    "[{TenantId}] Adapter '{AdapterRtId}' is persisted Online but has no live SignalR connection on this pod; " +
                    "reconciling to Offline (AB#4699)",
                    tenantId, adapterRtEntityId);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Adapter '{adapterRtEntityId}' had no live connection and was reconciled to Offline.",
                    adapterRtEntityId);
            }

            // The repository write carries an AttributeNewerThanGuard on the state timestamp, so a
            // concurrent Online write with a newer timestamp still wins if it raced past the check
            // above. Offline also resets ConfigurationState to Unconfigured (see the repository),
            // so the badge follows reality.
            await SetAdapterCommunicationStateAsync(tenantId, adapterRtEntityId, RtCommunicationStateEnum.Offline);
            reconciled++;
        }

        return reconciled;
    }

    public async Task DeployAdapterConfigurationAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        Logger.Info("[{TenantId}] AdapterRtId='{AdapterRtId}' deploy configuration",
            tenantId, adapterRtEntityId);

        // AB#4918 wake gate: a hibernated adapter is not in the cache and the push below would
        // throw AdapterNotLoaded. Wake-first (no-op unless the tenant has scale-to-zero on and
        // the adapter is OnDemand); after the wake the adapter has registered and is cached.
        await workloadLifecycleService.EnsureWorkloadRunningAsync(tenantId, adapterRtEntityId.RtId);

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                var configuration =
                    await GetAdapterConfigurationAsync(tenantId, adapterRtEntityId, false);

                // Always push: the user-initiated "Update Configuration" must
                // reach the live adapter pod, even when the controller-side
                // cache matches the persisted DB state. The cache can drift
                // from the pod (e.g. a previous push that threw after we had
                // already optimistically written to the cache, or a pod that
                // restarted without the controller noticing) — gating on
                // cache-equality silently swallows the retry the user
                // explicitly requested via the button.
                await communicationRepository.SetAdapterConfigurationStateAsync(tenantId, adapterRtEntityId,
                    RtConfigurationStateEnum.Pending, null);
                foreach (var pipelineConfigurationDto in configuration.Pipelines)
                {
                    await communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                        pipelineConfigurationDto.PipelineRtEntityId, RtDeploymentStateEnum.Pending, null);
                }

                await SendConfigurationAndWaitForResultAsync(tenantId, adapterRtEntityId, configuration);

                // Update the cache only after the SignalR push succeeded so
                // the cached config tracks the pod's actual state. A failed
                // push throws above and leaves the cache untouched.
                adapter.UpdateConfiguration(tenantId, configuration);

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Configuration deployed to adapter '{adapterRtEntityId}' with {configuration.Pipelines.Count} pipeline(s).",
                    adapterRtEntityId);

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    public async Task DeployPipelineAsync(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId,
        string? pipelineDefinition = null)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}', PipelineRtEntityId='{PipelineRtEntityId}' deploy pipeline configuration",
            tenantId, adapterRtEntityId, pipelineRtEntityId);

        // AB#4918 wake gate — see DeployAdapterConfigurationAsync.
        await workloadLifecycleService.EnsureWorkloadRunningAsync(tenantId, adapterRtEntityId.RtId);

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

                await StoreDeprecatedNodeWarningEventsAsync(tenantId, adapter, pipelineRtEntityId,
                    pipelineDefinition ?? pipeline.PipelineDefinition);

                // Deploying never changes the debug state (AB#4364): the pushed configuration
                // carries the persisted IsDebuggingEnabled as-is, so a pipeline in debug stays
                // in debug across redeploys and a routine deploy (editor, import, adapter move)
                // no longer switches debug capture on silently. Debug is toggled exclusively via
                // SetPipelineDebuggingAsync (PATCH /pipeline/{id}/debug).

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

                // AB#4918 wake gate: a hibernated executing adapter would fail the cache lookup
                // below with AdapterNotLoaded. No-op unless scale-to-zero applies.
                await workloadLifecycleService.EnsureWorkloadRunningAsync(tenantId, rtAdapter);

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

                    await StoreDeprecatedNodeWarningEventsAsync(tenantId, adapter, rtDeployPipeline.ToRtEntityId(),
                        rtDeployPipeline.PipelineDefinition);
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

    public async Task<bool> SetPipelineDebuggingAsync(string tenantId, RtEntityId pipelineRtEntityId, bool isEnabled)
    {
        Logger.Info("[{TenantId}] PipelineRtEntityId='{PipelineRtEntityId}' set debugging to {IsEnabled}",
            tenantId, pipelineRtEntityId, isEnabled);

        // Validate the pipeline exists (throws PipelineNotFound otherwise).
        _ = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId)
            ?? throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);

        // Persist on the RT entity: survives restarts and is honored by the next deploy/execution.
        await communicationRepository.SetPipelineDebuggingEnabledAsync(tenantId, pipelineRtEntityId, isEnabled);

        var dataFlow = await communicationRepository.GetDataFlowByPipelineAsync(tenantId, pipelineRtEntityId.RtId)
                       ?? throw AdapterServiceException.DataFlowNotFound(tenantId, pipelineRtEntityId);

        try
        {
            // Re-push via the data-flow deploy path, which rebuilds config from the persisted entity and
            // honors IsDebuggingEnabled as-is (no force-enable). This makes both enable AND disable effective
            // on the running adapter -- mirroring how Refinery Studio disables debug.
            await DeployDataFlowAsync(tenantId, dataFlow.RtId);
            return true;
        }
        catch (AdapterServiceException e)
        {
            // Owning adapter offline / not loaded: the flag is persisted and applies on the next deploy.
            Logger.Warn(e,
                "[{TenantId}] Pipeline '{PipelineRtEntityId}' debugging persisted to {IsEnabled} but not applied to a live adapter; will apply on next deploy",
                tenantId, pipelineRtEntityId, isEnabled);
            return false;
        }
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
        // AB#4662: debug capture retains per-iteration snapshots on the adapter and must never be
        // active without the operator knowing. Deploy paths preserve the persisted flag as-is
        // (AB#4364), so this single choke point for every configuration push surfaces each
        // debug-enabled pipeline as a Warning event before it starts running in debug silently.
        foreach (var debugPipeline in adapterConfiguration.Pipelines.Where(p => p.IsDebuggingEnabled))
        {
            Logger.Warn(
                "[{TenantId}] Pipeline '{PipelineRtEntityId}' is deployed to adapter '{AdapterRtId}' with debug capture ENABLED",
                tenantId, debugPipeline.PipelineRtEntityId, adapterRtEntityId);
            await eventService.StoreWarningEventAsync(tenantId,
                $"Pipeline '{debugPipeline.PipelineRtEntityId}' is deployed with debug capture enabled. " +
                "Debug capture retains per-iteration snapshots and should be disabled for large runs " +
                "(disable via SetPipelineDebug or the Studio debug button).",
                debugPipeline.PipelineRtEntityId);
        }

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

                    // AB#4918: Configured is the wake readiness signal (AB#4594 — Online is not
                    // enough). Releases wake-gate waiters and moves an OnDemand workload to
                    // Running. Best-effort by contract, never throws.
                    await workloadLifecycleService.NotifyWorkloadConfiguredAsync(tenantId, adapterRtEntityId.RtId);

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
                // Remove all adapters from cache so we skip any communication
                // attempts while the CK-cache is unloaded. The SignalR
                // connections to adapter pods themselves are not torn down by
                // this — they sit on the hub independently of the CK cache.
                adapterCache.RemoveTenant(tenantId);

                // Note: we do NOT touch CommunicationState in the database
                // here. The legacy code marked every adapter Unregistered on
                // the assumption that the cache flush also dropped the
                // SignalR connection — it doesn't. Live adapter pods stayed
                // connected through the nightly tenant pre-update, so any
                // state reset here just produced bogus "Unregistered"
                // entries that flipped back to Online seconds later via the
                // heartbeat. State in DB is authoritative; OnDisconnected
                // / OnConnected callbacks own all CommunicationState writes.

                await eventService.StoreInformationEventAsync(tenantId,
                    $"Tenant pre-update completed. {adapterTenant.AdapterById.Count} adapter(s) flushed from cache.");
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

            // Note: adapter CommunicationState is intentionally NOT reset
            // here — see PreUpdateTenantAsync above for the full rationale.
            // The previous "Offline-if-not-in-cache" loop relied on the
            // adapter cache having an accurate snapshot of currently-
            // connected pods, but PreUpdate had just wiped it; the check
            // therefore always reported "not connected" and reset every
            // adapter's state regardless of its real connection status.

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

    public async Task CkModelChangedAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] CK model changed, notifying adapters to invalidate their CK caches", tenantId);

        try
        {
            await adapterHubCallbacks.CkModelChangedAsync(tenantId);
        }
        catch (Exception e)
        {
            throw AdapterServiceException.CkModelChangedNotificationFailed(tenantId, e);
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

    /// <summary>
    /// Stores a tenant-scoped warning event for every deprecated node type used by a pipeline.
    /// Deprecation is reported by the adapter via its node descriptors
    /// (see <see cref="NodeDescriptorDto.IsDeprecated"/>). Best-effort: never fails the deploy.
    /// </summary>
    private async Task StoreDeprecatedNodeWarningEventsAsync(string tenantId, Adapter adapter,
        RtEntityId pipelineRtEntityId, string? pipelineDefinition)
    {
        if (string.IsNullOrEmpty(pipelineDefinition) || adapter.NodeDescriptors == null) return;

        try
        {
            // TryAdd tolerates duplicate descriptors (same NodeName@Version, incl. casing variants)
            var deprecatedByQualifiedName =
                new Dictionary<string, NodeDescriptorDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in adapter.NodeDescriptors)
            {
                if (!descriptor.IsDeprecated) continue;
                deprecatedByQualifiedName.TryAdd($"{descriptor.NodeName}@{descriptor.Version}", descriptor);
            }

            if (deprecatedByQualifiedName.Count == 0) return;

            var usedNodeTypes = pipelineDefinitionService.GetAllNodes(pipelineDefinition)
                .Select(n => n.NodeType)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var nodeType in usedNodeTypes)
            {
                if (!deprecatedByQualifiedName.TryGetValue(nodeType, out var descriptor)) continue;

                var message = string.IsNullOrEmpty(descriptor.DeprecationMessage)
                    ? $"Pipeline uses deprecated node '{nodeType}'."
                    : $"Pipeline uses deprecated node '{nodeType}': {descriptor.DeprecationMessage}";

                Logger.Warn("[{TenantId}] Pipeline '{PipelineRtEntityId}' uses deprecated node '{NodeType}'",
                    tenantId, pipelineRtEntityId, nodeType);
                await eventService.StoreWarningEventAsync(tenantId, message, pipelineRtEntityId);
            }
        }
        catch (Exception e)
        {
            // Best-effort by contract: a failure to detect or store the warning must never fail the deploy
            Logger.Warn(e,
                "[{TenantId}] Failed to store deprecated-node warning events for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
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
            StatusMessage = a.StatusMessage
        }).ToList();
    }

    public void RecordMetricsSample(string tenantId, AdapterMetricsSampleDto sample)
    {
        if (!adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            // Tenant cache may be transiently absent during enable/disable; drop the
            // sample silently so the SignalR caller is not impacted.
            Logger.Debug("[{TenantId}] Dropping metrics sample for unknown tenant.", tenantId);
            return;
        }

        if (!adapterTenant.AdapterById.TryGetValue(sample.AdapterRtEntityId, out var adapter))
        {
            Logger.Debug("[{TenantId}] Dropping metrics sample for unknown adapter '{AdapterRtId}'.",
                tenantId, sample.AdapterRtEntityId);
            return;
        }

        adapter.AddMetricsSample(sample);
    }

    public IReadOnlyList<AdapterMetricsSampleDto> GetMetricsSamples(string tenantId, RtEntityId adapterRtEntityId,
        DateTime? since)
    {
        if (!adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            throw AdapterServiceException.TenantNotEnabled(tenantId);
        }

        if (!adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
        {
            throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
        }

        return adapter.GetMetricsSamples(since);
    }
}