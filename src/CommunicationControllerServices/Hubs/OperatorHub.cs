using Meshmakers.Octo.Communication.Contracts.Hubs;
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

    /// <summary>
    /// Constructor
    /// </summary>
    public OperatorHub(IOperatorConnectionManager connectionManager)
    {
        _connectionManager = connectionManager;
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
    public Task<IEnumerable<string>> RegisterOperatorAsync()
    {
        Logger.Info("Operator registered with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.AddOperator(Context.ConnectionId);
        return Task.FromResult(_connectionManager.GetEnabledTenants());
    }

    /// <inheritdoc />
    public Task UnregisterOperatorAsync()
    {
        Logger.Info("Operator unregistered with connection id '{ConnectionId}'", Context.ConnectionId);
        _connectionManager.RemoveOperator(Context.ConnectionId);
        return Task.CompletedTask;
    }
}
