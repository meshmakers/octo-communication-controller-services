namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Manages connected operator instances and provides methods to notify them of tenant lifecycle events.
/// </summary>
public interface IOperatorConnectionManager
{
    /// <summary>
    /// Registers an operator connection.
    /// </summary>
    void AddOperator(string connectionId);

    /// <summary>
    /// Removes an operator connection.
    /// </summary>
    void RemoveOperator(string connectionId);

    /// <summary>
    /// Returns the list of tenant IDs that currently have communication enabled.
    /// </summary>
    IEnumerable<string> GetEnabledTenants();

    /// <summary>
    /// Notifies all connected operators that a tenant was created.
    /// </summary>
    Task NotifyTenantCreatedAsync(string tenantId);

    /// <summary>
    /// Notifies all connected operators that a tenant is being deleted.
    /// </summary>
    Task NotifyTenantDeletedAsync(string tenantId);
}
