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

    // For each connected operator (by connectionId), the (tenant, pool) tuples
    // it has claimed via RegisterPoolForConnection. On disconnect we hand
    // these back to PoolService so the corresponding pool entities' state
    // can be flipped to Offline.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<(string TenantId, string PoolName), byte>>
        _poolsByConnection = new();

    // Tracks Cloud pools that this controller has notified operators of as
    // deployed but not yet undeployed. Source of truth for the PreDeleteTenant
    // cascade so it doesn't have to query the tenant repository (which races
    // with PreUpdatePreDeleteTenantConsumer's cache unload).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _deployedPoolsByTenant = new();

    // Tracks Cloud workloads (Adapters + Applications) deployed via the Helm
    // path. Key inside the per-tenant bucket is "{poolName}|{workloadName}".
    // The stored DTO carries pool name, workload name, and workload type — the
    // tenant-delete cascade re-emits these via NotifyWorkloadUndeployedAsync
    // so the operator runs helm uninstall before the tenant data is gone.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WorkloadUndeployedDto>> _deployedWorkloadsByTenant = new();

    public void AddOperator(string connectionId)
    {
        _connectedOperators.TryAdd(connectionId, true);
        Logger.Info("Operator added, total connected: {Count}", _connectedOperators.Count);
    }

    public IReadOnlyCollection<(string TenantId, string PoolName)> RemoveOperator(string connectionId)
    {
        _connectedOperators.TryRemove(connectionId, out _);
        var orphaned = _poolsByConnection.TryRemove(connectionId, out var bucket)
            ? bucket.Keys.ToArray()
            : [];
        Logger.Info(
            "Operator removed, total connected: {Count}, orphaned pools: {OrphanCount}",
            _connectedOperators.Count, orphaned.Length);
        return orphaned;
    }

    public void RegisterPoolForConnection(string connectionId, string tenantId, string poolName)
    {
        var bucket = _poolsByConnection.GetOrAdd(connectionId,
            _ => new ConcurrentDictionary<(string TenantId, string PoolName), byte>());
        bucket[(tenantId, poolName)] = 0;
    }

    public void UnregisterPoolForConnection(string connectionId, string tenantId, string poolName)
    {
        if (_poolsByConnection.TryGetValue(connectionId, out var bucket))
        {
            bucket.TryRemove((tenantId, poolName), out _);
            if (bucket.IsEmpty)
            {
                _poolsByConnection.TryRemove(connectionId, out _);
            }
        }
    }

    public IEnumerable<DeployedPoolDto> GetDeployedPools()
    {
        return _deployedPoolsByTenant.SelectMany(tenant =>
            tenant.Value.Keys.Select(poolName => new DeployedPoolDto
            {
                TenantId = tenant.Key,
                PoolName = poolName
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

    private static string WorkloadKey(string poolName, string workloadName) =>
        poolName + "|" + workloadName;

    /// <summary>
    /// Returns the SignalR connection ids of every operator that has claimed
    /// the (tenantId, poolName) tuple via <see cref="RegisterPoolForConnection"/>.
    /// Used to route workload deploy / undeploy events to the single operator
    /// that actually manages the target pool — central and edge operators
    /// can both be connected to the same controller, but only one of them
    /// owns any given pool. Broadcasting workload events to every connected
    /// operator was the cause of stray Helm releases on the central cluster
    /// when an edge-pool workload was deployed.
    /// </summary>
    public IReadOnlyList<string> GetConnectionsForPool(string tenantId, string poolName)
    {
        return _poolsByConnection
            .Where(kvp => kvp.Value.ContainsKey((tenantId, poolName)))
            .Select(kvp => kvp.Key)
            .ToArray();
    }

    public async Task NotifyPoolDeployedAsync(DeployedPoolDto pool)
    {
        // Track regardless of whether any operator is connected — when one
        // connects later, GetDeployedPools() / GetDeployedPoolsForTenant()
        // must still return the pool.
        var tenantPools = _deployedPoolsByTenant.GetOrAdd(pool.TenantId,
            _ => new ConcurrentDictionary<string, byte>());
        tenantPools[pool.PoolName] = 0;

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping pool-deployed notification for tenant '{TenantId}', pool '{PoolName}'",
                pool.TenantId, pool.PoolName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of pool deployed: tenant '{TenantId}', pool '{PoolName}'",
            _connectedOperators.Count, pool.TenantId, pool.PoolName);

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
                    "Failed to notify operator {ConnectionId} of pool deployment for tenant '{TenantId}', pool '{PoolName}'",
                    connectionId, pool.TenantId, pool.PoolName);
            }
        }
    }

    public async Task NotifyPoolUndeployedAsync(string tenantId, string poolName)
    {
        if (_deployedPoolsByTenant.TryGetValue(tenantId, out var tenantPools))
        {
            tenantPools.TryRemove(poolName, out _);
            if (tenantPools.IsEmpty)
            {
                _deployedPoolsByTenant.TryRemove(tenantId, out _);
            }
        }

        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug(
                "No operators connected, skipping pool-undeployed notification for tenant '{TenantId}', pool '{PoolName}'",
                tenantId, poolName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of pool undeployed: tenant '{TenantId}', pool '{PoolName}'",
            _connectedOperators.Count, tenantId, poolName);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.PoolUndeployedAsync), tenantId, poolName);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to notify operator {ConnectionId} of pool undeployment for tenant '{TenantId}', pool '{PoolName}'",
                    connectionId, tenantId, poolName);
            }
        }
    }

    public async Task NotifyWorkloadDeployedAsync(WorkloadDeployedDto workload)
    {
        // Track regardless of whether any operator is connected. Stored DTO is
        // the minimal undeploy payload so the cascade can use it as-is.
        var tenantWorkloads = _deployedWorkloadsByTenant.GetOrAdd(workload.TenantId,
            _ => new ConcurrentDictionary<string, WorkloadUndeployedDto>());
        tenantWorkloads[WorkloadKey(workload.PoolName, workload.WorkloadName)] = new WorkloadUndeployedDto
        {
            TenantId = workload.TenantId,
            PoolName = workload.PoolName,
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
        var targetConnections = GetConnectionsForPool(workload.TenantId, workload.PoolName);
        if (targetConnections.Count == 0)
        {
            Logger.Warn(
                "No operator currently owns pool '{PoolName}' for tenant '{TenantId}'; skipping workload-deployed notification for '{WorkloadName}'",
                workload.PoolName, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload deployed: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', chart '{ChartName}:{ChartVersion}'",
            targetConnections.Count, workload.TenantId, workload.PoolName, workload.WorkloadName,
            workload.ChartName, workload.ChartVersion);

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
                    "Failed to notify operator {ConnectionId} of workload deployment for tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.PoolName, workload.WorkloadName);
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
            tenantWorkloads.TryRemove(WorkloadKey(workload.PoolName, workload.WorkloadName), out _);
            if (tenantWorkloads.IsEmpty)
            {
                _deployedWorkloadsByTenant.TryRemove(workload.TenantId, out _);
            }
        }

        // Same pool-scoped routing as NotifyWorkloadDeployedAsync.
        var targetConnections = GetConnectionsForPool(workload.TenantId, workload.PoolName);
        if (targetConnections.Count == 0)
        {
            Logger.Warn(
                "No operator currently owns pool '{PoolName}' for tenant '{TenantId}'; skipping workload-undeployed notification for '{WorkloadName}'",
                workload.PoolName, workload.TenantId, workload.WorkloadName);
            return;
        }

        Logger.Info(
            "Notifying {Count} operator(s) of workload undeployed: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}'",
            targetConnections.Count, workload.TenantId, workload.PoolName, workload.WorkloadName);

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
                    "Failed to notify operator {ConnectionId} of workload undeployment for tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}'",
                    connectionId, workload.TenantId, workload.PoolName, workload.WorkloadName);
            }
        }
    }
}
