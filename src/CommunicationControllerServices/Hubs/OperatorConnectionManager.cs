using System.Collections.Concurrent;
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

    public IEnumerable<string> GetEnabledTenants()
    {
        return Enumerable.Empty<string>();
    }

    public async Task NotifyTenantCreatedAsync(string tenantId)
    {
        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug("No operators connected, skipping tenant created notification for '{TenantId}'", tenantId);
            return;
        }

        Logger.Info("Notifying {Count} operator(s) of tenant creation: '{TenantId}'",
            _connectedOperators.Count, tenantId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.TenantCreatedAsync), tenantId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to notify operator {ConnectionId} of tenant creation", connectionId);
            }
        }
    }

    public async Task NotifyTenantDeletedAsync(string tenantId)
    {
        if (_connectedOperators.IsEmpty)
        {
            Logger.Debug("No operators connected, skipping tenant deleted notification for '{TenantId}'", tenantId);
            return;
        }

        Logger.Info("Notifying {Count} operator(s) of tenant deletion: '{TenantId}'",
            _connectedOperators.Count, tenantId);

        var connectionIds = _connectedOperators.Keys.ToList();
        foreach (var connectionId in connectionIds)
        {
            try
            {
                await hubContext.Clients.Client(connectionId)
                    .SendAsync(nameof(IOperatorHubCallbacks.TenantDeletedAsync), tenantId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to notify operator {ConnectionId} of tenant deletion", connectionId);
            }
        }
    }
}
