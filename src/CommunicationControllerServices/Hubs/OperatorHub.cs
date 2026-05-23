using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for operator management connections.
/// Operators register here to receive tenant lifecycle notifications,
/// register / unregister pools they own, and report workload deploy
/// outcomes. Not tenant-scoped — one operator process keeps one
/// connection regardless of how many pools / tenants it manages.
/// </summary>
public class OperatorHub : Hub, IOperatorHub
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IOperatorConnectionManager _connectionManager;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IPoolService _poolService;

    /// <summary>
    /// Constructor
    /// </summary>
    public OperatorHub(IOperatorConnectionManager connectionManager,
        ICommunicationRepository communicationRepository,
        IPoolService poolService)
    {
        _connectionManager = connectionManager;
        _communicationRepository = communicationRepository;
        _poolService = poolService;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        Logger.Info("Operator connected with connection id '{ConnectionId}'", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var disconnectingConnectionId = Context.ConnectionId;
        Logger.Info("Operator disconnected with connection id '{ConnectionId}'", disconnectingConnectionId);
        // Drop the connection-level entry and reset every pool it claimed.
        // Same call site whether the disconnect was graceful (operator
        // shutdown) or a crash — the hub guarantees this fires exactly once.
        // The disconnecting connection id is passed to PoolService so a stale
        // disconnect (a previous connection's handler firing late, after a
        // newer connection has already taken over) does not overwrite the
        // Online state written by the newer connection.
        var orphaned = _connectionManager.RemoveOperator(disconnectingConnectionId);
        foreach (var (tenantId, poolRtId, poolName) in orphaned)
        {
            try
            {
                await _poolService.SetCommunicationStateOfflineAsync(tenantId,
                    new OctoObjectId(poolRtId), disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark pool '{PoolName}' (rtId {PoolRtId}) offline after operator disconnect (tenant '{TenantId}')",
                    poolName, poolRtId, tenantId);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public Task<IEnumerable<DeployedPoolDto>> RegisterOperatorAsync()
    {
        Logger.Info("Operator registered with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.AddOperator(Context.ConnectionId);
        return Task.FromResult(_connectionManager.GetDeployedPools());
    }

    /// <inheritdoc />
    public async Task UnregisterOperatorAsync()
    {
        var disconnectingConnectionId = Context.ConnectionId;
        Logger.Info("Operator unregistered with connection id '{ConnectionId}'", disconnectingConnectionId);
        var orphaned = _connectionManager.RemoveOperator(disconnectingConnectionId);
        foreach (var (tenantId, poolRtId, poolName) in orphaned)
        {
            try
            {
                await _poolService.SetCommunicationStateOfflineAsync(tenantId,
                    new OctoObjectId(poolRtId), disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark pool '{PoolName}' (rtId {PoolRtId}) offline on operator unregister (tenant '{TenantId}')",
                    poolName, poolRtId, tenantId);
            }
        }
    }

    /// <inheritdoc />
    public async Task RegisterPoolAsync(string tenantId, string poolRtId, string poolName)
    {
        Logger.Info(
            "Operator '{ConnectionId}' claims pool '{PoolName}' (rtId {PoolRtId}) for tenant '{TenantId}'",
            Context.ConnectionId, poolName, poolRtId, tenantId);

        // Track the (connection, tenant, pool) tuple before flipping state —
        // if state-write fails we still want OnDisconnectedAsync to clean
        // up so the entity doesn't stay stuck on Online.
        _connectionManager.RegisterPoolForConnection(Context.ConnectionId, tenantId, poolRtId, poolName);

        try
        {
            await _poolService.SetCommunicationStateOnlineAsync(tenantId,
                new OctoObjectId(poolRtId), Context.ConnectionId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex,
                "Failed to mark pool '{PoolName}' (rtId {PoolRtId}) online (tenant '{TenantId}')",
                poolName, poolRtId, tenantId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnregisterPoolAsync(string tenantId, string poolRtId, string poolName)
    {
        Logger.Info(
            "Operator '{ConnectionId}' releases pool '{PoolName}' (rtId {PoolRtId}) for tenant '{TenantId}'",
            Context.ConnectionId, poolName, poolRtId, tenantId);

        _connectionManager.UnregisterPoolForConnection(Context.ConnectionId, tenantId, poolRtId);

        try
        {
            await _poolService.UnregisterPoolOperatorAsync(tenantId, new OctoObjectId(poolRtId));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Failed to unregister pool '{PoolName}' (rtId {PoolRtId}); state may stay Online until disconnect (tenant '{TenantId}')",
                poolName, poolRtId, tenantId);
        }
    }

    /// <inheritdoc />
    public async Task ReportWorkloadDeploymentStatusAsync(WorkloadDeploymentStatusDto status)
    {
        Logger.Info(
            "Workload deployment status report: tenant '{TenantId}', pool '{PoolName}', workload '{WorkloadName}', success={Success}",
            status.TenantId, status.PoolName, status.WorkloadName, status.Success);

        if (string.IsNullOrWhiteSpace(status.TenantId) || string.IsNullOrWhiteSpace(status.WorkloadRtId))
        {
            Logger.Warn("Ignoring deployment status report with missing tenant id or workload rt id");
            return;
        }

        var newState = status.Success
            ? RtDeploymentStateEnum.Deployed
            : RtDeploymentStateEnum.Error;

        try
        {
            // The DTO doesn't carry the workload's CK type, so we read the
            // entity to discover whether it's an Adapter or Application and
            // route to the matching repository setter. (Earlier this method
            // always wrote to the Adapter setter — Application status reports
            // never landed in MongoDB and the UI stayed stuck at Pending.)
            var workloadRtId = new OctoObjectId(status.WorkloadRtId);
            var workload = await _communicationRepository.GetWorkloadByRtIdAsync(status.TenantId, workloadRtId);
            if (workload == null)
            {
                Logger.Warn(
                    "Workload '{WorkloadRtId}' (tenant '{TenantId}') reported deployment status but no entity exists in the repository; skipping",
                    status.WorkloadRtId, status.TenantId);
                return;
            }

            switch (workload)
            {
                case RtApplication:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workloadRtId);
                        await _communicationRepository.SetApplicationDeploymentStateAsync(
                            status.TenantId, rtEntityId, newState, status.StatusMessage);
                        break;
                    }
                case RtAdapter:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workloadRtId);
                        await _communicationRepository.SetAdapterDeploymentStateAsync(
                            status.TenantId, rtEntityId, newState, status.StatusMessage);
                        break;
                    }
                default:
                    Logger.Warn(
                        "Workload '{WorkloadRtId}' (tenant '{TenantId}') is of unsupported type '{Type}'; skipping status persist",
                        status.WorkloadRtId, status.TenantId, workload.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Don't propagate — a failed status write must not crash the hub
            // for other workloads. The operator will retry the report on its
            // next deploy attempt.
            Logger.Error(ex,
                "Failed to persist deployment status for workload '{WorkloadName}' (tenant '{TenantId}')",
                status.WorkloadName, status.TenantId);
        }
    }
}
