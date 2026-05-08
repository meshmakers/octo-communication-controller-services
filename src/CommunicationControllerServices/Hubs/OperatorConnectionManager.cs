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
        // TODO: enumerate all Cloud pools currently in DeploymentState=Deployed
        // across every tenant. For now we return empty: live deploy/undeploy
        // events still flow correctly; only operator-restart sync is missing.
        return [];
    }

    public async Task NotifyPoolDeployedAsync(DeployedPoolDto pool)
    {
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
