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
        foreach (var (tenantId, poolName) in orphaned)
        {
            try
            {
                await _poolService.SetCommunicationStateOfflineAsync(tenantId, poolName,
                    disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark pool '{PoolName}' offline after operator disconnect (tenant '{TenantId}')",
                    poolName, tenantId);
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
        foreach (var (tenantId, poolName) in orphaned)
        {
            try
            {
                await _poolService.SetCommunicationStateOfflineAsync(tenantId, poolName,
                    disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark pool '{PoolName}' offline on operator unregister (tenant '{TenantId}')",
                    poolName, tenantId);
            }
        }
    }

    /// <inheritdoc />
    public async Task RegisterPoolAsync(string tenantId, string poolName)
    {
        Logger.Info(
            "Operator '{ConnectionId}' claims pool '{PoolName}' for tenant '{TenantId}'",
            Context.ConnectionId, poolName, tenantId);

        // Track the (connection, tenant, pool) tuple before flipping state —
        // if state-write fails we still want OnDisconnectedAsync to clean
        // up so the entity doesn't stay stuck on Online.
        _connectionManager.RegisterPoolForConnection(Context.ConnectionId, tenantId, poolName);

        try
        {
            await _poolService.SetCommunicationStateOnlineAsync(tenantId, poolName, Context.ConnectionId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex,
                "Failed to mark pool '{PoolName}' online (tenant '{TenantId}')", poolName, tenantId);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task UnregisterPoolAsync(string tenantId, string poolName)
    {
        Logger.Info(
            "Operator '{ConnectionId}' releases pool '{PoolName}' for tenant '{TenantId}'",
            Context.ConnectionId, poolName, tenantId);

        _connectionManager.UnregisterPoolForConnection(Context.ConnectionId, tenantId, poolName);

        try
        {
            await _poolService.UnregisterPoolOperatorAsync(tenantId, poolName);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Failed to unregister pool '{PoolName}' (tenant '{TenantId}'); state may stay Online until disconnect",
                poolName, tenantId);
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

        // The hub currently only carries Adapter workloads (Applications use
        // the same flow but their CK type isn't routed through this method
        // yet — see Phase-3 plan). Build the entity id as RtAdapter.
        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId,
            new OctoObjectId(status.WorkloadRtId));
        var newState = status.Success
            ? RtDeploymentStateEnum.Deployed
            : RtDeploymentStateEnum.Error;

        try
        {
            await _communicationRepository.SetAdapterDeploymentStateAsync(
                status.TenantId, rtEntityId, newState, status.StatusMessage);
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
