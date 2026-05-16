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
    private readonly IPoolHubCallbacks _poolHubCallbacks;
    private readonly ICommunicationEventService _eventService;
    private readonly IOperatorConnectionManager _operatorConnectionManager;
    private readonly IWorkloadEncryptionService _encryptionService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Communication repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="poolHubCallbacks">Callbacks to inform client of configuration changes</param>
    /// <param name="eventService">Service for storing system events</param>
    /// <param name="operatorConnectionManager">Manages SignalR connections to central Communication Operators (for Cloud-pool deploy/undeploy notifications)</param>
    /// <param name="encryptionService">Decrypts secret-flagged ValueOverride values before they go on the SignalR wire</param>
    public PoolService(ICommunicationRepository communicationRepository, IPoolCache poolCache,
        IPoolHubCallbacks poolHubCallbacks, ICommunicationEventService eventService,
        IOperatorConnectionManager operatorConnectionManager,
        IWorkloadEncryptionService encryptionService)
    {
        _communicationRepository = communicationRepository;
        _poolCache = poolCache;
        _poolHubCallbacks = poolHubCallbacks;
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
                // Inform all pools that tenant is going to be updated
                await _poolHubCallbacks.PreUpdateTenantAsync(tenantId);
                // Remove all pools from cache, so we skip the possibility to communicate with them
                _poolCache.RemoveTenant(tenantId);

                foreach (var pool in poolTenant.PoolsByName.Values)
                {
                    await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, pool.PoolRtId,
                        RtCommunicationStateEnum.Unregistered);
                }

                await _eventService.StoreInformationEventAsync(tenantId,
                    $"Tenant pre-update completed. {poolTenant.PoolsByName.Count} pool(s) disconnected.");
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

            var pools = await _communicationRepository.GetPoolsAsync(tenantId);
            foreach (var pool in pools)
            {
                await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, pool.RtId,
                    RtCommunicationStateEnum.Unregistered);
            }

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

            // Pool is up — fan out helm deploys for every managed workload.
            // Failures per workload are logged but don't fail the pool deploy
            // itself; the operator will retry on its next reconcile cycle.
            await DeployManagedWorkloadsAsync(tenantId, poolRtId, poolName);
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
    public async Task SetCommunicationStateOfflineAsync(string tenantId, string poolName)
    {
        if (_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
            if (poolDescription != null)
            {
                poolDescription.RemoveConnectionId(tenantId);
                await SetCommunicationStateOfflineAsync(tenantId, poolDescription.PoolRtId);
            }
        }
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
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        poolTenant.PoolsByName.TryGetValue(poolName, out var poolDescription);
        if (poolDescription != null)
        {
            poolDescription.UpdateConnectionId(tenantId, connectionId);

            await SetCommunicationStateOnlineAsync(tenantId, poolDescription.PoolRtId);
        }
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