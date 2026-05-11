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

    // Tracks Cloud pools that this controller has notified operators of as
    // deployed but not yet undeployed. Source of truth for the PreDeleteTenant
    // cascade so it doesn't have to query the tenant repository (which races
    // with PreUpdatePreDeleteTenantConsumer's cache unload).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _deployedPoolsByTenant = new();

    public void AddOperator(string connectionId)
    {
        _connectedOperators.TryAdd(connectionId, true);
        Logger.Info("Operator added, total connected: {Count}", _connectedOperators.Count);
    }

    public void RemoveOperator(string connectionId)
    {
        _connectedOperators.TryRemove(connectionId, out _);
        Logger.Info("Operator removed, total connected: {Count}", _connectedOperators.Count);
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
}
