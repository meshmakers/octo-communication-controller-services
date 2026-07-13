using System.Collections.Concurrent;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

internal class OperatorConnectionManager(IHubContext<OperatorHub> hubContext) : IOperatorConnectionManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<string, bool> _connectedOperators = new();

    // Per-connection AutoManagePools mode declared via RegisterOperatorAsync.
    // Used by OperatorHub.RegisterPoolAsync to reject a pool whose Environment
    // does not match the calling operator's mode (a Cloud pool claimed by an
    // edge operator, or an Edge pool claimed by the central operator). A
    // missing entry means the operator did not declare a mode (legacy build
    // or never called RegisterOperatorAsync) — enforcement is skipped in
    // that case to keep rolling upgrades safe.
    private readonly ConcurrentDictionary<string, bool> _operatorModeByConnection = new();

    // For each connected operator (by connectionId), the (tenant, poolRtId)
    // tuples it has claimed via RegisterPoolForConnection. On disconnect we
    // hand these back to PoolService so the corresponding pool entities'
    // state can be flipped to Offline. The dictionary value is unused —
    // ConcurrentHashSet does not exist, so a bool sentinel emulates a set.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string TenantId, string PoolRtId), bool>>
        _poolsByConnection = new();

    // Tracks Cloud pools that this controller has notified operators of as
    // deployed but not yet undeployed. Source of truth for the PreDeleteTenant
    // cascade so it doesn't have to query the tenant repository (which races
    // with PreUpdatePreDeleteTenantConsumer's cache unload). Keyed by poolRtId
    // (DNS-safe, stable across CK pool renames).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _deployedPoolsByTenant = new();

    // Tracks Cloud workloads (Adapters + Applications) deployed via the Helm
    // path. Key inside the per-tenant bucket is the workload RtId — also
    // DNS-safe and stable across CK renames. The stored DTO carries every
    // identifier the tenant-delete cascade needs to re-emit
    // NotifyWorkloadUndeployedAsync.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkloadUndeployedDto>> _deployedWorkloadsByTenant = new();

    // Workload deploy/undeploy notifications that could not be routed because
    // no operator connection owned the target pool at notify time (AB#4371 —
    // e.g. the operator's pool registration was rejected transiently and the
    // pool stayed orphaned until a later retry/reconnect). Keyed by
    // (tenant, poolRtId); the inner map is last-wins per workload rtId so an
    // undeploy supersedes a queued deploy of the same workload and vice
    // versa. Values are either WorkloadDeployedDto or WorkloadUndeployedDto.
    // Replayed by FlushPendingWorkloadNotificationsAsync when an operator
    // registers the pool. In-memory by design, like the rest of the tracking
    // here: a controller restart clears the queue and the operator-side
    // reverse-sync plus the next user-triggered deploy/undeploy re-establish
    // state.
    private readonly ConcurrentDictionary<(string TenantId, string PoolRtId), ConcurrentDictionary<string, object>>
        _pendingWorkloadNotificationsByPool = new();

    public void AddOperator(string connectionId)
    {
        _connectedOperators.TryAdd(connectionId, true);
        Logger.Info("Operator added, total connected: {Count}", _connectedOperators.Count);
    }

    public IReadOnlyCollection<(string TenantId, string PoolRtId)> RemoveOperator(string connectionId)
    {
        _connectedOperators.TryRemove(connectionId, out _);
        _operatorModeByConnection.TryRemove(connectionId, out _);
        var orphaned = _poolsByConnection.TryRemove(connectionId, out var bucket)
            ? bucket.Keys.ToArray()
            : [];
        Logger.Info(
            "Operator removed, total connected: {Count}, orphaned pools: {OrphanCount}",
            _connectedOperators.Count, orphaned.Length);
        return orphaned;
    }

    public void SetOperatorMode(string connectionId, bool? autoManagePools)
    {
        if (autoManagePools.HasValue)
        {
            _operatorModeByConnection[connectionId] = autoManagePools.Value;
        }
        else
        {
            // Legacy operator: leave the entry absent so GetOperatorMode
            // returns null and OperatorHub.RegisterPoolAsync skips enforcement.
            _operatorModeByConnection.TryRemove(connectionId, out _);
        }
    }

    public bool? GetOperatorMode(string connectionId)
    {
        return _operatorModeByConnection.TryGetValue(connectionId, out var mode)
            ? mode
            : null;
    }

    public void RegisterPoolForConnection(string connectionId, string tenantId, string poolRtId)
    {
        var bucket = _poolsByConnection.GetOrAdd(connectionId,
            _ => new ConcurrentDictionary<(string TenantId, string PoolRtId), bool>());
        bucket[(tenantId, poolRtId)] = true;
    }

    public void UnregisterPoolForConnection(string connectionId, string tenantId, string poolRtId)
    {
        if (_poolsByConnection.TryGetValue(connectionId, out var bucket))
        {
            bucket.TryRemove((tenantId, poolRtId), out _);
            if (bucket.IsEmpty)
            {
                _poolsByConnection.TryRemove(connectionId, out _);
            }
        }
    }

    public IEnumerable<DeployedPoolDto> GetDeployedPools()
    {
        return _deployedPoolsByTenant.SelectMany(tenant =>
            tenant.Value.Select(pool => new DeployedPoolDto
            {
                TenantId = tenant.Key,
                PoolRtId = pool.Key,
            })).ToArray();
    }

    public IReadOnlyCollection<string> GetDeployedPoolsForTenant(string tenantId)
    {
        return _deployedPoolsByTenant.TryGetValue(tenantId, out var pools)
            ? pools.Keys.ToArray()
            : [];
    }

    public IReadOnlyCollection<WorkloadUndeployedDto> GetDeployedWorkloadsForTenant(string tenantId)
    {
        return _deployedWorkloadsByTenant.TryGetValue(tenantId, out var workloads)
            ? workloads.Values.ToArray()
            : [];
    }

    /// <summary>
    /// Returns the SignalR connection ids of every operator that has claimed
    /// the (tenantId, poolRtId) tuple via <see cref="RegisterPoolForConnection"/>.
    /// Used to route workload deploy / undeploy events to the single operator
    /// that actually manages the target pool — central and edge operators
    /// can both be connected to the same controller, but only one of them
    /// owns any given pool. Broadcasting workload events to every connected
    /// operator was the cause of stray Helm releases on the central cluster
    /// when an edge-pool workload was deployed.
    /// </summary>
    public IReadOnlyList<string> GetConnectionsForPool(string tenantId, string poolRtId)
    {
        return _poolsByConnection
            .Where(kvp => kvp.Value.ContainsKey((tenantId, poolRtId)))
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    public void TrackDeployedPool(DeployedPoolDto pool)
    {
        // Mirror NotifyPoolDeployedAsync's tracking write but skip the SignalR
        // fan-out — the operator is reporting state it already owns, no need
        // to echo a PoolDeployedAsync back at it.
        var tenantPools = _deployedPoolsByTenant.GetOrAdd(pool.TenantId,
            _ => new ConcurrentDictionary<string, bool>());
        tenantPools[pool.PoolRtId] = true;
    }

    public void TrackDeployedWorkload(WorkloadUndeployedDto workload)
    {
        // Companion to TrackDeployedPool — the stored DTO is the minimal
        // undeploy payload, same shape NotifyWorkloadDeployedAsync writes.
        var tenantWorkloads = _deployedWorkloadsByTenant.GetOrAdd(workload.TenantId,
            _ => new ConcurrentDictionary<string, WorkloadUndeployedDto>());
        tenantWorkloads[workload.WorkloadRtId] = workload;
    }

    public async Task NotifyPoolDeployedAsync(DeployedPoolDto pool)
    {
        // Track regardless of whether any operator is connected — when one
        // connects later, GetDeployedPools() / GetDeployedPoolsForTenant()
        // must still return the pool.
        var tenantPools = _deployedPoolsByTenant.GetOrAdd(pool.TenantId,
            _ => new ConcurrentDictionary<string, bool>());
        tenantPools[pool.PoolRtId] = true;

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping pool-deployed notification for tenant '{TenantId}', pool rtId {PoolRtId}",
                pool.TenantId, pool.PoolRtId);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of pool deployed: tenant '{TenantId}', pool rtId {PoolRtId}",
            _connectedOperators.Count, pool.TenantId, pool.PoolRtId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.PoolDeployedAsync), pool);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of pool deployment for tenant '{TenantId}', pool rtId {PoolRtId}",
                    connectionId, pool.TenantId, pool.PoolRtId);
            }
        }
    }

    public async Task NotifyPoolUndeployedAsync(string tenantId, string poolRtId)
    {
        if (_deployedPoolsByTenant.TryGetValue(tenantId, out var tenantPools))
        {
            tenantPools.TryRemove(poolRtId, out _);
            if (tenantPools.IsEmpty)
            {
                _deployedPoolsByTenant.TryRemove(tenantId, out _);
            }
        }

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping pool-undeployed notification for tenant '{TenantId}', pool rtId {PoolRtId}",
                tenantId, poolRtId);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of pool undeployed: tenant '{TenantId}', pool rtId {PoolRtId}",
            _connectedOperators.Count, tenantId, poolRtId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.PoolUndeployedAsync), tenantId, poolRtId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of pool undeployment for tenant '{TenantId}', pool rtId {PoolRtId}",
                    connectionId, tenantId, poolRtId);
            }
        }
    }

    public async Task NotifyWorkloadDeployedAsync(WorkloadDeployedDto workload)
    {
        // Track regardless of whether any operator is connected. Stored DTO is
        // the minimal undeploy payload so the cascade can use it as-is.
        var tenantWorkloads = _deployedWorkloadsByTenant.GetOrAdd(workload.TenantId,
            _ => new ConcurrentDictionary<string, WorkloadUndeployedDto>());
        tenantWorkloads[workload.WorkloadRtId] = new WorkloadUndeployedDto
        {
            TenantId = workload.TenantId,
            PoolRtId = workload.PoolRtId,
            WorkloadRtId = workload.WorkloadRtId,
            WorkloadName = workload.WorkloadName,
            WorkloadType = workload.WorkloadType,
        };

        // Route only to the operator(s) that actually own this pool. Workload
        // deploys are pool-scoped: a central operator and an edge operator
        // can both be connected to the same controller, but the workload
        // must only be deployed by the one that manages the target pool.
        // Broadcasting to every connected operator caused a stray Helm
        // release on the central cluster whenever a workload assigned to an
        // edge pool was deployed (the central operator happily ran the
        // helm-install against its own namespace and reported success, which
        // then overwrote the edge operator's failure on the runtime entity).
        var targetConnections = GetConnectionsForPool(workload.TenantId, workload.PoolRtId);
        if (targetConnections.Count == 0)
        {
            // Don't drop the event — the pool may be orphaned only
            // transiently (AB#4371). Queue it for replay when an operator
            // registers the pool.
            QueuePendingWorkloadNotification(workload.TenantId, workload.PoolRtId,
                workload.WorkloadRtId, workload);
            Logger.Warn(
                "No operator currently owns pool rtId {PoolRtId} for tenant '{TenantId}'; queueing workload-deployed notification for '{WorkloadName}' until the pool is registered",
                workload.PoolRtId, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload deployed: tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId}), chart '{ChartName}:{ChartVersion}'",
            targetConnections.Count, workload.TenantId, workload.PoolRtId,
            workload.WorkloadName, workload.WorkloadRtId, workload.ChartName, workload.ChartVersion);

        foreach (var connectionId in targetConnections)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.WorkloadDeployedAsync), workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of workload deployment for tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.PoolRtId, workload.WorkloadName);
            }
        }
    }

    public async Task NotifyPreUpdateTenantAsync(string tenantId)
    {
        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug("No operators connected, skipping pre-update notification for tenant '{TenantId}'", tenantId);
            return;
        }

        Logger.Info("Notifying {Count} operator(s) of pre-update for tenant '{TenantId}'",
            _connectedOperators.Count, tenantId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.PreUpdateTenantAsync), tenantId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of pre-update for tenant '{TenantId}'",
                    connectionId, tenantId);
            }
        }
    }

    public async Task NotifyWorkloadUndeployedAsync(WorkloadUndeployedDto workload)
    {
        if (_deployedWorkloadsByTenant.TryGetValue(workload.TenantId, out var tenantWorkloads))
        {
            tenantWorkloads.TryRemove(workload.WorkloadRtId, out _);
            if (tenantWorkloads.IsEmpty)
            {
                _deployedWorkloadsByTenant.TryRemove(workload.TenantId, out _);
            }
        }

        // Same pool-scoped routing as NotifyWorkloadDeployedAsync.
        var targetConnections = GetConnectionsForPool(workload.TenantId, workload.PoolRtId);
        if (targetConnections.Count == 0)
        {
            // Don't drop the event (AB#4371) — a dropped undeploy leaves the
            // helm release running forever while the entity says Undeployed.
            // Queue it; last-wins per workload rtId also cancels out a queued
            // deploy for the same workload.
            QueuePendingWorkloadNotification(workload.TenantId, workload.PoolRtId,
                workload.WorkloadRtId, workload);
            Logger.Warn(
                "No operator currently owns pool rtId {PoolRtId} for tenant '{TenantId}'; queueing workload-undeployed notification for '{WorkloadName}' until the pool is registered",
                workload.PoolRtId, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload undeployed: tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId})",
            targetConnections.Count, workload.TenantId, workload.PoolRtId,
            workload.WorkloadName, workload.WorkloadRtId);

        foreach (var connectionId in targetConnections)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync), workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of workload undeployment for tenant '{TenantId}', pool rtId {PoolRtId}, workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.PoolRtId, workload.WorkloadName);
            }
        }
    }

    public async Task FlushPendingWorkloadNotificationsAsync(string connectionId, string tenantId, string poolRtId)
    {
        if (!_pendingWorkloadNotificationsByPool.TryRemove((tenantId, poolRtId), out var pending)
            || pending.IsEmpty)
        {
            return;
        }

        Logger.Info(
            "Replaying {Count} queued workload notification(s) for pool rtId {PoolRtId} (tenant '{TenantId}') to operator {ConnectionId}",
            pending.Count, poolRtId, tenantId, connectionId);

        foreach (var (workloadRtId, notification) in pending)
        {
            var methodName = notification is WorkloadDeployedDto
                ? nameof(IOperatorHubCallbacks.WorkloadDeployedAsync)
                : nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync);
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(methodName, notification);
            }
            catch (Exception ex)
            {
                // Put it back so the next registration of this pool retries
                // the replay — dropping it here would reintroduce the very
                // bug this queue exists to fix.
                QueuePendingWorkloadNotification(tenantId, poolRtId, workloadRtId, notification);
                Logger.Warn(ex,
                    "Failed to replay queued workload notification for workload rtId {WorkloadRtId} (tenant '{TenantId}', pool rtId {PoolRtId}); re-queued",
                    workloadRtId, tenantId, poolRtId);
            }
        }
    }

    private void QueuePendingWorkloadNotification(string tenantId, string poolRtId,
        string workloadRtId, object notification)
    {
        var pending = _pendingWorkloadNotificationsByPool.GetOrAdd((tenantId, poolRtId),
            _ => new ConcurrentDictionary<string, object>());
        pending[workloadRtId] = notification;
    }
}
