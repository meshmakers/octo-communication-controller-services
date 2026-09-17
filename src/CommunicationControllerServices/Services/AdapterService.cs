using System.Collections.Concurrent;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Microsoft.AspNetCore.Http;
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
    IWorkloadLifecycleService workloadLifecycleService,
    IWorkloadOnDemandCapabilityService onDemandCapabilityService,
    IPipelineExecutionClassService pipelineExecutionClassService,
    IAdapterNodeCapabilityService adapterNodeCapabilityService,
    IPipelineServiceAccountResolver serviceAccountResolver,
    IWorkloadTemplateResolver templateResolver,
    IIdentityClientReader identityClientReader,
    IHttpContextAccessor httpContextAccessor,
    IOptions<ServiceAccountGuardOptions> serviceAccountGuardOptions)
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

            // AB#4984: registration is the first moment the descriptors (incl.
            // RequiresRunningProcess) are known — persist the computed on-demand
            // capability for the Studio. Best-effort, never fails the registration.
            await onDemandCapabilityService.RefreshWorkloadCapabilityAsync(tenantId, adapterRtEntityId);

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
                        await CreatePipelineConfigurationAsync(tenantId, dataFlow.RtId, adapterRtEntityId.RtId,
                            rtPipeline));
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

    /// <inheritdoc />
    public async Task<PipelineConfigurationDto?> GetLeasedPipelineConfigurationAsync(string tenantId,
        RtEntityId adapterRtEntityId, OctoObjectId pipelineRtId)
    {
        try
        {
            var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId);
            var pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
            if (pipeline is null || string.IsNullOrWhiteSpace(pipeline.PipelineDefinition) || pipeline.Enabled == false)
            {
                Logger.Warn(
                    "[{TenantId}] Pipeline '{PipelineRtId}' cannot be leased out: it does not exist, is disabled or " +
                    "carries no definition",
                    tenantId, pipelineRtId);
                return null;
            }

            // 🔴 The pipeline has to belong to the adapter the lease was granted for. Without this a
            // work item queued against adapter A could be dispatched to a member acting as adapter B
            // in the same tenant - the credential on the lease is B's, so the pipeline would run under
            // an identity its author never chose.
            var adaptersPipelines = await communicationRepository.GetPipelinesAsync(tenantId, adapterRtEntityId);
            if (adaptersPipelines.All(p => p.RtId != pipelineRtId))
            {
                Logger.Warn(
                    "[{TenantId}] Pipeline '{PipelineRtId}' is not deployed to adapter '{AdapterRtEntityId}' and is " +
                    "therefore not leasable through it",
                    tenantId, pipelineRtId, adapterRtEntityId);
                return null;
            }

            var dataFlow = await communicationRepository.GetDataFlowByPipelineAsync(tenantId, pipelineRtId);
            if (dataFlow == null)
            {
                Logger.Warn("[{TenantId}] Data flow for pipeline '{PipelineRtId}' not found; it cannot be leased out",
                    tenantId, pipelineRtId);
                return null;
            }

            return await CreatePipelineConfigurationAsync(tenantId, dataFlow.RtId, adapterRtEntityId.RtId, pipeline);
        }
        catch (Exception e)
        {
            // Never throws into the lease path: a projection that failed must refuse the lease with a
            // named reason, not take down the scheduling round for every other tenant in the rotation.
            Logger.Warn(e,
                "[{TenantId}] Could not project pipeline '{PipelineRtId}' of adapter '{AdapterRtEntityId}' for a lease",
                tenantId, pipelineRtId, adapterRtEntityId);
            return null;
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

        // Whatever took it offline is over (AB#4919).
        WorkloadLifecycleMetrics.RecordOnline(tenantId, adapterRtEntityId.RtId, workloadName: null);
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
            var intentional = await workloadLifecycleService.IsIntentionallyDownAsync(tenantId, adapterRtEntityId.RtId);
            if (intentional)
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

            // Same judgement, published as the metric an alert rule can threshold on (AB#4919).
            WorkloadLifecycleMetrics.RecordOffline(tenantId, adapterRtEntityId.RtId, workloadName: null, intentional);

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
            var intentional = await workloadLifecycleService.IsIntentionallyDownAsync(tenantId, adapter.RtId);
            WorkloadLifecycleMetrics.RecordOffline(tenantId, adapter.RtId, adapter.Name, intentional);
            if (intentional)
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

    /// <summary>
    ///     Deploys a single pipeline to its adapter.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>AB#4924 — this method has two halves, and only the first one is universal.</b>
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             <description>
    ///                 <b>Validate and persist</b> (<see cref="ValidateAndPersistPipelineAsync" />):
    ///                 schema validation, the AB#4984 process-bound-trigger gate, the AB#5027
    ///                 mandatory-service-account gate, the AB#5128 elevation authorization, then the
    ///                 definition, the resolved <c>ExecutionClass</c>, the <c>SendsDataTo</c> sync and
    ///                 the deprecated-node warnings. This half runs for <b>every</b> adapter — a
    ///                 <c>Leased</c> one no less than a dedicated one. It is the reason a leased deploy
    ///                 is a deploy at all: a pipeline that skipped it would be executed by a borrowed
    ///                 process that never checked whether its service account resolves, and never
    ///                 checked whether the caller was entitled to the privilege elevation it asks for.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <b>Push</b>: project the adapter configuration and send it down the adapter's
    ///                 SignalR connection. Skipped for a <c>Leased</c> adapter, because there is
    ///                 nothing to send and nobody to send it to — a leased adapter has no process of
    ///                 its own and never registers on <c>/{tenantId}/adapterHub</c>, and the definition
    ///                 travels on the <c>LeaseDto</c> at execution time (plan §9.9 / D4). Refusing the
    ///                 deploy outright and letting the lease carry an unvalidated definition was
    ///                 considered and rejected: it leaves all four gates above unrun, and one of them
    ///                 is a security control.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public async Task DeployPipelineAsync(string tenantId, RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId,
        string? pipelineDefinition = null)
    {
        Logger.Info(
            "[{TenantId}] AdapterRtId='{AdapterRtId}', PipelineRtEntityId='{PipelineRtEntityId}' deploy pipeline configuration",
            tenantId, adapterRtEntityId, pipelineRtEntityId);

        // Read the workload FIRST. Whether this adapter owns a process decides all three of: is there
        // anything to wake, whose node descriptors answer the validation questions, and is there
        // anybody to push to. Asking the adapter cache first would bake "dedicated" into the shape.
        var workload = await communicationRepository.GetWorkloadByRtIdAsync(tenantId, adapterRtEntityId.RtId);
        var leasedAdapter = workload is RtAdapter { LifecycleMode: RtLifecycleModeEnum.Leased } borrower
            ? borrower
            : null;

        if (leasedAdapter == null)
        {
            // AB#4918 wake gate — see DeployAdapterConfigurationAsync.
            await workloadLifecycleService.EnsureWorkloadRunningAsync(tenantId, adapterRtEntityId.RtId);
        }
        else
        {
            // 🔴 AB#4924: NOT a no-op by accident. EnsureWorkloadRunningAsync would return without
            // doing anything today purely because Leased != OnDemand, which is a coincidence of the
            // mode check rather than a decision. Stated here instead: a Leased adapter has no
            // workload of its own to wake — the pool it borrows from is a separate workload in a
            // different tenant, woken (or scaled) by its own lender, and waking it from a borrower's
            // deploy would let any borrower spin up the lender's capacity by pressing "deploy".
            Logger.Debug(
                "[{TenantId}] Adapter '{AdapterRtId}' is Leased; skipping the wake gate — it has no workload of its own",
                tenantId, adapterRtEntityId);
        }

        // AB#4924: the descriptors and pipeline schema of whatever will actually execute this
        // pipeline — the adapter itself when dedicated, the lending pool's members when leased.
        var capabilities = adapterNodeCapabilityService.Resolve(tenantId, adapterRtEntityId, leasedAdapter);

        if (leasedAdapter != null)
        {
            await DeployLeasedPipelineAsync(tenantId, adapterRtEntityId, pipelineRtEntityId, pipelineDefinition,
                leasedAdapter, capabilities);
            return;
        }

        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant))
        {
            if (adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
            {
                // ---- half 1: validate and persist (shared with the leased path) ----
                var (pipeline, dataFlow) = await ValidateAndPersistPipelineAsync(tenantId, adapterRtEntityId,
                    pipelineRtEntityId, pipelineDefinition, workload, capabilities, leasedAdapter: null);

                // ---- half 2: push (dedicated adapters only) ----

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
                    await CreatePipelineConfigurationAsync(tenantId, dataFlow.RtId, adapterRtEntityId.RtId, pipeline,
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

                // AB#4984: the deployed pipeline set changed — refresh the persisted capability
                await onDemandCapabilityService.RefreshWorkloadCapabilityAsync(tenantId, adapterRtEntityId);

                return;
            }
        }

        throw AdapterServiceException.AdapterNotLoaded(tenantId, adapterRtEntityId);
    }

    /// <summary>
    ///     AB#4924 — the deploy of a pipeline whose adapter borrows its process from a pool.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Runs the whole validate-and-persist half and stops there. The push half is not skipped
    ///         because it is inconvenient here — it is <b>vacuous</b>: the borrower has no adapter-hub
    ///         connection, and the pool member that will run the pipeline is handed the definition on
    ///         the <c>LeaseDto</c> when the lease is granted (plan §9.9 / D4), read fresh from the
    ///         repository by <see cref="GetLeasedPipelineConfigurationAsync" />. Pushing the same
    ///         bytes down a second channel would create a second source of truth for no gain.
    ///     </para>
    ///     <para>
    ///         🔴 <b>The deployment state goes straight to <c>Deployed</c>, never through
    ///         <c>Pending</c>.</b> <c>Pending</c> means "a push is in flight and may still fail"; there
    ///         is no push here, so the state would never leave it. And persisting IS the deployment on
    ///         this path — from the moment the definition and its execution class are written, the very
    ///         next lease runs exactly that. Leaving the pipeline <c>Undeployed</c> would be the
    ///         dishonest answer, not the cautious one: it would claim work is still outstanding when
    ///         there is none, and every surface reads this field to decide what to offer the user. The
    ///         status message names the difference, so nobody reads this <c>Deployed</c> as "a pod
    ///         acknowledged it".
    ///     </para>
    /// </remarks>
    private async Task DeployLeasedPipelineAsync(string tenantId, RtEntityId adapterRtEntityId,
        RtEntityId pipelineRtEntityId, string? pipelineDefinition, RtAdapter leasedAdapter,
        AdapterNodeCapabilities capabilities)
    {
        Logger.Info(
            "[{TenantId}] Adapter '{AdapterRtId}' is Leased from pool {PoolRtId} of tenant '{LenderTenantId}'; " +
            "validating and persisting pipeline '{PipelineRtEntityId}' without a push. Node capabilities: {Source}",
            tenantId, adapterRtEntityId, leasedAdapter.LentFromPoolRtId ?? "<unset>",
            leasedAdapter.LentFromTenantId ?? "<unset>", pipelineRtEntityId, capabilities.Source);

        await ValidateAndPersistPipelineAsync(tenantId, adapterRtEntityId, pipelineRtEntityId, pipelineDefinition,
            leasedAdapter, capabilities, leasedAdapter);

        await communicationRepository.SetPipelineDeploymentStateAsync(tenantId, pipelineRtEntityId,
            RtDeploymentStateEnum.Deployed,
            "Deployed on a leased adapter: the definition is persisted and travels on the lease (AB#4924). " +
            "There is no adapter process to push it to.");

        // AB#4984 / AB#4924: the deployed pipeline set changed. Still meaningful here — "Leased"
        // requires on-demand capability (DeploymentSiteService refuses a leased workload that is not capable),
        // so this is the value that explains a later refusal to deploy the workload.
        await onDemandCapabilityService.RefreshWorkloadCapabilityAsync(tenantId, adapterRtEntityId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Pipeline '{pipelineRtEntityId}' was deployed to leased adapter '{adapterRtEntityId}'. " +
            "Nothing was pushed: the definition travels on the adapter pool lease.",
            adapterRtEntityId);
    }

    /// <summary>
    ///     AB#4924 — the half of a pipeline deploy that runs whatever executes the pipeline: every
    ///     validation gate, then the persistence.
    /// </summary>
    /// <remarks>
    ///     Order is load-bearing and unchanged from the dedicated path: all four gates run BEFORE the
    ///     first state write, so a rejected deploy leaves no half-applied definition behind.
    /// </remarks>
    /// <returns>The pipeline and its data flow, which the push half needs to project a configuration.</returns>
    private async Task<(RtPipeline Pipeline, RtDataFlow DataFlow)> ValidateAndPersistPipelineAsync(string tenantId,
        RtEntityId adapterRtEntityId, RtEntityId pipelineRtEntityId, string? pipelineDefinition,
        RtDeployableWorkload? workload, AdapterNodeCapabilities capabilities, RtAdapter? leasedAdapter)
    {
        if (pipelineDefinition != null)
        {
            ValidatePipelineDefinition(tenantId, adapterRtEntityId, capabilities, pipelineDefinition);
        }

        var pipeline = await communicationRepository.GetPipelineAsync(tenantId, pipelineRtEntityId);
        if (pipeline == null)
        {
            throw AdapterServiceException.PipelineNotFound(tenantId, pipelineRtEntityId);
        }

        var dataFlow = await communicationRepository.GetDataFlowByPipelineAsync(tenantId, pipeline.RtId);
        if (dataFlow == null)
        {
            throw AdapterServiceException.DataFlowNotFound(tenantId, pipelineRtEntityId);
        }

        // AB#4984: deploying a process-bound-trigger pipeline to an OnDemand workload
        // is rejected — hibernation would silently stop the trigger (explicit beats silent).
        // AB#4924 extends the same gate to Leased, where the argument is strictly stronger.
        EnsurePipelineIsOnDemandCompatible(tenantId, workload, capabilities.NodeDescriptors,
            pipelineRtEntityId, pipelineDefinition ?? pipeline.PipelineDefinition);

        // AB#5027: a pipeline must have a resolvable service account before it may run.
        // Like the AB#4984 gate this runs BEFORE the first state write below, so a
        // rejected deploy leaves no half-applied definition behind.
        await EnsurePipelineHasServiceAccountAsync(tenantId, pipelineRtEntityId, adapterRtEntityId.RtId,
            workload?.Name);

        // AB#5128 (Epic AB#4979): authorize privilege elevation. A pipeline running any
        // node under Identity=ServiceAccount/System escalates beyond the caller's rights,
        // so an unauthorized caller is refused here — before the first state write, like
        // the guards above. The confused-deputy lint (advisory) runs on the same walk.
        await EnsurePipelineElevationAuthorizedAsync(tenantId, pipelineRtEntityId,
            pipelineDefinition ?? pipeline.PipelineDefinition);

        // Persist the pipeline definition to the RT entity so it is visible in the UI
        if (pipelineDefinition != null)
        {
            // AB#4924 — resolve the execution class from the definition being saved and
            // write it in the same update. Resolved here rather than in the repository
            // because it needs the live node descriptors of whatever will execute the pipeline,
            // which the repository has no business knowing about.
            var executionClass = pipelineExecutionClassService.ResolveForAdapter(tenantId,
                adapterRtEntityId, pipelineDefinition, leasedAdapter);

            // SetPipelineDefinitionAsync also syncs SendsDataTo associations
            await communicationRepository.SetPipelineDefinitionAsync(tenantId, pipelineRtEntityId,
                pipelineDefinition, executionClass);
        }
        else if (!string.IsNullOrEmpty(pipeline.PipelineDefinition))
        {
            // Sync SendsDataTo associations from existing definition (e.g. after import)
            await communicationRepository.SyncPipelineDataConnectionsAsync(tenantId, pipelineRtEntityId,
                pipeline.PipelineDefinition);

            // AB#4924 — a redeploy without a new definition (MovePipelinesToAdapter, an
            // import) still has to re-resolve: the class depends on the executing process's
            // descriptors as well as the YAML, so moving a pipeline to an adapter running a
            // different SDK — or to a leased adapter borrowing a differently built pool — can
            // legitimately change it. Skipping this is how the persisted class drifts from the
            // definition it claims to describe. A single-field write, NOT a definition rewrite —
            // deploy must not persist a definition it was not given.
            await communicationRepository.SetPipelineExecutionClassAsync(tenantId, pipelineRtEntityId,
                pipelineExecutionClassService.ResolveForAdapter(tenantId, adapterRtEntityId,
                    pipeline.PipelineDefinition, leasedAdapter));
        }

        await StoreDeprecatedNodeWarningEventsAsync(tenantId, capabilities.NodeDescriptors, pipelineRtEntityId,
            pipelineDefinition ?? pipeline.PipelineDefinition);

        return (pipeline, dataFlow);
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

                        // AB#5138: seed the new configuration ONCE per adapter — every already
                        // deployed pipeline of OTHER data flows is kept, this data flow's entries
                        // are dropped so the loop below re-adds their fresh versions. This used to
                        // run once per pipeline of the data flow, and because
                        // PipelineConfigurationDto has value equality the repeated
                        // Remove(oldEntry) also removed the fresh entries added in earlier
                        // iterations whenever a sibling redeployed UNCHANGED — a multi-pipeline
                        // data flow deploy (or SetPipelineDebugging, which deploys the whole data
                        // flow) then silently undeployed every sibling except the last one
                        // processed, tearing down their FromPipelineDataEvent consumers.
                        foreach (var deployedPipelineConfigurationDto in adapter.Configuration.Pipelines)
                        {
                            if (deployedPipelineConfigurationDto.DataFlowRtId != dataFlowRtId)
                            {
                                adapterConfig.Pipelines.Add(deployedPipelineConfigurationDto);
                            }
                        }
                    }

                    // AB#4984: reject process-bound-trigger pipelines on OnDemand workloads
                    EnsurePipelineIsOnDemandCompatible(tenantId, rtAdapter, adapter.NodeDescriptors,
                        rtDeployPipeline.ToRtEntityId(), rtDeployPipeline.PipelineDefinition);

                    // AB#5027: mandatory identity. Runs before UpdateAdapterConfigurationAsync
                    // below (the first state write of this path), so one identity-less pipeline
                    // aborts the whole data-flow deploy instead of half-applying it.
                    await EnsurePipelineHasServiceAccountAsync(tenantId, rtDeployPipeline.ToRtEntityId(),
                        rtAdapter.RtId, rtAdapter.Name);

                    // AB#5128: authorize elevation per pipeline — a data flow deploys many, and
                    // one un-authorized elevated pipeline aborts the whole deploy before any write.
                    await EnsurePipelineElevationAuthorizedAsync(tenantId, rtDeployPipeline.ToRtEntityId(),
                        rtDeployPipeline.PipelineDefinition);

                    adapterConfig.Pipelines.Add(
                        await CreatePipelineConfigurationAsync(tenantId, dataFlowRtId, rtAdapter.RtId,
                            rtDeployPipeline));

                    await StoreDeprecatedNodeWarningEventsAsync(tenantId, adapter.NodeDescriptors,
                        rtDeployPipeline.ToRtEntityId(),
                        rtDeployPipeline.PipelineDefinition);
                }
                else
                {
                    throw AdapterServiceException.AdapterNotLoaded(tenantId, rtAdapter.ToRtEntityId());
                }
            }

            await UpdateAdapterConfigurationAsync(tenantId, adapterConfigurations.Values.ToList());

            // AB#4984: the deployed pipeline sets changed — refresh the persisted capability
            foreach (var configuredAdapterRtEntityId in adapterConfigurations.Keys)
            {
                await onDemandCapabilityService.RefreshWorkloadCapabilityAsync(tenantId, configuredAdapterRtEntityId);
            }

            return;
        }

        throw AdapterServiceException.TenantNotEnabled(tenantId);
    }

    /// <summary>
    /// AB#4984: rejects deploying a pipeline whose triggers are process-bound (would silently
    /// stop at 0 replicas) to a workload with LifecycleMode=OnDemand. No-op for AlwaysOn
    /// workloads or when the workload entity cannot be resolved.
    ///
    /// <para>
    /// 🔴 AB#4924 extends the same gate to <c>Leased</c>, and the argument there is strictly
    /// stronger: an OnDemand workload has a process that is merely allowed to hibernate, while a
    /// Leased adapter has no process of its own <b>at all</b> — it is handed one BETWEEN work items
    /// and gives it back. A trigger that only fires while a process of its own is running can
    /// therefore never fire on a leased adapter. The same guard is applied at workload-deploy time
    /// by <c>DeploymentSiteService.EnsureLeasingConfigurationIsValidAsync</c>, over the adapter's whole
    /// pipeline set; without this per-pipeline arm a NEW process-bound pipeline could be deployed
    /// to an already deployed leased adapter and would only be caught at the next workload deploy —
    /// i.e. it would sit in the queue and never run. Deliberately reuses
    /// <c>GetProcessBoundNodes</c> rather than duplicating the trigger list, so the two modes can
    /// never drift apart.
    /// </para>
    /// </summary>
    private void EnsurePipelineIsOnDemandCompatible(string tenantId, RtDeployableWorkload? workload,
        IReadOnlyList<NodeDescriptorDto>? nodeDescriptors, RtEntityId pipelineRtEntityId, string? pipelineDefinition)
    {
        if (workload is not { LifecycleMode: RtLifecycleModeEnum.OnDemand or RtLifecycleModeEnum.Leased })
        {
            return;
        }

        var processBoundNodes = onDemandCapabilityService.GetProcessBoundNodes(pipelineDefinition, nodeDescriptors);
        if (processBoundNodes.Count > 0)
        {
            throw workload.LifecycleMode == RtLifecycleModeEnum.Leased
                ? AdapterServiceException.PipelineNotLeasable(tenantId, pipelineRtEntityId, workload.Name,
                    processBoundNodes)
                : AdapterServiceException.PipelineNotOnDemandCompatible(tenantId, pipelineRtEntityId,
                    workload.Name, processBoundNodes);
        }
    }

    /// <summary>
    /// AB#5027 (Epic AB#4979): refuses to deploy a pipeline that has no resolvable service
    /// account. Every mesh adapter must have one linked (adapter-wide default), a single
    /// pipeline may override it — but "no identity at all" is never accepted. The obligation
    /// lives here rather than in the CK multiplicity so that existing Adapter entities keep
    /// importing while the provisioning phase is still outstanding.
    /// Always call this BEFORE the first state write of a deploy path.
    ///
    /// <para>
    /// AB#5112 hardens the guard beyond mere resolvability: a resolved configuration without a
    /// usable client secret is refused when the AB#5114 impersonation path cannot stand in either
    /// (no adapter-own client with a usable secret — with both credentials absent such a pipeline
    /// could never authenticate; both are local facts of the tenant's entities), and the identity
    /// client's existence is verified against the identity service
    /// when <see cref="ServiceAccountGuardOptions.CheckIdentityClient" /> allows it (default on;
    /// the per-environment off switch exists for rollouts that outpace the tenant sweep). 🔴 An
    /// <b>unanswerable</b> identity lookup — identity down, or no caller token to ask with — is
    /// deliberately NON-blocking: it logs a warning and lets the deploy proceed, because identity
    /// downtime must not brick pipeline deploys, and the adapter-side token request will surface a
    /// genuinely missing client immediately anyway.
    /// </para>
    /// </summary>
    private async Task EnsurePipelineHasServiceAccountAsync(string tenantId, RtEntityId pipelineRtEntityId,
        OctoObjectId adapterRtId, string? adapterName)
    {
        var resolution = await serviceAccountResolver.ResolveAsync(tenantId, pipelineRtEntityId.RtId, adapterRtId);
        if (!resolution.IsResolved)
        {
            throw AdapterServiceException.PipelineHasNoServiceAccount(tenantId, pipelineRtEntityId, adapterRtId,
                adapterName);
        }

        var serviceAccount = resolution.ServiceAccount!;
        var wellKnownName = serviceAccount.RtWellKnownName ?? serviceAccount.RtId.ToString();

        // Defensive reads, same reasoning as the whole service-account path (and ClientId is still
        // mandatory on the CK type since 3.33.0, so its generated getter throws on exactly the
        // half-configured entity this guard exists to refuse with a useful message instead).
        var secret = serviceAccount.GetAttributeValueOrDefault(
            nameof(RtServiceAccountConfiguration.ClientSecret)) as string;
        var serviceAccountClientId = serviceAccount.GetAttributeValueOrDefault(
            nameof(RtServiceAccountConfiguration.ClientId)) as string;

        // AB#5114 credential-aware refusal: a configuration without a usable secret (empty, or an
        // angle-bracket seed placeholder) is refused ONLY when impersonation cannot stand in — the
        // adapter's own client (AB#5072: its default pipeline service account, whose credentials
        // travel in the pod's Helm values) must exist with a usable secret and be a different
        // client than the account itself. The MayActAs edge that ultimately authorizes the
        // impersonation lives identity-side and is NOT verifiable through the identity REST
        // surface the controller reads (no client-association endpoint) — best-effort like the
        // client-existence check below, so its absence never blocks a deploy here; the identity
        // token endpoint refuses the impersonation request if it is missing, and the reconcile
        // materialises it.
        if (!PipelineServiceAccountProvisioningService.IsSecretUsable(secret))
        {
            var actorClientId = string.IsNullOrWhiteSpace(serviceAccountClientId)
                ? null
                : await TryGetImpersonationActorClientIdAsync(tenantId, adapterRtId, serviceAccountClientId!);
            if (actorClientId == null)
            {
                throw AdapterServiceException.PipelineServiceAccountSecretMissing(tenantId, pipelineRtEntityId,
                    wellKnownName, adapterRtId, adapterName);
            }

            Logger.Info(
                "[{TenantId}] Pipeline '{PipelineRtEntityId}' deploys on the impersonation path (AB#5114): service account '{WellKnownName}' has no usable secret; adapter client '{ActorClientId}' will request its identity. The MayActAs edge cannot be verified from here — identity refuses the token request if it is missing.",
                tenantId, pipelineRtEntityId, wellKnownName, actorClientId);
        }

        if (serviceAccountGuardOptions.Value.CheckIdentityClient)
        {
            var clientId = serviceAccountClientId;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                // No client id means no identity client can exist — the same violation, established
                // without asking identity.
                throw AdapterServiceException.PipelineServiceAccountClientMissing(tenantId, pipelineRtEntityId,
                    wellKnownName, clientId: null, adapterRtId, adapterName);
            }

            var lookup = await identityClientReader.GetClientAsync(tenantId, clientId!, includeRoles: false);
            switch (lookup.Status)
            {
                case IdentityClientLookupStatus.NotFound:
                    throw AdapterServiceException.PipelineServiceAccountClientMissing(tenantId, pipelineRtEntityId,
                        wellKnownName, clientId, adapterRtId, adapterName);

                case IdentityClientLookupStatus.Unavailable:
                    // Non-blocking by design (see the method remarks). The reason never carries a secret.
                    Logger.Warn(
                        "[{TenantId}] Deploying pipeline '{PipelineRtEntityId}' without verifying identity client '{ClientId}' of service account '{WellKnownName}': {Reason}",
                        tenantId, pipelineRtEntityId, clientId, wellKnownName,
                        lookup.UnavailableReason ?? "the identity service could not be queried");
                    break;
            }
        }

        Logger.Debug(
            "[{TenantId}] Pipeline '{PipelineRtEntityId}' executes as service account '{ServiceAccount}' (source: {Source})",
            tenantId, pipelineRtEntityId, resolution.ServiceAccount!.RtWellKnownName ?? resolution.ServiceAccount.RtId.ToString(),
            resolution.Source);
    }

    /// <summary>
    /// AB#5128 (Epic AB#4979): authorizes privilege elevation at deploy time and runs the
    /// confused-deputy lint. Setting any data node to <c>Identity=ServiceAccount</c> or
    /// <c>System</c> (AB#5127) is an escalation — the node executes with the service account's full
    /// roles, or unfiltered as the system context, even when a caller principal is present. When
    /// the pipeline being deployed contains at least one such node:
    /// <list type="bullet">
    ///   <item>
    ///     <b>Blocking gate</b> (behind <see cref="ServiceAccountGuardOptions.CheckElevation" />,
    ///     default on): the caller must hold <see cref="CommonConstants.UserManagementRole" /> —
    ///     the same role the AB#5111 service-account reconcile is gated on, elevation being at
    ///     least as powerful. A system-initiated deploy (no HTTP caller principal, e.g. the
    ///     post-provisioning re-deploy) is allowed, but logged. An unauthorized caller is refused
    ///     naming every offending node.
    ///   </item>
    ///   <item>
    ///     <b>Advisory lint</b> (always, independent of the flag): warns — never refuses — when an
    ///     elevated node's target-selecting input reads a raw caller-controlled path
    ///     (<c>$.body</c>/<c>$.query</c>/<c>$.files</c>/<c>$.headers</c>). The caller may TRIGGER
    ///     the elevated op but must not silently STEER it (confused deputy).
    ///   </item>
    /// </list>
    /// Always call this BEFORE the first state write of a deploy path.
    /// </summary>
    private async Task EnsurePipelineElevationAuthorizedAsync(string tenantId, RtEntityId pipelineRtEntityId,
        string? pipelineDefinition)
    {
        if (string.IsNullOrEmpty(pipelineDefinition))
        {
            return;
        }

        // A definition that does not parse is not this guard's concern — schema validation and the
        // AB#5113 rights analysis report it. Treat "unparsable" as "nothing to authorize".
        if (!pipelineDefinitionService.TryGetAllNodes(pipelineDefinition, out var nodes))
        {
            return;
        }

        var elevatedNodes = PipelineElevationInspector.FindElevatedNodes(nodes);
        if (elevatedNodes.Count == 0)
        {
            return;
        }

        // Blocking authorization (gated). The lint below still runs when this is disabled.
        if (serviceAccountGuardOptions.Value.CheckElevation)
        {
            var caller = httpContextAccessor.HttpContext?.User;
            if (caller == null)
            {
                // System-initiated deploy: no HTTP caller principal to authorize (e.g. the
                // post-provisioning re-deploy). Allowed by design — same as the AB#5111 reconcile
                // system path — but logged, because an elevation still happened.
                Logger.Info(
                    "[{TenantId}] Pipeline '{PipelineRtEntityId}' deploys with elevated node(s) {ElevatedNodes} on a system-initiated path (no caller principal) — elevation authorization skipped (AB#5128)",
                    tenantId, pipelineRtEntityId, string.Join(", ", elevatedNodes.Select(n => n.Label)));
            }
            else if (!caller.HasRole(CommonConstants.UserManagementRole))
            {
                Logger.Warn(
                    "[{TenantId}] Refusing to deploy pipeline '{PipelineRtEntityId}': caller lacks '{Role}' but the pipeline elevates in node(s) {ElevatedNodes} (AB#5128)",
                    tenantId, pipelineRtEntityId, CommonConstants.UserManagementRole,
                    string.Join(", ", elevatedNodes.Select(n => n.Label)));
                throw AdapterServiceException.PipelineElevationNotAuthorized(tenantId, pipelineRtEntityId,
                    elevatedNodes.Select(n => n.Label).ToList(), CommonConstants.UserManagementRole);
            }
            else
            {
                Logger.Info(
                    "[{TenantId}] Authorized elevated deploy of pipeline '{PipelineRtEntityId}' (node(s) {ElevatedNodes}) for caller holding '{Role}' (AB#5128)",
                    tenantId, pipelineRtEntityId, string.Join(", ", elevatedNodes.Select(n => n.Label)),
                    CommonConstants.UserManagementRole);
            }
        }

        await LintElevationConfusedDeputyAsync(tenantId, pipelineRtEntityId, nodes);
    }

    /// <summary>
    /// AB#5128 confused-deputy lint: advisory, best-effort, never fails the deploy. Emits one
    /// warning per elevated node whose target-selecting input reads a raw caller-controlled path,
    /// into the tenant event log (the deploy result surface) and the service log. A pipeline author
    /// may do this deliberately — the point is that they must SEE it.
    /// </summary>
    private async Task LintElevationConfusedDeputyAsync(string tenantId, RtEntityId pipelineRtEntityId,
        IReadOnlyList<PipelineNodeProperties> nodes)
    {
        try
        {
            var findings = PipelineElevationInspector.FindConfusedDeputyHazards(nodes);
            foreach (var finding in findings)
            {
                var message =
                    $"Confused-deputy hazard: elevated node '{finding.NodeLabel}' takes its target from the " +
                    $"caller-controlled path '{finding.CallerControlledPath}' (property '{finding.PropertyName}'). " +
                    "The caller can trigger this elevated operation but should not steer which entity it acts on. " +
                    "If this is intentional, ignore this warning; otherwise pin the target to a constant or a " +
                    "value computed under the pipeline's control.";
                Logger.Warn(
                    "[{TenantId}] Pipeline '{PipelineRtEntityId}' confused-deputy hazard: elevated node '{NodeLabel}' property '{Property}' reads caller-controlled path '{Path}' (AB#5128)",
                    tenantId, pipelineRtEntityId, finding.NodeLabel, finding.PropertyName, finding.CallerControlledPath);
                await eventService.StoreWarningEventAsync(tenantId, message, pipelineRtEntityId);
            }
        }
        catch (Exception e)
        {
            // Advisory by contract: a failure to detect or store the warning must never fail the deploy.
            Logger.Warn(e,
                "[{TenantId}] Failed to run the confused-deputy lint for pipeline '{PipelineRtEntityId}'",
                tenantId, pipelineRtEntityId);
        }
    }

    /// <summary>
    /// AB#5114: the adapter's own client, as far as the deploy guard can establish it — the
    /// adapter's default pipeline service account with a usable secret (exactly what AB#5072
    /// projects into the pod as its own credentials), and a different client than the account
    /// being deployed (a client cannot impersonate itself; in particular, the adapter default
    /// itself without a secret leaves the adapter with no credentials at all). Returns <c>null</c>
    /// when no such actor exists <b>or the lookup fails</b> — a guard must fail closed on its
    /// local facts, and the actionable repair (reconcile the adapter) is named in the refusal.
    /// </summary>
    private async Task<string?> TryGetImpersonationActorClientIdAsync(string tenantId, OctoObjectId adapterRtId,
        string targetClientId)
    {
        try
        {
            var adapterDefault = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapterRtId);
            var actorClientId = adapterDefault?.GetAttributeValueOrDefault(
                nameof(RtServiceAccountConfiguration.ClientId)) as string;
            var actorSecret = adapterDefault?.GetAttributeValueOrDefault(
                nameof(RtServiceAccountConfiguration.ClientSecret)) as string;

            if (string.IsNullOrWhiteSpace(actorClientId) || actorClientId == targetClientId ||
                !PipelineServiceAccountProvisioningService.IsSecretUsable(actorSecret))
            {
                return null;
            }

            return actorClientId;
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{TenantId}] Could not read the own service account of adapter '{AdapterRtId}' while judging the impersonation path (AB#5114)",
                tenantId, adapterRtId);
            return null;
        }
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

            // AB#4984: the deployed pipeline sets changed — refresh the persisted capability
            foreach (var configuredAdapterRtEntityId in adapterConfigurations.Keys)
            {
                await onDemandCapabilityService.RefreshWorkloadCapabilityAsync(tenantId, configuredAdapterRtEntityId);
            }

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

    /// <summary>
    /// Builds the per-pipeline configuration the adapter receives.
    ///
    /// AB#5027 projection: configurations reach a pipeline exclusively through that pipeline's
    /// own <c>Uses</c> edges — the adapter materialises the list into a per-pipeline
    /// <c>GlobalConfiguration</c> dictionary keyed by <c>RtWellKnownName</c>, and there is no
    /// adapter-level configuration scope. So the adapter-wide default service account has to be
    /// mixed into this list controller-side, or the pipeline could never read it. Done here on
    /// purpose: no SDK/wire change, no new DTO shape, hence no version skew between adapter and
    /// controller.
    ///
    /// NOTE: the adapter caches <c>GlobalConfiguration</c> when the pipeline is registered, so a
    /// change to the linked service account only takes effect after the pipeline / data flow is
    /// redeployed.
    /// </summary>
    private async Task<PipelineConfigurationDto> CreatePipelineConfigurationAsync(string tenantId,
        OctoObjectId dataFlowRtId, OctoObjectId adapterRtId, RtPipeline rtPipeline,
        string? pipelineDefinition = null)
    {
        var pipelineConfigurations = (await communicationRepository
            .GetConfigurationsByPipelineAsync(tenantId, rtPipeline.RtId)).ToList();

        // Only when the pipeline has no service account of its own: an explicit per-pipeline
        // override is already in the list and must win untouched.
        //
        // NOT OfType: GetConfigurationsByPipelineAsync materialises the Uses targets as the
        // requested base RtConfiguration (the Mongo discriminator is the generic "RtEntity"),
        // so a type test never matches a loaded override — it matched only the typed instances
        // unit tests hand in. Found live on AB#5111's first delegated run: the override WAS in
        // the list, yet the adapter default was injected and the issuer token below stayed
        // unresolved. Detect by CkTypeId instead.
        if (pipelineConfigurations.Any(IsServiceAccountConfiguration) == false)
        {
            var adapterServiceAccount = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapterRtId);
            if (adapterServiceAccount != null &&
                // Never insert the same well-known name twice — it is the dictionary key on the
                // adapter side, and a duplicate would throw there.
                pipelineConfigurations.All(c => c.RtId != adapterServiceAccount.RtId &&
                                                c.RtWellKnownName != adapterServiceAccount.RtWellKnownName))
            {
                pipelineConfigurations.Add(adapterServiceAccount);
            }
        }

        // AB#5145: the tenant's SignalChannel singleton is always projected — no Uses edge needed.
        await AddSignalChannelConfigurationAsync(tenantId, pipelineConfigurations);

        // AB#5111: resolve the IssuerUri deploy-time token before the configuration is serialised
        // for the adapter — the projection is the consumption point of the entity, so this is
        // where {{service.authority}} has to become a concrete URL.
        foreach (var serviceAccount in pipelineConfigurations.Where(IsServiceAccountConfiguration))
        {
            ResolveServiceAccountIssuerUri(tenantId, serviceAccount);
        }

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

    /// <summary>
    ///     AB#5145 — the cross-repo contract for the Signal channel nodes (this doc is the one
    ///     authoritative place; the adapter side references it from
    ///     <c>SignalChannelEndpointResolver</c> in octo-mesh-adapter).
    ///
    ///     <para>
    ///     The tenant's <c>System.Communication/SignalChannel</c> singleton (the AB#5143
    ///     self-service entity, created with <c>RtWellKnownName</c> <c>"signal-channel"</c> —
    ///     <see cref="SignalChannelService.ChannelWellKnownName" />) is ALWAYS projected into every
    ///     pipeline's configuration list, no <c>Uses</c> association required. The adapter
    ///     materialises the list into its per-pipeline <c>GlobalConfiguration</c> dictionary keyed
    ///     by <c>RtWellKnownName</c>, so the Signal nodes (<c>FromSignal@1</c> /
    ///     <c>SignalSender@1</c>) look the entry up under the key <c>"signal-channel"</c> and read
    ///     <c>Number</c> / <c>ApiUrl</c> from its serialized <c>attributes</c> — but only act on it
    ///     while <c>RegistrationState == Registered (2)</c>. The entity is shipped in EVERY state
    ///     on purpose: only the shipped state lets the node distinguish "present but not
    ///     registered" (warn precisely) from "no channel at all" (stay idle quietly).
    ///     </para>
    ///
    ///     <para>
    ///     Guards: no SignalChannel → nothing added, no error. A configuration with the same rtId
    ///     or well-known name already in the list (e.g. a legacy <c>Uses</c> edge to the channel)
    ///     is never duplicated — the well-known name is the adapter-side dictionary key and a
    ///     duplicate would throw there. A repository failure is logged and skipped rather than
    ///     failing the deploy: the channel projection is an optional enrichment, and a tenant
    ///     whose CK model predates 3.34.0 (no SignalChannel type) must keep deploying.
    ///     </para>
    ///
    ///     NOTE: like every projected configuration, the adapter caches it at pipeline
    ///     registration — a channel registered later reaches the nodes on the next redeploy of the
    ///     pipeline / data flow (or an adapter restart).
    /// </summary>
    private async Task AddSignalChannelConfigurationAsync(string tenantId,
        List<RtConfiguration> pipelineConfigurations)
    {
        IReadOnlyCollection<RtSignalChannel> channels;
        try
        {
            channels = await communicationRepository.GetSignalChannelsAsync(tenantId);
        }
        catch (Exception e)
        {
            Logger.Warn(e,
                "[{TenantId}] Could not read the tenant's SignalChannel for the pipeline configuration projection; the configuration is built without it",
                tenantId);
            return;
        }

        if (channels.Count == 0)
        {
            return;
        }

        // The service layer enforces the singleton; break a hand-crafted tie deterministically so
        // every pod and every redeploy agrees.
        var channel = channels.OrderBy(c => c.RtId.ToString(), StringComparer.Ordinal).First();
        if (channels.Count > 1)
        {
            Logger.Warn(
                "[{TenantId}] Tenant carries {Count} SignalChannel definitions; projecting '{RtId}'. The channel is a singleton — remove the others.",
                tenantId, channels.Count, channel.RtId);
        }

        if (string.IsNullOrWhiteSpace(channel.RtWellKnownName))
        {
            // Never produced by the self-service; a hand-crafted entity without the key would fail
            // the whole deploy at the ConfigurationDto guard below — skip it instead.
            Logger.Warn(
                "[{TenantId}] SignalChannel '{RtId}' carries no RtWellKnownName and is not projected",
                tenantId, channel.RtId);
            return;
        }

        if (pipelineConfigurations.Any(c => c.RtId == channel.RtId ||
                                            c.RtWellKnownName == channel.RtWellKnownName))
        {
            // Already reachable through the pipeline's own Uses edges — never insert the same
            // well-known name twice (adapter-side dictionary key).
            return;
        }

        pipelineConfigurations.Add(channel);
    }

    /// <summary>
    /// AB#5111: resolves deploy-time tokens in a service account's <c>IssuerUri</c> — legacy
    /// entities may still carry <c>{{service.authority}}</c> (the 3.32.0 default), and an adapter
    /// that pre-dates AB#5115 needs a concrete URL to run OIDC discovery against. An <b>empty</b>
    /// (or absent) IssuerUri — the AB#5115 canonical value — passes through EMPTY untouched: it
    /// means "the adapter's own installation", and the adapter resolves it against its own
    /// authority configuration.
    ///
    /// <para>
    /// REUSES the existing workload-template machinery (<see cref="IWorkloadTemplateResolver" />,
    /// the same one that resolves <c>{{service.NAME}}</c> / <c>{{domain.NAME}}</c> in Hostname,
    /// ValueOverrides and ValuesYaml at deploy time) instead of inventing a second token syntax.
    /// One deliberate extra: when <c>ServiceUrls</c> has no <c>authority</c> entry (local dev,
    /// clusters whose helm chart predates the map), <c>{{service.authority}}</c> alone falls back
    /// to this instance's <c>AuthorityUrl</c> — the value the provisioning wrote before AB#5111 —
    /// so the token can never resolve to less than the old hard-coded behaviour.
    /// </para>
    ///
    /// <para>
    /// Mutates only the in-memory entity that is about to be serialised into the
    /// <c>PipelineConfigurationDto</c>; nothing is written back to the repository — the persisted
    /// entity keeps the portable token. An unresolvable value (unknown placeholder, no fallback)
    /// is passed through verbatim with a warning rather than failing the deploy: a deploy that
    /// worked yesterday must not start failing because a template map shrank, and the adapter-side
    /// token request will name the failing issuer clearly.
    /// </para>
    /// </summary>
    /// <summary>
    ///     A service-account configuration regardless of how it was materialised: the typed
    ///     instance the resolver/provisioning builds, or the base-typed entity the generic
    ///     Uses-association load returns (see the CkTypeId rationale at the override check).
    /// </summary>
    private static bool IsServiceAccountConfiguration(RtConfiguration configuration)
    {
        return configuration is RtServiceAccountConfiguration ||
               SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId.Equals(configuration.CkTypeId);
    }

    private void ResolveServiceAccountIssuerUri(string tenantId, RtConfiguration serviceAccount)
    {
        // Defensive read, same reasoning as everywhere on the service-account path: IssuerUri is
        // mandatory on the CK type, and the generated getter throws on a half-written entity.
        var issuerUri = serviceAccount.GetAttributeValueOrDefault(
            nameof(RtServiceAccountConfiguration.IssuerUri)) as string;
        if (issuerUri == null || issuerUri.IndexOf("{{", StringComparison.Ordinal) < 0)
        {
            return;
        }

        if (templateResolver.TryResolve(issuerUri, new WorkloadTemplateContext(tenantId), out var resolved,
                out var unknownPlaceholder))
        {
            // resolved is non-null whenever the input was non-null and TryResolve returned true.
            serviceAccount.SetAttributeValue(nameof(RtServiceAccountConfiguration.IssuerUri),
                AttributeValueTypesDto.String, resolved!);
            return;
        }

        if (string.Equals(unknownPlaceholder, "service.authority", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(issuerUri.Trim(), "{{service.authority}}", StringComparison.OrdinalIgnoreCase))
        {
            // No configured authority URL — fall back to the one this instance authenticates
            // against itself, which is exactly what the provisioning wrote before AB#5111. Only for
            // the bare token: a composite template with an unresolvable part is a configuration
            // error and falls through to the warning below.
            serviceAccount.SetAttributeValue(nameof(RtServiceAccountConfiguration.IssuerUri),
                AttributeValueTypesDto.String, communicationControllerOptions.Value.AuthorityUrl);
            return;
        }

        Logger.Warn(
            "[{TenantId}] IssuerUri of service account '{WellKnownName}' references the unknown placeholder '{Placeholder}'; the value is passed to the adapter unresolved",
            tenantId, serviceAccount.RtWellKnownName, unknownPlaceholder);
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
    private async Task StoreDeprecatedNodeWarningEventsAsync(string tenantId,
        IReadOnlyList<NodeDescriptorDto>? nodeDescriptors,
        RtEntityId pipelineRtEntityId, string? pipelineDefinition)
    {
        if (string.IsNullOrEmpty(pipelineDefinition) || nodeDescriptors == null) return;

        try
        {
            // TryAdd tolerates duplicate descriptors (same NodeName@Version, incl. casing variants)
            var deprecatedByQualifiedName =
                new Dictionary<string, NodeDescriptorDto>(StringComparer.OrdinalIgnoreCase);
            foreach (var descriptor in nodeDescriptors)
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

    /// <summary>
    ///     Validates a definition against the pipeline schema of whatever will execute it (AB#4924:
    ///     the adapter itself when dedicated, the lending pool's members when leased). A leased
    ///     pipeline used to be validated against no schema at all, which is strictly worse than
    ///     validating it against the pool's — the pool is the process that has to parse it.
    /// </summary>
    private void ValidatePipelineDefinition(string tenantId, RtEntityId adapterRtEntityId,
        AdapterNodeCapabilities capabilities, string pipelineDefinition)
    {
        if (!communicationControllerOptions.Value.EnablePipelineSchemaValidation) return;
        if (capabilities.PipelineSchemaJson == null) return;

        var errors = pipelineSchemaValidator.Validate(pipelineDefinition, capabilities.PipelineSchemaJson);
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