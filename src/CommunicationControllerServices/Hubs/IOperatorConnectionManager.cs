using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Manages connected operator instances and provides methods to notify them of
/// Cloud pool deploy / undeploy events.
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
    /// Returns all currently-deployed Cloud pools across every tenant. Used as
    /// the response to a freshly (re)connecting operator's
    /// <c>RegisterOperatorAsync</c> call so it can synchronize its desired
    /// state.
    /// </summary>
    IEnumerable<DeployedPoolDto> GetDeployedPools();

    /// <summary>
    /// Notifies all connected operators that a Cloud pool was deployed.
    /// </summary>
    Task NotifyPoolDeployedAsync(DeployedPoolDto pool);

    /// <summary>
    /// Notifies all connected operators that a Cloud pool was undeployed.
    /// </summary>
    Task NotifyPoolUndeployedAsync(string tenantId, string poolName);
}
