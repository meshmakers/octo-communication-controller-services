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
    private readonly IWorkloadTemplateResolver _templateResolver;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="communicationRepository">Communication repository</param>
    /// <param name="poolCache">Distributed and synchronized data between nodes</param>
    /// <param name="eventService">Service for storing system events</param>
    /// <param name="operatorConnectionManager">Manages SignalR connections to central Communication Operators (for Cloud-pool deploy/undeploy notifications and PreUpdateTenant fan-out)</param>
    /// <param name="encryptionService">Decrypts secret-flagged ValueOverride values before they go on the SignalR wire</param>
    /// <param name="templateResolver">Resolves <c>{{domain.NAME}}</c>, <c>{{service.NAME}}</c> and <c>{{context.tenantId}}</c> placeholders in workload <c>Hostname</c>, non-secret <c>ValueOverride.Value</c> and <c>ValuesYaml</c> at deploy time</param>
    public PoolService(ICommunicationRepository communicationRepository, IPoolCache poolCache,
        ICommunicationEventService eventService,
        IOperatorConnectionManager operatorConnectionManager,
        IWorkloadEncryptionService encryptionService,
        IWorkloadTemplateResolver templateResolver)
    {
        _communicationRepository = communicationRepository;
        _poolCache = poolCache;
        _eventService = eventService;
        _operatorConnectionManager = operatorConnectionManager;
        _encryptionService = encryptionService;
        _templateResolver = templateResolver;
    }
    
    /// <inheritdoc />
    public async Task UnregisterPoolOperatorAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Unregistering operator for pool '{PoolRtId}'",
            tenantId, poolRtId);

        if (!_poolCache.TryGetTenant(tenantId, out var tenantDescription))
        {
            return;
        }
        if (!tenantDescription.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            return;
        }

        // Set communication state to Unregistered *before* removing from cache.
        // After RemovePool, the OnDisconnectedAsync that follows the operator's
        // graceful disconnect can no longer locate the pool, so any state write
        // would silently no-op and the UI would keep showing Online forever.
        await _communicationRepository.SetPoolCommunicationStateAsync(tenantId, poolDescription.PoolRtId,
            RtCommunicationStateEnum.Unregistered);

        var poolName = poolDescription.PoolName;
        tenantDescription.RemovePool(poolDescription.PoolRtId);

        // Edge pools stay Disabled regardless of operator presence; only Cloud
        // pools flip back to Pending until a new operator re-registers.
        var pools = await _communicationRepository.GetPoolsAsync(tenantId);
        var rtPool = pools.FirstOrDefault(p => p.RtId == poolRtId);
        var targetState = rtPool?.Environment == RtEnvironmentEnum.Edge
            ? RtDeploymentStateEnum.Disabled
            : RtDeploymentStateEnum.Pending;
        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolDescription.PoolRtId,
            targetState);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool operator for pool '{poolName}' unregistered.",
            new RtEntityId(SystemCommunicationCkIds.RtCkPoolTypeId, poolDescription.PoolRtId));

        Logger.Info("[{TenantId}] Operator for pool '{PoolRtId}' unregistered", tenantId, poolRtId);
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
                    $"Tenant pre-update completed. {poolTenant.PoolsById.Count} pool(s) flushed from cache.");
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

        // Outside the semaphore: recompute DeploymentState across all pools /
        // workloads / pipelines / triggers. This is the catch-all backfill that
        // keeps the DB in sync with the Disabled rules whenever a tenant is
        // (re-)enabled or its CK model updated. Runs after PosUpdate so the
        // pool cache is already re-initialised.
        try
        {
            await RecomputeAllDeploymentStatesAsync(tenantId);
        }
        catch (Exception e)
        {
            // Backfill is best-effort — log but don't fail the PosUpdate handler.
            Logger.Warn(e,
                "[{TenantId}] DeploymentState recompute after PosUpdateTenant failed", tenantId);
        }
    }

    /// <inheritdoc />
    public async Task DeployPoolAsync(string tenantId, OctoObjectId poolRtId)
    {
        Logger.Info("[{TenantId}] Deploying pool '{PoolRtId}'", tenantId, poolRtId);

        var rtPool = await GetPoolByRtIdAsync(tenantId, poolRtId);

        if (rtPool.Environment == RtEnvironmentEnum.Edge)
        {
            // We never ask the central operator to deploy an Edge pool. The
            // entity's DeploymentState is left untouched here — it reflects
            // whatever the operator last reported (e.g. Deployed if the pool
            // was Cloud-deployed before the user switched it to Edge; the
            // user must call Undeploy to clean those resources up). The
            // backfill takes care of moving Undeployed Edge pools to
            // Disabled separately.
            throw PoolServiceException.EdgePoolNotDeployable(tenantId, poolRtId, rtPool.Name);
        }

        var poolName = rtPool.Name ?? string.Empty;
        Logger.Info(
            "[{TenantId}] Pool '{PoolName}' (rtId {PoolRtId}) is Cloud — notifying central Communication Operator",
            tenantId, poolName, poolRtId);
        await _operatorConnectionManager.NotifyPoolDeployedAsync(new DeployedPoolDto
        {
            TenantId = tenantId,
            PoolRtId = poolRtId.ToString(),
        });

        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolRtId,
            RtDeploymentStateEnum.Deployed);

        // Note: workloads are NOT auto-deployed here. Users (or callers)
        // trigger DeployWorkloadAsync per workload explicitly — this lets
        // the pool's CommunicationState turn Online first, so any issue
        // with the pool itself is visible before any helm install runs.
        // Use case: smoke-test a fresh pool, then phase adapter deploys.

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool '{poolName}' deployed.");
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

        var pool = await _communicationRepository.GetPoolForWorkloadAsync(tenantId, workload.RtId);
        if (pool == null)
        {
            throw PoolServiceException.WorkloadNotInPool(tenantId, workloadRtId);
        }

        // Workloads in Edge pools are deployable: NotifyWorkloadDeployedAsync
        // routes via RegisterPoolForConnection to whichever operator (central
        // or edge) registered the pool, and OperatorHubService.WorkloadDeployedAsync
        // runs the same helm upgrade --install path in either mode. Only the
        // pool itself (CR + broker secret) is central-cluster-only and rejected
        // in DeployPoolAsync.

        // Validate the workload's Helm fields up-front so we can throw a precise
        // exception telling the user exactly what to fix. BuildWorkloadDeployedDtoAsync
        // intentionally returns null for any missing field (silently skipped by the
        // pool fan-out), but for an explicit user-triggered single-workload deploy
        // the user deserves to know which field is missing.
        await EnsureWorkloadIsHelmDeployableAsync(tenantId, workload);

        var poolName = pool.Name ?? string.Empty;
        var dto = await BuildWorkloadDeployedDtoAsync(tenantId, pool.RtId, poolName, workload);
        if (dto == null)
        {
            // Should be unreachable after EnsureWorkloadIsHelmDeployableAsync, but
            // keep the fallback so the call can never silently no-op.
            throw PoolServiceException.WorkloadMissingChartName(tenantId, workloadRtId, workload.Name);
        }

        await _operatorConnectionManager.NotifyWorkloadDeployedAsync(dto);

        // Set Pending immediately so a re-deploy is visible in the UI — e.g.
        // the user updates the chart version on a currently-Deployed adapter
        // and clicks Deploy: without this write the state would stay Deployed
        // throughout and the user would see no feedback that the helm-upgrade
        // actually ran. The operator's ReportWorkloadDeploymentStatusAsync
        // round-trip flips this to Deployed (success) or Error (failure)
        // within a few seconds.
        await SetWorkloadDeploymentStateAsync(tenantId, workload, RtDeploymentStateEnum.Pending);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' deploy requested.");
    }

    private async Task SetWorkloadDeploymentStateAsync(string tenantId, RtDeployableWorkload workload,
        RtDeploymentStateEnum deploymentState)
    {
        switch (workload)
        {
            case RtAdapter:
                {
                    var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workload.RtId);
                    await _communicationRepository.SetAdapterDeploymentStateAsync(tenantId, rtEntityId, deploymentState);
                    break;
                }
            case RtApplication:
                {
                    var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workload.RtId);
                    await _communicationRepository.SetApplicationDeploymentStateAsync(tenantId, rtEntityId, deploymentState);
                    break;
                }
            default:
                // Defensive — if a new DeployableWorkload subtype is added without a
                // dedicated setter, we'd silently skip the write. Make that visible
                // in the log instead of looking like the write succeeded.
                Logger.Warn(
                    "[{TenantId}] No DeploymentState setter for workload of type '{Type}' (RtId '{RtId}'); skipping",
                    tenantId, workload.GetType().Name, workload.RtId);
                break;
        }
    }

    /// <summary>
    /// Throws a precise <see cref="PoolServiceException"/> when the workload is
    /// missing any of the fields required for a Helm-based deploy: chart name,
    /// linked HelmRepositoryConfiguration, or repository URL. <c>ChartVersion</c>
    /// is intentionally NOT required — an empty value is the explicit "use the
    /// newest chart in the configured repository" opt-in (the operator's
    /// HelmRunner omits <c>--version</c> in that case, matching the dev/test
    /// rollout pattern seeded by the System.Communication.MainLatest blueprint).
    /// </summary>
    private async Task EnsureWorkloadIsHelmDeployableAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName))
        {
            throw PoolServiceException.WorkloadMissingChartName(tenantId, workload.RtId, workload.Name);
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

        // Ingress contract: when IngressEnabled is true we project ingress.enabled=true
        // + publicUri into the chart values. The chart's templates/ingress.yaml builds
        // host rules from publicUri, so an empty Hostname produces an Ingress with an
        // empty host — k8s admission rejects it and the helm release would fail
        // mid-rollout. Surface the misconfiguration as an actionable Deploy-time error
        // instead. ChartName / repo checks above mirror the same fail-fast pattern.
        if (workload.IngressEnabled && string.IsNullOrWhiteSpace(workload.Hostname))
        {
            throw PoolServiceException.WorkloadIngressEnabledButHostnameEmpty(tenantId, workload.RtId, workload.Name);
        }

        // Validate template placeholders up-front so misconfigured workloads
        // fail with an actionable Deploy-time error instead of producing an
        // Ingress with the literal '{{...}}' as host (k8s admission rejects
        // mid-rollout) or a helm values file with unresolved placeholders.
        // Workloads that don't use template syntax pass through unchanged.
        var ctx = new WorkloadTemplateContext(tenantId);

        if (!string.IsNullOrWhiteSpace(workload.Hostname) &&
            !_templateResolver.TryResolve(workload.Hostname, ctx, out _, out var unknownInHostname))
        {
            throw PoolServiceException.WorkloadTemplateUnknownPlaceholder(
                tenantId, workload.RtId, workload.Name, "Hostname", workload.Hostname, unknownInHostname!);
        }

        // Non-secret ValueOverrides flow through the resolver. Secret-flagged
        // entries are NOT validated/substituted here — the encryption layer
        // owns those values, and running templating over decrypted secret
        // material would mix two contracts.
        foreach (var v in workload.Values ?? Enumerable.Empty<RtValueOverrideRecord>())
        {
            if (v.IsSecret || string.IsNullOrEmpty(v.Value))
            {
                continue;
            }
            if (!_templateResolver.TryResolve(v.Value, ctx, out _, out var unknownInOverride))
            {
                throw PoolServiceException.WorkloadTemplateUnknownPlaceholder(
                    tenantId, workload.RtId, workload.Name,
                    $"ValueOverride[{v.Path ?? string.Empty}]", v.Value, unknownInOverride!);
            }
        }

        if (!string.IsNullOrEmpty(workload.ValuesYaml) &&
            !_templateResolver.TryResolve(workload.ValuesYaml, ctx, out _, out var unknownInYaml))
        {
            throw PoolServiceException.WorkloadTemplateUnknownPlaceholder(
                tenantId, workload.RtId, workload.Name, "ValuesYaml", workload.ValuesYaml, unknownInYaml!);
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

        var pool = await _communicationRepository.GetPoolForWorkloadAsync(tenantId, workload.RtId);
        if (pool == null)
        {
            throw PoolServiceException.WorkloadNotInPool(tenantId, workloadRtId);
        }

        // Reject when there's nothing to undeploy. Both Undeployed and
        // Disabled are terminal resting states — no helm release to remove.
        if (workload.DeploymentState == RtDeploymentStateEnum.Undeployed ||
            workload.DeploymentState == RtDeploymentStateEnum.Disabled)
        {
            throw PoolServiceException.WorkloadAlreadyNotDeployed(tenantId, workloadRtId, workload.Name,
                workload.DeploymentState);
        }

        // Always go through the central-operator cleanup path even when
        // Environment is now Edge or Helm fields have since been cleared —
        // a helm release may still exist from a prior valid Deploy that
        // has not been cleaned up yet.
        await _operatorConnectionManager.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = tenantId,
            PoolRtId = pool.RtId.ToString(),
            WorkloadRtId = workload.RtId.ToString(),
            WorkloadName = workload.Name ?? string.Empty,
            WorkloadType = workload is RtApplication
                ? WorkloadTypeDto.Application
                : WorkloadTypeDto.Adapter,
        });

        // Compute resting state. If the workload can no longer be deployed
        // (missing Helm fields), park it at Disabled; otherwise Undeployed so a
        // fresh deploy can be triggered. Edge pools are NOT a disabling rule —
        // an edge operator deploys workloads via the same helm path as central.
        var restingState = await IsWorkloadHelmDeployableAsync(tenantId, workload)
            ? RtDeploymentStateEnum.Undeployed
            : RtDeploymentStateEnum.Disabled;
        await SetWorkloadDeploymentStateAsync(tenantId, workload, restingState);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Workload '{workload.Name}' undeploy requested (resting state: {restingState}).");
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

        // Reject when there's nothing to undeploy. Both Undeployed and
        // Disabled are terminal resting states — the operator has no CR /
        // broker secret to remove.
        if (rtPool.DeploymentState == RtDeploymentStateEnum.Undeployed ||
            rtPool.DeploymentState == RtDeploymentStateEnum.Disabled)
        {
            throw PoolServiceException.PoolAlreadyNotDeployed(tenantId, poolRtId, rtPool.Name,
                rtPool.DeploymentState);
        }

        var poolName = rtPool.Name ?? string.Empty;

        // Helm uninstall managed workloads before tearing down the pool
        // itself — the operator removes the CommunicationPool CR last so
        // it can still resolve the pool's namespace while uninstalling.
        // We always go through the central-operator cleanup path even when
        // Environment is now Edge: the user may have switched a Cloud pool
        // to Edge without first undeploying, and the CR/secret still exists
        // in the central cluster and must be removed.
        await UndeployManagedWorkloadsAsync(tenantId, poolRtId, poolName);

        Logger.Info(
            "[{TenantId}] Pool '{PoolName}' (rtId {PoolRtId}): notifying central Communication Operator to clean up (Environment={Environment})",
            tenantId, poolName, poolRtId, rtPool.Environment);
        await _operatorConnectionManager.NotifyPoolUndeployedAsync(tenantId, poolRtId.ToString());

        // Resting state after undeploy: Disabled when the pool can no longer
        // be deployed via this controller (Edge), else Undeployed.
        var restingState = rtPool.Environment == RtEnvironmentEnum.Edge
            ? RtDeploymentStateEnum.Disabled
            : RtDeploymentStateEnum.Undeployed;
        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, poolRtId, restingState);

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Pool '{poolName}' undeployed (resting state: {restingState}).");
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
                var dto = await BuildWorkloadDeployedDtoAsync(tenantId, poolRtId, poolName, workload);
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

    private async Task UndeployManagedWorkloadsAsync(string tenantId, OctoObjectId poolRtId, string poolName)
    {
        // Read from in-memory tracking only — same rationale as
        // UndeployAllCloudPoolsAsync, this path may run during tenant delete
        // where the repository is already torn down.
        var poolRtIdString = poolRtId.ToString();
        var tracked = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId)
            .Where(w => w.PoolRtId == poolRtIdString)
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

    private async Task<WorkloadDeployedDto?> BuildWorkloadDeployedDtoAsync(string tenantId,
        OctoObjectId poolRtId, string poolName, RtDeployableWorkload workload)
    {
        // ChartName is the minimal Helm identity we need to talk to a repository;
        // ChartVersion is optional and means "latest" when empty (see
        // EnsureWorkloadIsHelmDeployableAsync for the contract).
        if (string.IsNullOrWhiteSpace(workload.ChartName))
        {
            return null;
        }

        var repo = await _communicationRepository.GetHelmRepositoryForWorkloadAsync(tenantId, workload.RtId);
        if (repo == null || string.IsNullOrWhiteSpace(repo.RepositoryUrl))
        {
            return null;
        }

        var ctx = new WorkloadTemplateContext(tenantId);
        var overrides = (workload.Values ?? Enumerable.Empty<RtValueOverrideRecord>())
            .Select(v => new ValueOverrideDto
            {
                Path = v.Path ?? string.Empty,
                // Secret values flow through Decrypt only; template substitution
                // is deliberately skipped so encryption-sentinel and template
                // layers stay decoupled (see EnsureWorkloadIsHelmDeployableAsync).
                // Non-secret values are substituted; EnsureWorkloadIsHelmDeployableAsync
                // has already validated every placeholder, so TryResolve cannot
                // fail here.
                Value = v.IsSecret
                    ? _encryptionService.Decrypt(v.Value ?? string.Empty)
                    : ResolveTemplate(v.Value, ctx) ?? string.Empty,
                IsSecret = v.IsSecret,
            })
            .ToArray();

        return new WorkloadDeployedDto
        {
            TenantId = tenantId,
            PoolRtId = poolRtId.ToString(),
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
            // Coalesce a null/missing ChartVersion to empty string — the DTO is
            // non-nullable on the operator side and an empty value carries the
            // "use latest from configured repo" contract (the operator's
            // HelmRunner omits --version when blank).
            ChartVersion = workload.ChartVersion ?? string.Empty,
            // Same template resolution as for non-secret ValueOverrides — already
            // validated by EnsureWorkloadIsHelmDeployableAsync.
            ValuesYaml = ResolveTemplate(workload.ValuesYaml, ctx) ?? string.Empty,
            Values = overrides,
            // Lives on DeployableWorkload so both Adapter and Application can
            // opt in. Applications with a backend (e.g. energy-community,
            // voest-app) need cluster credentials just like in-cluster adapters.
            ReceivesClusterSecrets = workload.ReceivesClusterSecrets,
            // Public-ingress opt-in. The operator projects ingress.enabled=true
            // and publicUri into the workload's Helm values when this is set —
            // cluster-wide ingress defaults (className, cluster-issuer, TLS)
            // come from operator config. Hostname is normalised to null when
            // blank so the DTO matches the operator-side "set or absent"
            // contract on the chart values. Any {{domain.NAME}} placeholder is
            // resolved at this point against the controller's configured named
            // domains; EnsureWorkloadIsHelmDeployableAsync has already validated
            // that every referenced NAME exists, so TryResolve cannot fail here.
            IngressEnabled = workload.IngressEnabled,
            Hostname = ResolveHostname(workload.Hostname, ctx),
        };
    }

    private string? ResolveHostname(string? hostname, WorkloadTemplateContext ctx)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return null;
        }
        _templateResolver.TryResolve(hostname, ctx, out var resolved, out _);
        return string.IsNullOrWhiteSpace(resolved) ? null : resolved;
    }

    private string? ResolveTemplate(string? template, WorkloadTemplateContext ctx)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }
        _templateResolver.TryResolve(template, ctx, out var resolved, out _);
        return resolved;
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
        var deployedPools = _operatorConnectionManager.GetDeployedPoolsForTenant(tenantId);
        var trackedWorkloads = _operatorConnectionManager.GetDeployedWorkloadsForTenant(tenantId);

        if (deployedPools.Count == 0 && trackedWorkloads.Count == 0)
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
                    "[{TenantId}] Failed to notify operator of workload undeploy during tenant cleanup, workload '{WorkloadName}' (rtId {WorkloadRtId}, pool rtId {PoolRtId})",
                    tenantId, workload.WorkloadName, workload.WorkloadRtId, workload.PoolRtId);
            }
        }

        foreach (var poolRtId in deployedPools)
        {
            try
            {
                await _operatorConnectionManager.NotifyPoolUndeployedAsync(tenantId, poolRtId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to notify operator of pool undeploy during tenant cleanup, pool rtId {PoolRtId}",
                    tenantId, poolRtId);
            }
        }

        await _eventService.StoreInformationEventAsync(tenantId,
            $"Notified central Communication Operator to undeploy {trackedWorkloads.Count} workload(s) and {deployedPools.Count} Cloud pool(s) for tenant cleanup.");
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
    public async Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId,
        string disconnectingConnectionId)
    {
        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            return;
        }

        if (!poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
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
        var stillClaiming = _operatorConnectionManager.GetConnectionsForPool(tenantId, poolRtId.ToString());
        if (stillClaiming.Count > 0)
        {
            // Keep the pool Online and rewire the cache to a surviving
            // connection so the stale-disconnect guard below works correctly
            // when THAT one eventually disconnects too.
            poolDescription.UpdateConnectionId(tenantId, stillClaiming[0]);
            Logger.Info(
                "[{TenantId}] pool '{PoolRtId}' stays online after disconnect of " +
                "'{OldConnectionId}': {Count} other operator connection(s) still claim it; " +
                "cache rewired to '{NewConnectionId}'",
                tenantId, poolRtId, disconnectingConnectionId, stillClaiming.Count,
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
                "[{TenantId}] ignoring stale disconnect for pool '{PoolRtId}': cached connection " +
                "'{CurrentConnectionId}' has replaced disconnecting connection '{OldConnectionId}'",
                tenantId, poolRtId, poolDescription.ConnectionId, disconnectingConnectionId);
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
    public async Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId, string connectionId)
    {
        Logger.Info("[{TenantId}] Setting pool '{PoolRtId}' online (connection '{ConnectionId}')",
            tenantId, poolRtId, connectionId);

        if (!_poolCache.TryGetTenant(tenantId, out var poolTenant))
        {
            throw PoolServiceException.TenantNotFoundOrNotEnabled(tenantId);
        }

        // Lazy-load the pool into the cache on first sight. The legacy /poolHub
        // path relied on RegisterPoolOperatorAsync (which also touched the
        // pool's DeploymentState) to populate the cache; the new /operatorHub
        // RegisterPoolAsync is purely about CommunicationState, so we just
        // ensure the cache is populated here without touching DeploymentState.
        if (!poolTenant.PoolsById.TryGetValue(poolRtId, out var poolDescription))
        {
            var pools = await _communicationRepository.GetPoolsAsync(tenantId);
            var rtPool = pools.FirstOrDefault(p => p.RtId == poolRtId);
            if (rtPool == null)
            {
                Logger.Warn("[{TenantId}] Cannot set pool '{PoolRtId}' online — not found in repository",
                    tenantId, poolRtId);
                return;
            }
            poolDescription = poolTenant.AddPool(rtPool.Name ?? string.Empty, rtPool.RtId, connectionId);
        }
        else
        {
            poolDescription.UpdateConnectionId(tenantId, connectionId);
        }

        await SetCommunicationStateOnlineAsync(tenantId, poolDescription.PoolRtId);
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveDeployment>> GetActiveDeploymentsAsync(string tenantId)
    {
        var pools = await _communicationRepository.GetPoolsAsync(tenantId);
        var workloads = await _communicationRepository.GetWorkloadsAsync(tenantId);

        var active = new List<ActiveDeployment>();
        active.AddRange(pools
            .Where(p => ActiveDeployment.IsActive(p.DeploymentState))
            .Select(p => new ActiveDeployment(ActiveDeployment.PoolKind, DisplayName(p.Name, p.RtId), p.DeploymentState))
            .OrderBy(d => d.Name, StringComparer.Ordinal));
        active.AddRange(workloads
            .Where(w => ActiveDeployment.IsActive(w.DeploymentState))
            .Select(w => new ActiveDeployment(
                w is RtApplication ? ActiveDeployment.ApplicationKind : ActiveDeployment.AdapterKind,
                DisplayName(w.Name, w.RtId), w.DeploymentState))
            .OrderBy(d => d.Name, StringComparer.Ordinal));

        return active;
    }

    private static string DisplayName(string? name, OctoObjectId rtId)
    {
        return string.IsNullOrWhiteSpace(name) ? rtId.ToString() : name;
    }

    /// <inheritdoc />
    public async Task RecomputeAllDeploymentStatesAsync(string tenantId)
    {
        Logger.Info("[{TenantId}] Recomputing all deployment states", tenantId);

        var poolsUpdated = 0;
        var workloadsUpdated = 0;
        var pipelinesUpdated = 0;
        var triggersUpdated = 0;

        // 1) Pools: Edge → Disabled, Cloud → leave (controller-managed lifecycle)
        IReadOnlyCollection<RtPool> pools;
        try
        {
            pools = await _communicationRepository.GetPoolsAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate pools during deployment-state recompute", tenantId);
            return;
        }

        // Track adapter Disabled state so pipelines can inherit it without an extra DB hit per pipeline.
        var disabledAdapterRtIds = new HashSet<OctoObjectId>();

        foreach (var pool in pools)
        {
            try
            {
                // Only flip resting states. A pool currently Deployed/Pending/Error
                // owns real operator resources (CommunicationPool CR, broker secret)
                // and must stay until an explicit Undeploy. The user who switches a
                // Cloud pool to Edge while it is Deployed sees Deployed correctly
                // and can clean up via the Undeploy command.
                if (pool.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                    pool.DeploymentState == RtDeploymentStateEnum.Disabled)
                {
                    var poolTarget = pool.Environment == RtEnvironmentEnum.Edge
                        ? RtDeploymentStateEnum.Disabled
                        : RtDeploymentStateEnum.Undeployed;
                    if (poolTarget != pool.DeploymentState)
                    {
                        await _communicationRepository.SetPoolDeploymentStateAsync(tenantId, pool.RtId, poolTarget);
                        poolsUpdated++;
                    }
                }

                // 2) Workloads in this pool
                var workloads = await _communicationRepository.GetWorkloadsForPoolAsync(tenantId, pool.RtId);
                foreach (var workload in workloads)
                {
                    var target = await ComputeWorkloadTargetStateAsync(tenantId, workload, pool);
                    if (target.HasValue && target.Value != workload.DeploymentState)
                    {
                        await SetWorkloadDeploymentStateAsync(tenantId, workload, target.Value);
                        workloadsUpdated++;
                    }

                    // Track adapters that ended up (or stayed) Disabled so pipelines
                    // can inherit. A Deployed adapter — even one in an Edge pool
                    // post-env-switch — is still physically running, so its pipelines
                    // are not inherited-Disabled.
                    var endStateIsDisabled = (target ?? workload.DeploymentState) == RtDeploymentStateEnum.Disabled;
                    if (workload is RtAdapter && endStateIsDisabled)
                    {
                        disabledAdapterRtIds.Add(workload.RtId);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for pool '{PoolName}' or its workloads",
                    tenantId, pool.Name ?? pool.RtId.ToString());
            }
        }

        // 3) Pipelines: Disabled if no adapter or adapter is Disabled
        IReadOnlyCollection<RtPipeline> pipelines;
        try
        {
            pipelines = await _communicationRepository.GetAllPipelinesAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate pipelines during deployment-state recompute", tenantId);
            pipelines = Array.Empty<RtPipeline>();
        }

        var disabledPipelineRtIds = new HashSet<OctoObjectId>();
        foreach (var pipeline in pipelines)
        {
            try
            {
                var pipelineRtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipeline.RtId);
                var adapter = await _communicationRepository.GetAdapterByPipelineAsync(tenantId, pipelineRtEntityId);

                var ruleSaysDisabled = adapter == null || disabledAdapterRtIds.Contains(adapter.RtId);

                // Only touch resting states — a Deployed pipeline is physically pushed
                // to a running adapter and must be Undeployed via the proper command,
                // not silently flipped.
                if (pipeline.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                    pipeline.DeploymentState == RtDeploymentStateEnum.Disabled)
                {
                    var target = ruleSaysDisabled
                        ? RtDeploymentStateEnum.Disabled
                        : RtDeploymentStateEnum.Undeployed;
                    if (target != pipeline.DeploymentState)
                    {
                        await _communicationRepository.SetPipelineDeploymentStateAsync(tenantId,
                            pipelineRtEntityId, target, null);
                        pipelinesUpdated++;
                    }
                }

                // Pipeline is treated as "currently effectively disabled" for trigger
                // inheritance only when it actually ended up Disabled.
                var endState = pipeline.DeploymentState == RtDeploymentStateEnum.Undeployed ||
                               pipeline.DeploymentState == RtDeploymentStateEnum.Disabled
                    ? (ruleSaysDisabled ? RtDeploymentStateEnum.Disabled : RtDeploymentStateEnum.Undeployed)
                    : pipeline.DeploymentState;
                if (endState == RtDeploymentStateEnum.Disabled)
                {
                    disabledPipelineRtIds.Add(pipeline.RtId);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for pipeline '{PipelineRtId}'",
                    tenantId, pipeline.RtId);
            }
        }

        // 4) Triggers: Disabled if no pipelines or every triggered pipeline is Disabled
        IDictionary<RtPipelineTrigger, IList<RtPipeline>> triggersAndPipelines;
        try
        {
            triggersAndPipelines = await _communicationRepository.GetTriggersAndPipelinesAsync(tenantId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "[{TenantId}] Failed to enumerate triggers during deployment-state recompute", tenantId);
            triggersAndPipelines = new Dictionary<RtPipelineTrigger, IList<RtPipeline>>();
        }

        foreach (var (trigger, triggeredPipelines) in triggersAndPipelines)
        {
            try
            {
                // Only flip resting states — a Deployed trigger has its cron schedule
                // live in the trigger management service and must be removed via the
                // proper Undeploy path.
                if (trigger.DeploymentState != RtDeploymentStateEnum.Undeployed &&
                    trigger.DeploymentState != RtDeploymentStateEnum.Disabled)
                {
                    continue;
                }

                var hasRunnablePipeline = triggeredPipelines.Any(p =>
                    !disabledPipelineRtIds.Contains(p.RtId));
                var ruleSaysDisabled = triggeredPipelines.Count == 0 || !hasRunnablePipeline;

                var target = ruleSaysDisabled
                    ? RtDeploymentStateEnum.Disabled
                    : RtDeploymentStateEnum.Undeployed;
                if (target != trigger.DeploymentState)
                {
                    await _communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId, trigger.RtId,
                        target);
                    triggersUpdated++;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "[{TenantId}] Failed to recompute deployment state for trigger '{TriggerRtId}'",
                    tenantId, trigger.RtId);
            }
        }

        if (poolsUpdated + workloadsUpdated + pipelinesUpdated + triggersUpdated > 0)
        {
            await _eventService.StoreInformationEventAsync(tenantId,
                $"DeploymentState recompute: pools {poolsUpdated}, workloads {workloadsUpdated}, " +
                $"pipelines {pipelinesUpdated}, triggers {triggersUpdated} updated.");
        }

        Logger.Info(
            "[{TenantId}] Deployment-state recompute done: pools {Pools}, workloads {Workloads}, " +
            "pipelines {Pipelines}, triggers {Triggers}",
            tenantId, poolsUpdated, workloadsUpdated, pipelinesUpdated, triggersUpdated);
    }

    /// <summary>
    /// Computes the target DeploymentState for a workload at backfill time. Returns
    /// <c>null</c> when the current state must be left untouched — most notably for
    /// any operator-managed state (Deployed / Pending / Error), which reflects actual
    /// physical state and must not be silently overwritten. Only <c>Undeployed ↔
    /// Disabled</c> flips are allowed at backfill time. Anything operator-managed
    /// transitions to Disabled only via the Undeploy command path.
    /// </summary>
    private async Task<RtDeploymentStateEnum?> ComputeWorkloadTargetStateAsync(string tenantId,
        RtDeployableWorkload workload, RtPool pool)
    {
        // Only touch resting states. Deployed/Pending/Error must stay — those reflect
        // real operator-managed resources in the cluster, regardless of whether the
        // missing-Helm rule currently says "should be Disabled".
        if (workload.DeploymentState != RtDeploymentStateEnum.Undeployed &&
            workload.DeploymentState != RtDeploymentStateEnum.Disabled)
        {
            return null;
        }

        // Edge pools are NOT a disabling rule for workloads (only for the pool
        // itself) — an edge operator deploys workloads via the same helm path
        // as the central operator. Only missing Helm fields disable a workload.
        _ = pool;

        return await IsWorkloadHelmDeployableAsync(tenantId, workload)
            ? RtDeploymentStateEnum.Undeployed
            : RtDeploymentStateEnum.Disabled;
    }

    /// <summary>
    /// Non-throwing companion to <see cref="EnsureWorkloadIsHelmDeployableAsync"/>: returns
    /// <c>true</c> iff the workload has all required Helm fields (ChartName, an associated
    /// HelmRepositoryConfiguration with a non-empty RepositoryUrl). <c>ChartVersion</c> is
    /// optional and an empty value is valid (= "use latest"), so it does not gate this check.
    /// Used by the backfill to classify entities without throwing.
    /// </summary>
    private async Task<bool> IsWorkloadHelmDeployableAsync(string tenantId, RtDeployableWorkload workload)
    {
        if (string.IsNullOrWhiteSpace(workload.ChartName)) return false;

        var repo = await _communicationRepository.GetHelmRepositoryForWorkloadAsync(tenantId, workload.RtId);
        if (repo == null) return false;
        return !string.IsNullOrWhiteSpace(repo.RepositoryUrl);
    }

    /// <inheritdoc />
    public async Task RestoreDeployedStateAsync(string operatorConnectionId,
        IReadOnlyList<OperatorDeployedPoolReportDto> deployedPools)
    {
        // Defensive: an empty list is a valid no-op (operator just restarted
        // and currently owns nothing). Don't log noise.
        if (deployedPools.Count == 0)
        {
            return;
        }

        Logger.Info(
            "Reverse-sync from operator connection '{ConnectionId}': {Count} pool report(s)",
            operatorConnectionId, deployedPools.Count);

        foreach (var report in deployedPools)
        {
            // Load by repository — the pool may or may not be in the local
            // cache yet (operator can call ReportDeployedStateAsync before
            // any RegisterPoolAsync for the same pool has been processed).
            var pools = await _communicationRepository.GetPoolsAsync(report.TenantId);
            var rtPool = pools.FirstOrDefault(p => p.RtId.ToString() == report.PoolRtId);
            if (rtPool == null)
            {
                Logger.Warn(
                    "[{TenantId}] Reverse-sync: pool rtId '{PoolRtId}' reported by operator does not exist; skipping",
                    report.TenantId, report.PoolRtId);
                continue;
            }

            // Per-pool environment guard: a Cloud operator (mode check ran in
            // OperatorHub) must not be able to revive Edge-pool state via this
            // path. Mirrors the same enforcement on RegisterPoolAsync.
            if (rtPool.Environment != RtEnvironmentEnum.Cloud)
            {
                Logger.Warn(
                    "[{TenantId}] Reverse-sync: pool '{PoolName}' has Environment={Environment} (not Cloud); skipping",
                    report.TenantId, rtPool.Name, rtPool.Environment);
                continue;
            }

            // Only write when state would actually change — avoids no-op
            // SetState calls firing audit events for every report.
            if (rtPool.DeploymentState != RtDeploymentStateEnum.Deployed)
            {
                await _communicationRepository.SetPoolDeploymentStateAsync(report.TenantId, rtPool.RtId,
                    RtDeploymentStateEnum.Deployed);
                await _eventService.StoreInformationEventAsync(report.TenantId,
                    $"Pool '{rtPool.Name}' DeploymentState restored to Deployed by operator reverse-sync " +
                    $"(was {rtPool.DeploymentState}).",
                    new RtEntityId(SystemCommunicationCkIds.RtCkPoolTypeId, rtPool.RtId));
                Logger.Info(
                    "[{TenantId}] Reverse-sync: pool '{PoolName}' restored to Deployed (was {OldState})",
                    report.TenantId, rtPool.Name, rtPool.DeploymentState);
            }

            // Always rebuild the tracking + per-connection pool registration —
            // they're keyed on the new connection id, and the previous
            // connection's entries were dropped on disconnect. Idempotent if
            // the connection is already registered (ConcurrentDictionary set).
            _operatorConnectionManager.TrackDeployedPool(new DeployedPoolDto
            {
                TenantId = report.TenantId,
                PoolRtId = report.PoolRtId,
            });
            _operatorConnectionManager.RegisterPoolForConnection(operatorConnectionId, report.TenantId,
                report.PoolRtId);

            // Workloads inside the pool — same restore-only-when-changed rule.
            foreach (var workloadRtIdString in report.WorkloadRtIds)
            {
                if (!OctoObjectId.TryParse(workloadRtIdString, out var workloadRtId))
                {
                    Logger.Warn(
                        "[{TenantId}] Reverse-sync: workload rtId '{RtId}' under pool '{PoolName}' is not a valid OctoObjectId; skipping",
                        report.TenantId, workloadRtIdString, rtPool.Name);
                    continue;
                }

                var workload = await _communicationRepository.GetWorkloadByRtIdAsync(report.TenantId, workloadRtId);
                if (workload == null)
                {
                    Logger.Warn(
                        "[{TenantId}] Reverse-sync: workload rtId '{RtId}' reported by operator under pool '{PoolName}' does not exist; skipping",
                        report.TenantId, workloadRtIdString, rtPool.Name);
                    continue;
                }

                if (workload.DeploymentState != RtDeploymentStateEnum.Deployed)
                {
                    await SetWorkloadDeploymentStateAsync(report.TenantId, workload, RtDeploymentStateEnum.Deployed);
                    await _eventService.StoreInformationEventAsync(report.TenantId,
                        $"Workload '{workload.Name}' DeploymentState restored to Deployed by operator reverse-sync " +
                        $"(was {workload.DeploymentState}).");
                    Logger.Info(
                        "[{TenantId}] Reverse-sync: workload '{WorkloadName}' restored to Deployed (was {OldState})",
                        report.TenantId, workload.Name, workload.DeploymentState);
                }

                // Rebuild workload tracking so PreDeleteTenant cascade can fan
                // out undeploy events for restored workloads. Same minimal
                // DTO shape NotifyWorkloadDeployedAsync stores.
                _operatorConnectionManager.TrackDeployedWorkload(new WorkloadUndeployedDto
                {
                    TenantId = report.TenantId,
                    PoolRtId = report.PoolRtId,
                    WorkloadRtId = workloadRtIdString,
                    WorkloadName = workload.Name ?? string.Empty,
                    WorkloadType = workload is RtApplication
                        ? WorkloadTypeDto.Application
                        : WorkloadTypeDto.Adapter,
                });
            }
        }
    }
}