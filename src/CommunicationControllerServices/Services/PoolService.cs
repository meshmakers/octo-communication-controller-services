using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PoolService : IPoolService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPoolCache _poolCache;
    private readonly ICommunicationEventService _eventService;
    private readonly IOperatorConnectionManager _operatorConnectionManager;
    private readonly IWorkloadEncryptionService _encryptionService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Communication repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="eventService">Service for storing system events</param>
    /// <param name="operatorConnectionManager">Manages SignalR connections to central Communication Operators (for Cloud-pool deploy/undeploy notifications and PreUpdateTenant fan-out)</param>
    /// <param name="encryptionService">Decrypts secret-flagged ValueOverride values before they go on the SignalR wire</param>
    public PoolService(ICommunicationRepository communicationRepository, IPoolCache poolCache,
        ICommunicationEventService eventService,
        IOperatorConnectionManager operatorConnectionManager,
        IWorkloadEncryptionService encryptionService)
    {
        _communicationRepository = communicationRepository;
        _poolCache = poolCache;
        _eventService = eventService;
        _operatorConnectionManager = operatorConnectionManager;
        _encryptionService = encryptionService;
    }
    
    /// <inheritdoc />
    public async Task<OctoObjectId> RegisterPoolOperatorAsync(string tenantId, string poolName, string connectionId)
    {
        Logger.Info("[{TenantId}] Registering operator for pool '{PoolName}'",
            tenantId, poolName);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            Logger.Info("[{TenantId}] Pool '{PoolName}' already registered",
                tenantId, poolName);

            poolDescription.UpdateConnectionId(tenantId, connectionId);
        }
        else
        {
            var poolList = await _communicationRepository.GetPoolByNameAsync(tenantId, poolName);
            var rtPool = poolList.FirstOrDefault();
            if (rtPool == null)
            {
                Logger.Info("[{TenantId}] Creating pool '{PoolName}'",
                    tenantId, poolName);
                await _communicationRepository.CreatePoolAsync(tenantId, poolName);

                poolList = await _communicationRepository.GetPoolByNameAsync(tenantId, poolName);
                rtPool = poolList.FirstOrDefault();

                if (rtPool == null)
                {
                    throw PoolServiceException.CannotCreatePool(tenantId, poolName);
                }
            }

            poolDescription = poolTenant.AddPool(poolName, rtPool.RtId, connectionId);
        }

        // Update status in asset repository
        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
            RtDeploymentStateEnum.Deployed);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool operator for pool '{poolName}' registered with connection id '{connectionId}'.",
            new RtEntityId(SystemCommunicationCkIds.RtCkPoolTypeId, poolDescription.PoolRtId));

        Logger.Info("[{TenantId}] Operator for pool '{PoolName}' registered",
            tenantId, poolName);
        return poolDescription.PoolRtId;
    }

    /// <inheritdoc />
    public async Task UnregisterPoolOperatorAsync(string tenantId, string poolName)
    {
        Logger.Info("[{TenantId}] Unregistering operator for pool '{PoolName}'",
            tenantId, poolName);

        if (_poolCache.TryGetTenant(tenantId, out var tenantDescription))
        {
            if (tenantDescription.PoolsByName.TryGetValue(poolName, out var poolDescription))
            {
                // Set communication state to Unregistered *before* removing from cache.
                // After RemovePool, the OnDisconnectedAsync that follows the operator's
                // graceful disconnect can no longer locate the pool, so any state write
                // would silently no-op and the UI would keep showing Online forever.
                await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                    RtCommunicationStateEnum.Unregistered);

                tenantDescription.RemovePool(poolDescription.PoolRtId);

                await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
                    RtDeploymentStateEnum.Pending);

                await _eventService.StoreInformationEventAsync(tenantId,
                    $"Pool operator for pool '{poolName}' unregistered.",
                    new RtEntityId(SystemCommunicationCkIds.RtCkPoolTypeId, poolDescription.PoolRtId));

                Logger.Info("[{TenantId}] Operator for pool '{PoolName}' unregistered",
                    tenantId, poolName);
            }
        }
    }

    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task PreUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PreUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();

            if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
            {
                // Inform all connected operators that the tenant is about to
                // be updated. Replaces the per-pool /poolHub fan-out — every
                // operator multiplexes through its single /operatorHub channel.
                await _operatorConnectionManager.NotifyPreUpdateTenantAsync(tenantId);
                // Remove all pools from cache so we skip the possibility to
                // communicate with them while the CK-cache is unloaded.
                _poolCache.RemoveTenant(tenantId);

                // Note: we do NOT touch CommunicationState in the database here.
                // The legacy /poolHub design had to mark every pool Unregistered
                // because the per-pool SignalR connection died on cache flush
                // and only re-registered after the operator reconnected. With
                // the new /operatorHub model the operator's connection survives
                // tenant cache reloads entirely — pools stay Online unless the
                // operator actually disconnects, in which case OnDisconnectedAsync
                // sets them Offline.

                await _eventService.StoreInformationEventAsync(tenantId,
                    $"Tenant pre-update completed. {poolTenant.PoolsByName.Count} pool(s) flushed from cache.");
            }
        }
        catch (Exception e)
        {
            throw PoolServiceException.PreUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task PosUpdateTenantAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] PosUpdate tenant", tenantId);

        try
        {
            await _semaphore.WaitAsync();

            _poolCache.AddOrUpdateTenant(tenantId);

            // Note: pool CommunicationState is intentionally NOT reset here.
            // See PreUpdateTenantAsync above for the full rationale — the
            // operator-hub model decouples connection lifecycle from tenant
            // cache lifecycle, so the on-disk state is authoritative and
            // should be preserved across cache reloads.

            await _eventService.StoreInformationEventAsync(tenantId,
                "Tenant post-update completed. Pool cache re-initialized.");
        }
        catch (Exception e)
        {
            throw PoolServiceException.PosUpdateTenantFailed(tenantId, e);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeployPoolAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Deploying pool '{PoolRtId}'", tenantId, poolRtId);

        var rtPool = await GetPoolByRtIdAsync(tenantId, poolRtId);

        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolRtId,
            RtDeploymentStateEnum.Deployed);

        if (rtPool.Environment == RtEnvironmentEnum.Cloud)
        {
            var poolName = rtPool.Name ?? string.Empty;
            Logger.Info(
                "[{TenantId}] Pool '{PoolName}' is Cloud — notifying central Communication Operator",
                tenantId, poolName);
            await _operatorConnectionManager.NotifyPoolDeployedAsync(new DeployedPoolDto
            {
                TenantId = tenantId,
                PoolName = poolName,
            });

            // Note: workloads are NOT auto-deployed here. Users (or callers)
            // trigger DeployWorkloadAsync per workload explicitly — this lets
            // the pool's CommunicationState turn Online first, so any issue
            // with the pool itself is visible before any helm install runs.
            // Use case: smoke-test a fresh pool, then phase adapter deploys.
        }
        else
        {
            Logger.Info(
                "[{TenantId}] Pool '{PoolName}' is Edge — operator notification skipped, install the pool externally",
                tenantId, (rtPool.Name ?? string.Empty));
        }

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool '{(rtPool.Name ?? string.Empty)}' deployed (environment: {rtPool.Environment}).");
    }

    /// <inheritdoc />
    public async Task DeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId)
    {
        Logger.Info("[{TenantId}] Deploying workload '{WorkloadRtId}'", tenantId, workloadRtId);

        var workload = await _communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
        if (workload == null)
        {
            throw PoolServiceException.WorkloadNotFound(tenantId, workloadRtId);
        }

        var poolName = await ResolvePoolNameForWorkloadAsync(tenantId, workload);
        if (poolName == null)
        {
            throw PoolServiceException.WorkloadNotInPool(tenantId, workloadRtId);
        }

        // Validate the workload's Helm fields up-front so we can throw a precise
        // exception telling the user exactly what to fix. BuildWorkloadDeployedDtoAsync
        // intentionally returns null for any missing field (silently skipped by the
        // pool fan-out), but for an explicit user-triggered single-workload deploy
        // the user deserves to know which field is missing.
        await EnsureWorkloadIsHelmDeployableAsync(tenantId, workload);

        var dto = await BuildWorkloadDeployedDtoAsync(tenantId, poolName, workload);
        if (dto == null)
        {
            // Should be unreachable after EnsureWorkloadIsHelmDeployableAsync, but
            // keep the fallback so the call can never silently no-op.
            throw PoolServiceException.WorkloadMissingChartName(tenantId, workloadRtId, workload.Name);
        }

        await _operatorConnectionManager.NotifyWorkloadDeployedAsync(dto);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' deploy requested.");
    }

    /// <summary>
    /// Throws a precise <see cref="PoolServiceException"/> when the workload is
    /// missing any of the fields required for a Helm-based deploy: chart name,
    /// chart version, linked HelmRepositoryConfiguration, or repository URL.
    /// </summary>
    private async Task EnsureWorkloadIsHelmDeployableAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName))
        {
            throw PoolServiceException.WorkloadMissingChartName(tenantId, workload.RtId, workload.Name);
        }
        if (string.IsNullOrWhiteSpace(workload.ChartVersion))
        {
            throw PoolServiceException.WorkloadMissingChartVersion(tenantId, workload.RtId, workload.Name);
        }

        var repo = await _communicationRepository.GetHelmRepositoryForWorkloadAsync(tenantId, workload.RtId);
        if (repo == null)
        {
            throw PoolServiceException.WorkloadMissingHelmRepository(tenantId, workload.RtId, workload.Name);
        }
        if (string.IsNullOrWhiteSpace(repo.RepositoryUrl))
        {
            throw PoolServiceException.WorkloadHelmRepositoryUrlEmpty(tenantId, workload.RtId, workload.Name);
        }
    }

    /// <inheritdoc />
    public async Task UndeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId)
    {
        Logger.Info("[{TenantId}] Undeploying workload '{WorkloadRtId}'", tenantId, workloadRtId);

        var workload = await _communicationRepository.GetWorkloadByRtIdAsync(tenantId, workloadRtId);
        if (workload == null)
        {
            throw PoolServiceException.WorkloadNotFound(tenantId, workloadRtId);
        }

        var poolName = await ResolvePoolNameForWorkloadAsync(tenantId, workload);
        if (poolName == null)
        {
            throw PoolServiceException.WorkloadNotInPool(tenantId, workloadRtId);
        }

        await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = tenantId,
            PoolName = poolName,
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadType = workload is RtApplication
                ? WorkloadTypeDto.Application
                : WorkloadTypeDto.Adapter,
        });

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' undeploy requested.");
    }

    /// <summary>
    /// Resolves the pool name for a workload by walking the <c>Manages</c>
    /// association back to its parent <c>RtPool</c>. Returns null when the
    /// workload isn't currently in any pool.
    /// </summary>
    private async Task<string?> ResolvePoolNameForWorkloadAsync(string tenantId, RtDeployableWorkload workload)
    {
        var pool = await _communicationRepository.GetPoolForWorkloadAsync(tenantId, workload.RtId);
        return pool?.Name;
    }

    /// <inheritdoc />
    public async Task UndeployPoolAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Undeploying pool '{PoolRtId}'", tenantId, poolRtId);

        var rtPool = await GetPoolByRtIdAsync(tenantId, poolRtId);

        if (rtPool.Environment == RtEnvironmentEnum.Cloud)
        {
            var poolName = rtPool.Name ?? string.Empty;

            // Helm uninstall managed workloads before tearing down the pool
            // itself — the operator removes the CommunicationPool CR last so
            // it can still resolve the pool's namespace while uninstalling.
            await UndeployManagedWorkloadsAsync(tenantId, poolName);

            Logger.Info(
                "[{TenantId}] Pool '{PoolName}' is Cloud — notifying central Communication Operator to clean up",
                tenantId, poolName);
            await _operatorConnectionManager.NotifyPoolUndeployedAsync(tenantId, poolName);
        }

        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolRtId,
            RtDeploymentStateEnum.Undeployed);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool '{(rtPool.Name ?? string.Empty)}' undeployed (environment: {rtPool.Environment}).");
    }

    private async Task DeployManagedWorkloadsAsync(string tenantId, OctoObjectId poolRtId, string poolName)
    {
        IReadOnlyCollection<RtDeployableWorkload> workloads;
        try
        {
            workloads = await _communicationRepository.GetWorkloadsForPoolAsync(tenantId, poolRtId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "[{TenantId}] Failed to enumerate managed workloads of pool '{PoolName}'; pool is deployed but no workloads were fanned out",
                tenantId, poolName);
            return;
        }

        if (workloads.Count == 0)
        {
            Logger.Info("[{TenantId}] Pool '{PoolName}' has no managed workloads", tenantId, poolName);
            return;
        }

        Logger.Info("[{TenantId}] Pool '{PoolName}' has {Count} managed workload(s) to deploy",
            tenantId, poolName, workloads.Count);

        foreach (var workload in workloads)
        {
            try
            {
                var dto = await BuildWorkloadDeployedDtoAsync(tenantId, poolName, workload);
                if (dto == null)
                {
                    Logger.Warn(
                        "[{TenantId}] Workload '{WorkloadName}' is incomplete — skipping deploy",
                        tenantId, workload.Name ?? string.Empty);
                    continue;
                }

                await _operatorConnectionManager.NotifyWorkloadDeployedAsync(dto);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to deploy workload '{WorkloadName}' of pool '{PoolName}'",
                    tenantId, workload.Name ?? string.Empty, poolName);
            }
        }
    }

    private async Task UndeployManagedWorkloadsAsync(string tenantId, string poolName)
    {
        // Read from in-memory tracking only — same rationale as
        // UndeployAllCloudPoolsAsync, this path may run during tenant delete
        // where the repository is already torn down.
        var tracked = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId)
            .Where(w => w.PoolName == poolName)
            .ToArray();

        if (tracked.Length == 0)
        {
            return;
        }

        Logger.Info("[{TenantId}] Undeploying {Count} workload(s) of pool '{PoolName}'",
            tenantId, tracked.Length, poolName);

        foreach (var workload in tracked)
        {
            try
            {
                await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to undeploy workload '{WorkloadName}' of pool '{PoolName}'",
                    tenantId, workload.WorkloadName, poolName);
            }
        }
    }

    private async Task<WorkloadDeployedDto?> BuildWorkloadDeployedDtoAsync(string tenantId, string poolName,
        RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName) || string.IsNullOrWhiteSpace(workload.ChartVersion))
        {
            return null;
        }

        var repo = await _communicationRepository.GetHelmRepositoryForWorkloadAsync(tenantId, workload.RtId);
        if (repo == null || string.IsNullOrWhiteSpace(repo.RepositoryUrl))
        {
            return null;
        }

        var overrides = (workload.Values ?? Enumerable.Empty<RtValueOverrideRecord>())
            .Select(v => new ValueOverrideDto
            {
                Path = v.Path ?? string.Empty,
                Value = v.IsSecret
                    ? _encryptionService.Decrypt(v.Value ?? string.Empty)
                    : v.Value ?? string.Empty,
                IsSecret = v.IsSecret,
            })
            .ToArray();

        // Only Adapter carries the ReceivesClusterSecrets flag — Application
        // workloads keep the default (false). If we ever want apps to receive
        // cluster credentials, move the attribute up to DeployableWorkload.
        var receivesClusterSecrets = workload is RtAdapter adapter && adapter.ReceivesClusterSecrets;

        return new WorkloadDeployedDto
        {
            TenantId = tenantId,
            PoolName = poolName,
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadRtId = workload.RtId.ToString(),
            WorkloadType = workload is RtApplication
                ? WorkloadTypeDto.Application
                : WorkloadTypeDto.Adapter,
            RepositoryUrl = repo.RepositoryUrl,
            RepositoryUsername = repo.Username,
            RepositoryPassword = string.IsNullOrEmpty(repo.Password)
                ? null
                : _encryptionService.Decrypt(repo.Password),
            ChartName = workload.ChartName,
            ChartVersion = workload.ChartVersion,
            ValuesYaml = workload.ValuesYaml ?? string.Empty,
            Values = overrides,
            ReceivesClusterSecrets = receivesClusterSecrets,
        };
    }

    /// <inheritdoc />
    public async Task UndeployAllCloudPoolsAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Undeploying all Cloud pools (tenant cleanup)", tenantId);

        // Read from the operator connection manager's in-memory tracking
        // rather than the tenant repository. PreDeleteTenant fires in parallel
        // with PreUpdatePreDeleteTenantConsumer (octo-common-services), which
        // unloads the CK-cache for the tenant. If we hit the repository here
        // we race and get "Failed to get pools" — and the operator is never
        // told to clean up, leaving the CommunicationPool CR and broker
        // secret orphaned in the cluster.
        var poolNames = _operatorConnectionManager.GetDeployedPoolsForTenant(tenantId);
        var trackedWorkloads = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId);

        if (poolNames.Count == 0 && trackedWorkloads.Count == 0)
        {
            Logger.Info("[{TenantId}] No Cloud pools or workloads to clean up", tenantId);
            return;
        }

        // Tear down workloads first so the operator can helm uninstall while
        // the pool namespace is still around.
        foreach (var workload in trackedWorkloads)
        {
            try
            {
                await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to notify operator of workload undeploy during tenant cleanup, workload '{WorkloadName}' of pool '{PoolName}'",
                    tenantId, workload.WorkloadName, workload.PoolName);
            }
        }

        foreach (var poolName in poolNames)
        {
            try
            {
                await _operatorConnectionManager.NotifyPoolUndeployedAsync(tenantId, poolName);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to notify operator of pool undeploy during tenant cleanup, pool '{PoolName}'",
                    tenantId, poolName);
            }
        }

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Notified central Communication Operator to undeploy {trackedWorkloads.Count} workload(s) and {poolNames.Count} Cloud pool(s) for tenant cleanup.");
    }

    private async Task<RtPool> GetPoolByRtIdAsync(string tenantId, OctoObjectId poolRtId)
    {
        var pools = await _communicationRepository.GetPoolsAsync(tenantId);
        var rtPool = pools.FirstOrDefault(p => p.RtId == poolRtId);
        if (rtPool == null)
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolRtId);
        }
        return rtPool;
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' offline", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Offline);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOfflineAsync(string tenantId, string poolName,
        string disconnectingConnectionId)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            return;
        }

        if (!poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            return;
        }

        // Multi-claim guard: more than one operator connection can claim the
        // same pool at the same time — central operator with replicas, or a
        // brief rolling-upgrade overlap where the new pod has registered but
        // the old pod's SignalR connection has not yet timed out. The
        // PoolDescription cache only remembers the LAST claim's ConnectionId,
        // so the disconnect of one claimer would silently flip the pool
        // Offline even though another connection is still hosting it
        // (caller passed RemoveOperator's orphan list, which only filters
        // claims made by the disconnecting connection, not all live claims).
        //
        // OperatorConnectionManager.RemoveOperator has already cleared the
        // disconnecting connection's tracking entry by the time we get here,
        // so any results from GetConnectionsForPool are surviving operators.
        var stillClaiming = _operatorConnectionManager.GetConnectionsForPool(tenantId, poolName);
        if (stillClaiming.Count > 0)
        {
            // Keep the pool Online and rewire the cache to a surviving
            // connection so the stale-disconnect guard below works correctly
            // when THAT one eventually disconnects too.
            poolDescription.UpdateConnectionId(tenantId, stillClaiming[0]);
            Logger.Info(
                "[{TenantId}] pool '{PoolName}' stays online after disconnect of " +
                "'{OldConnectionId}': {Count} other operator connection(s) still claim it; " +
                "cache rewired to '{NewConnectionId}'",
                tenantId, poolName, disconnectingConnectionId, stillClaiming.Count,
                stillClaiming[0]);
            return;
        }

        // Stale-disconnect guard: if a newer connection has already taken over this
        // pool (e.g. the operator reconnected after a controller restart and the old
        // connection's OnDisconnectedAsync is only now firing), we must not flip
        // Online → Offline. Mirrors the adapter pattern in
        // AdapterService.SetAdapterCommunicationStateOfflineAsync.
        if (!string.IsNullOrWhiteSpace(poolDescription.ConnectionId) &&
            poolDescription.ConnectionId != disconnectingConnectionId)
        {
            Logger.Warn(
                "[{TenantId}] ignoring stale disconnect for pool '{PoolName}': cached connection " +
                "'{CurrentConnectionId}' has replaced disconnecting connection '{OldConnectionId}'",
                tenantId, poolName, poolDescription.ConnectionId, disconnectingConnectionId);
            return;
        }

        poolDescription.RemoveConnectionId(tenantId);
        await SetCommunicationStateOfflineAsync(tenantId, poolDescription.PoolRtId);
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' online", tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
                RtCommunicationStateEnum.Online);
        }
    }

    /// <inheritdoc />
    public async Task SetCommunicationStateOnlineAsync(string tenantId, string poolName, string connectionId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolName}' online (connection '{ConnectionId}')",
            tenantId, poolName, connectionId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        // Lazy-load the pool into the cache on first sight. The legacy /poolHub
        // path relied on RegisterPoolOperatorAsync (which also touched the
        // pool's DeploymentState) to populate the cache; the new /operatorHub
        // RegisterPoolAsync is purely about CommunicationState, so we just
        // ensure the cache is populated here without touching DeploymentState.
        if (!poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription))
        {
            var poolList = await _communicationRepository.GetPoolByNameAsync(tenantId, poolName);
            var rtPool = poolList.FirstOrDefault();
            if (rtPool == null)
            {
                Logger.Warn("[{TenantId}] Cannot set pool '{PoolName}' online — not found in repository",
                    tenantId, poolName);
                return;
            }
            poolDescription = poolTenant.AddPool(poolName, rtPool.RtId, connectionId);
        }
        else
        {
            poolDescription.UpdateConnectionId(tenantId, connectionId);
        }

        await SetCommunicationStateOnlineAsync(tenantId, poolDescription.PoolRtId);
    }

    /// <inheritdoc />
    public async Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsByName.ContainsKey(poolName))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolName);
        }

        await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityId,
            deploymentState);
    }

    /// <inheritdoc />
    public async Task SetAdapterDeploymentStateAsync(string tenantId, string poolName,
        ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        if (!poolTenant.PoolsByName.ContainsKey(poolName))
        {
            throw PoolServiceException.PoolNotFound(tenantId, poolName);
        }

        await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, adapterRtEntityIds,
            deploymentState);
    }

    public async Task<IReadOnlyList<PoolSummaryDto>> GetPoolSummariesAsync(string tenantId)
    {
        var pools = await _communicationRepository.GetPoolsAsync(tenantId);
        return pools.Select(p => new PoolSummaryDto
        {
            RtId = p.RtId.ToString(),
            Name = p.Name ?? string.Empty,
            Description = p.Description,
            CommunicationState = (CommunicationState)(int)p.CommunicationState,
            ConfigurationState = (ConfigurationState)(int)p.ConfigurationState,
            DeploymentState = (EntityDeploymentState)(int)p.DeploymentState,
            CommunicationStateTimestamp = p.CommunicationStateTimestamp,
            StatusMessage = p.StatusMessage
        }).ToList();
    }
}