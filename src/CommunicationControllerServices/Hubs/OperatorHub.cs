using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for operator management connections.
/// Operators register here to receive tenant lifecycle notifications.
/// Not tenant-scoped - operators connect once and receive events for all tenants.
/// </summary>
public class OperatorHub : Hub, IOperatorHub
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IOperatorConnectionManager _connectionManager;
    private readonly ICommunicationRepository _communicationRepository;

    /// <summary>
    /// Constructor
    /// </summary>
    public OperatorHub(IOperatorConnectionManager connectionManager,
        ICommunicationRepository communicationRepository)
    {
        _connectionManager = connectionManager;
        _communicationRepository = communicationRepository;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        Logger.Info("Operator connected with connection id '{ConnectionId}'", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Logger.Info("Operator disconnected with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.RemoveOperator(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public Task<IEnumerable<DeployedPoolDto>> RegisterOperatorAsync()
    {
        Logger.Info("Operator registered with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.AddOperator(Context.ConnectionId);
        return Task.FromResult(_connectionManager.GetDeployedPools());
    }

    /// <inheritdoc />
    public Task UnregisterOperatorAsync()
    {
        Logger.Info("Operator unregistered with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.RemoveOperator(Context.ConnectionId);
        return Task.CompletedTask;
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
