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

    // Per-connection AutoManageDeploymentSites mode declared via RegisterOperatorAsync.
    // Used by OperatorHub.RegisterDeploymentSiteAsync to reject a deploymentSite whose Environment
    // does not match the calling operator's mode (a Cloud deploymentSite claimed by an
    // edge operator, or an Edge deploymentSite claimed by the central operator). A
    // missing entry means the operator did not declare a mode (legacy build
    // or never called RegisterOperatorAsync) — enforcement is skipped in
    // that case to keep rolling upgrades safe.
    private readonly ConcurrentDictionary<string, bool> _operatorModeByConnection = new();

    // For each connected operator (by connectionId), the (tenant, deploymentSiteRtId)
    // tuples it has claimed via RegisterDeploymentSiteForConnection. On disconnect we
    // hand these back to DeploymentSiteService so the corresponding deploymentSite entities'
    // state can be flipped to Offline. The dictionary value is unused —
    // ConcurrentHashSet does not exist, so a bool sentinel emulates a set.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string TenantId, string DeploymentSiteRtId), bool>>
        _poolsByConnection = new();

    // Tracks Cloud deploymentSites that this controller has notified operators of as
    // deployed but not yet undeployed. Source of truth for the PreDeleteTenant
    // cascade so it doesn't have to query the tenant repository (which races
    // with PreUpdatePreDeleteTenantConsumer's cache unload). Keyed by deploymentSiteRtId
    // (DNS-safe, stable across CK deploymentSite renames).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _deployedDeploymentSitesByTenant = new();

    // Tracks Cloud workloads (Adapters + Applications) deployed via the Helm
    // path. Key inside the per-tenant bucket is the workload RtId — also
    // DNS-safe and stable across CK renames. The stored DTO carries every
    // identifier the tenant-delete cascade needs to re-emit
    // NotifyWorkloadUndeployedAsync.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkloadUndeployedDto>> _deployedWorkloadsByTenant = new();

    // Workload deploy/undeploy notifications that could not be routed because
    // no operator connection owned the target deploymentSite at notify time (AB#4371 —
    // e.g. the operator's deploymentSite registration was rejected transiently and the
    // deploymentSite stayed orphaned until a later retry/reconnect). Keyed by
    // (tenant, deploymentSiteRtId); the inner map is last-wins per workload rtId so an
    // undeploy supersedes a queued deploy of the same workload and vice
    // versa. Values are either WorkloadDeployedDto or WorkloadUndeployedDto.
    // Replayed by FlushPendingWorkloadNotificationsAsync when an operator
    // registers the deploymentSite. In-memory by design, like the rest of the tracking
    // here: a controller restart clears the queue and the operator-side
    // reverse-sync plus the next user-triggered deploy/undeploy re-establish
    // state.
    private readonly ConcurrentDictionary<(string TenantId, string DeploymentSiteRtId), ConcurrentDictionary<string, object>>
        _pendingWorkloadNotificationsByDeploymentSite = new();

    public void AddOperator(string connectionId)
    {
        _connectedOperators.TryAdd(connectionId, true);
        Logger.Info("Operator added, total connected: {Count}", _connectedOperators.Count);
    }

    public IReadOnlyCollection<(string TenantId, string DeploymentSiteRtId)> RemoveOperator(string connectionId)
    {
        _connectedOperators.TryRemove(connectionId, out _);
        _operatorModeByConnection.TryRemove(connectionId, out _);
        var orphaned = _poolsByConnection.TryRemove(connectionId, out var bucket)
            ? bucket.Keys.ToArray()
            : [];
        Logger.Info(
            "Operator removed, total connected: {Count}, orphaned deploymentSites: {OrphanCount}",
            _connectedOperators.Count, orphaned.Length);
        return orphaned;
    }

    public void SetOperatorMode(string connectionId, bool? autoManageDeploymentSites)
    {
        if (autoManageDeploymentSites.HasValue)
        {
            _operatorModeByConnection[connectionId] = autoManageDeploymentSites.Value;
        }
        else
        {
            // Legacy operator: leave the entry absent so GetOperatorMode
            // returns null and OperatorHub.RegisterDeploymentSiteAsync skips enforcement.
            _operatorModeByConnection.TryRemove(connectionId, out _);
        }
    }

    public bool? GetOperatorMode(string connectionId)
    {
        return _operatorModeByConnection.TryGetValue(connectionId, out var mode)
            ? mode
            : null;
    }

    public void RegisterDeploymentSiteForConnection(string connectionId, string tenantId, string deploymentSiteRtId)
    {
        var bucket = _poolsByConnection.GetOrAdd(connectionId,
            _ => new ConcurrentDictionary<(string TenantId, string DeploymentSiteRtId), bool>());
        bucket[(tenantId, deploymentSiteRtId)] = true;
    }

    public void UnregisterDeploymentSiteForConnection(string connectionId, string tenantId, string deploymentSiteRtId)
    {
        if (_poolsByConnection.TryGetValue(connectionId, out var bucket))
        {
            bucket.TryRemove((tenantId, deploymentSiteRtId), out _);
            if (bucket.IsEmpty)
            {
                _poolsByConnection.TryRemove(connectionId, out _);
            }
        }
    }

    public IEnumerable<DeployedDeploymentSiteDto> GetDeployedDeploymentSites()
    {
        return _deployedDeploymentSitesByTenant.SelectMany(tenant =>
            tenant.Value.Select(deploymentSite => new DeployedDeploymentSiteDto
            {
                TenantId = tenant.Key,
                DeploymentSiteRtId = deploymentSite.Key,
            })).ToArray();
    }

    public IReadOnlyCollection<string> GetDeployedDeploymentSitesForTenant(string tenantId)
    {
        return _deployedDeploymentSitesByTenant.TryGetValue(tenantId, out var deploymentSites)
            ? deploymentSites.Keys.ToArray()
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
    /// the (tenantId, deploymentSiteRtId) tuple via <see cref="RegisterDeploymentSiteForConnection"/>.
    /// Used to route workload deploy / undeploy events to the single operator
    /// that actually manages the target deploymentSite — central and edge operators
    /// can both be connected to the same controller, but only one of them
    /// owns any given deploymentSite. Broadcasting workload events to every connected
    /// operator was the cause of stray Helm releases on the central cluster
    /// when an edge-deploymentSite workload was deployed.
    /// </summary>
    public IReadOnlyList<string> GetConnectionsForDeploymentSite(string tenantId, string deploymentSiteRtId)
    {
        return _poolsByConnection
            .Where(kvp => kvp.Value.ContainsKey((tenantId, deploymentSiteRtId)))
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    public void TrackDeployedDeploymentSite(DeployedDeploymentSiteDto deploymentSite)
    {
        // Mirror NotifyDeploymentSiteDeployedAsync's tracking write but skip the SignalR
        // fan-out — the operator is reporting state it already owns, no need
        // to echo a PoolDeployedAsync back at it.
        var tenantDeploymentSites = _deployedDeploymentSitesByTenant.GetOrAdd(deploymentSite.TenantId,
            _ => new ConcurrentDictionary<string, bool>());
        tenantDeploymentSites[deploymentSite.DeploymentSiteRtId] = true;
    }

    public void TrackDeployedWorkload(WorkloadUndeployedDto workload)
    {
        // Companion to TrackDeployedDeploymentSite — the stored DTO is the minimal
        // undeploy payload, same shape NotifyWorkloadDeployedAsync writes.
        var tenantWorkloads = _deployedWorkloadsByTenant.GetOrAdd(workload.TenantId,
            _ => new ConcurrentDictionary<string, WorkloadUndeployedDto>());
        tenantWorkloads[workload.WorkloadRtId] = workload;
    }

    public async Task NotifyDeploymentSiteDeployedAsync(DeployedDeploymentSiteDto deploymentSite)
    {
        // Track regardless of whether any operator is connected — when one
        // connects later, GetDeployedDeploymentSites() / GetDeployedDeploymentSitesForTenant()
        // must still return the deploymentSite.
        var tenantDeploymentSites = _deployedDeploymentSitesByTenant.GetOrAdd(deploymentSite.TenantId,
            _ => new ConcurrentDictionary<string, bool>());
        tenantDeploymentSites[deploymentSite.DeploymentSiteRtId] = true;

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping deploymentSite-deployed notification for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of deploymentSite deployed: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            _connectedOperators.Count, deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.DeploymentSiteDeployedAsync), deploymentSite);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of deploymentSite deployment for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                    connectionId, deploymentSite.TenantId, deploymentSite.DeploymentSiteRtId);
            }
        }
    }

    public async Task NotifyDeploymentSiteUndeployedAsync(string tenantId, string deploymentSiteRtId)
    {
        if (_deployedDeploymentSitesByTenant.TryGetValue(tenantId, out var tenantDeploymentSites))
        {
            tenantDeploymentSites.TryRemove(deploymentSiteRtId, out _);
            if (tenantDeploymentSites.IsEmpty)
            {
                _deployedDeploymentSitesByTenant.TryRemove(tenantId, out _);
            }
        }

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping deploymentSite-undeployed notification for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                tenantId, deploymentSiteRtId);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of deploymentSite undeployed: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
            _connectedOperators.Count, tenantId, deploymentSiteRtId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.DeploymentSiteUndeployedAsync), tenantId, deploymentSiteRtId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of deploymentSite undeployment for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}",
                    connectionId, tenantId, deploymentSiteRtId);
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
            DeploymentSiteRtId = workload.DeploymentSiteRtId,
            WorkloadRtId = workload.WorkloadRtId,
            WorkloadName = workload.WorkloadName,
            WorkloadType = workload.WorkloadType,
        };

        // Route only to the operator(s) that actually own this deploymentSite. Workload
        // deploys are deploymentSite-scoped: a central operator and an edge operator
        // can both be connected to the same controller, but the workload
        // must only be deployed by the one that manages the target deploymentSite.
        // Broadcasting to every connected operator caused a stray Helm
        // release on the central cluster whenever a workload assigned to an
        // edge deploymentSite was deployed (the central operator happily ran the
        // helm-install against its own namespace and reported success, which
        // then overwrote the edge operator's failure on the runtime entity).
        var targetConnections = GetConnectionsForDeploymentSite(workload.TenantId, workload.DeploymentSiteRtId);
        if (targetConnections.Count == 0)
        {
            // Don't drop the event — the deploymentSite may be orphaned only
            // transiently (AB#4371). Queue it for replay when an operator
            // registers the deploymentSite.
            QueuePendingWorkloadNotification(workload.TenantId, workload.DeploymentSiteRtId,
                workload.WorkloadRtId, workload);
            Logger.Warn(
                "No operator currently owns deployment site rtId {DeploymentSiteRtId} for tenant '{TenantId}'; queueing workload-deployed notification for '{WorkloadName}' until the deploymentSite is registered",
                workload.DeploymentSiteRtId, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload deployed: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId}), chart '{ChartName}:{ChartVersion}'",
            targetConnections.Count, workload.TenantId, workload.DeploymentSiteRtId,
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
                    "Failed to notify operator {ConnectionId} of workload deployment for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName);
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

        // Same deploymentSite-scoped routing as NotifyWorkloadDeployedAsync.
        var targetConnections = GetConnectionsForDeploymentSite(workload.TenantId, workload.DeploymentSiteRtId);
        if (targetConnections.Count == 0)
        {
            // Don't drop the event (AB#4371) — a dropped undeploy leaves the
            // helm release running forever while the entity says Undeployed.
            // Queue it; last-wins per workload rtId also cancels out a queued
            // deploy for the same workload.
            QueuePendingWorkloadNotification(workload.TenantId, workload.DeploymentSiteRtId,
                workload.WorkloadRtId, workload);
            Logger.Warn(
                "No operator currently owns deployment site rtId {DeploymentSiteRtId} for tenant '{TenantId}'; queueing workload-undeployed notification for '{WorkloadName}' until the deploymentSite is registered",
                workload.DeploymentSiteRtId, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload undeployed: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId})",
            targetConnections.Count, workload.TenantId, workload.DeploymentSiteRtId,
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
                    "Failed to notify operator {ConnectionId} of workload undeployment for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName);
            }
        }
    }

    public async Task NotifyWorkloadScaleAsync(ScaleWorkloadDto workload)
    {
        // No tracking-map updates: scaling does not change what is deployed —
        // a hibernated workload keeps its helm release and must still be
        // covered by the tenant-delete cascade.
        var targetConnections = GetConnectionsForDeploymentSite(workload.TenantId, workload.DeploymentSiteRtId);
        if (targetConnections.Count == 0)
        {
            // Same AB#4371 rationale as deploy/undeploy: the deploymentSite may be
            // orphaned only transiently (operator mid-rollout), and a dropped
            // scale-1 leaves a wake gate waiting for its full budget. Queued
            // under a scale-specific key so a scale never supersedes a queued
            // deploy/undeploy of the same workload (which the last-wins map
            // would otherwise silently drop); among scales last-wins is
            // exactly right.
            QueuePendingWorkloadNotification(workload.TenantId, workload.DeploymentSiteRtId,
                ScalePendingKey(workload.WorkloadRtId), workload);
            Logger.Warn(
                "No operator currently owns deployment site rtId {DeploymentSiteRtId} for tenant '{TenantId}'; queueing workload-scale notification for '{WorkloadName}' (replicas {Replicas}) until the deploymentSite is registered",
                workload.DeploymentSiteRtId, workload.TenantId, workload.WorkloadName, workload.Replicas);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload scale: tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}' (rtId {WorkloadRtId}), replicas {Replicas}",
            targetConnections.Count, workload.TenantId, workload.DeploymentSiteRtId,
            workload.WorkloadName, workload.WorkloadRtId, workload.Replicas);

        foreach (var connectionId in targetConnections)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.ScaleWorkloadAsync), workload);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of workload scale for tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}, workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.DeploymentSiteRtId, workload.WorkloadName);
            }
        }
    }

    private static string ScalePendingKey(string workloadRtId)
    {
        // RtIds are 24-hex, so the suffix cannot collide with a real rtId key.
        return workloadRtId + "::scale";
    }

    public async Task FlushPendingWorkloadNotificationsAsync(string connectionId, string tenantId, string deploymentSiteRtId)
    {
        if (!_pendingWorkloadNotificationsByDeploymentSite.TryRemove((tenantId, deploymentSiteRtId), out var pending)
            || pending.IsEmpty)
        {
            return;
        }

        Logger.Info(
            "Replaying {Count} queued workload notification(s) for deployment site rtId {DeploymentSiteRtId} (tenant '{TenantId}') to operator {ConnectionId}",
            pending.Count, deploymentSiteRtId, tenantId, connectionId);

        foreach (var (workloadRtId, notification) in pending)
        {
            var methodName = notification switch
            {
                WorkloadDeployedDto => nameof(IOperatorHubCallbacks.WorkloadDeployedAsync),
                ScaleWorkloadDto => nameof(IOperatorHubCallbacks.ScaleWorkloadAsync),
                _ => nameof(IOperatorHubCallbacks.WorkloadUndeployedAsync),
            };
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(methodName, notification);
            }
            catch (Exception ex)
            {
                // Put it back so the next registration of this deploymentSite retries
                // the replay — dropping it here would reintroduce the very
                // bug this queue exists to fix.
                QueuePendingWorkloadNotification(tenantId, deploymentSiteRtId, workloadRtId, notification);
                Logger.Warn(ex,
                    "Failed to replay queued workload notification for workload rtId {WorkloadRtId} (tenant '{TenantId}', deployment site rtId {DeploymentSiteRtId}); re-queued",
                    workloadRtId, tenantId, deploymentSiteRtId);
            }
        }
    }

    private void QueuePendingWorkloadNotification(string tenantId, string deploymentSiteRtId,
        string workloadRtId, object notification)
    {
        var pending = _pendingWorkloadNotificationsByDeploymentSite.GetOrAdd((tenantId, deploymentSiteRtId),
            _ => new ConcurrentDictionary<string, object>());
        pending[workloadRtId] = notification;
    }
}
