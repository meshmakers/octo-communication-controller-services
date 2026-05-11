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
    /// Returns the pool names that this controller has notified operators of
    /// as deployed for the given tenant. Backed by in-memory tracking that is
    /// updated whenever <c>NotifyPoolDeployedAsync</c> or
    /// <c>NotifyPoolUndeployedAsync</c> is invoked. The cascade in
    /// <c>PreDeleteTenant</c> reads from here instead of the tenant repository
    /// because the CK-cache for the tenant may already be torn down by the
    /// parallel <c>PreUpdatePreDeleteTenantConsumer</c>.
    /// </summary>
    IReadOnlyCollection<string> GetDeployedPoolsForTenant(string tenantId);

    /// <summary>
    /// Notifies all connected operators that a Cloud pool was deployed.
    /// </summary>
    Task NotifyPoolDeployedAsync(DeployedPoolDto pool);

    /// <summary>
    /// Notifies all connected operators that a Cloud pool was undeployed.
    /// </summary>
    Task NotifyPoolUndeployedAsync(string tenantId, string poolName);

    /// <summary>
    /// Returns the workloads this controller has notified operators of as
    /// deployed for the given tenant. Same pattern as
    /// <see cref="GetDeployedPoolsForTenant"/> — backed by in-memory tracking
    /// driven by <c>NotifyWorkloadDeployedAsync</c> / <c>NotifyWorkloadUndeployedAsync</c>.
    /// </summary>
    IReadOnlyCollection<WorkloadUndeployedDto> GetDeployedWorkloadsForTenant(string tenantId);

    /// <summary>
    /// Notifies all connected operators that an Adapter or Application managed
    /// by a Cloud pool was deployed. Secret-flagged value overrides on
    /// <paramref name="workload"/> are expected to be decrypted by the caller
    /// (controller side) before invocation — the wire is TLS-secured.
    /// </summary>
    Task NotifyWorkloadDeployedAsync(WorkloadDeployedDto workload);

    /// <summary>
    /// Notifies all connected operators that an Adapter or Application should
    /// be undeployed (helm uninstall).
    /// </summary>
    Task NotifyWorkloadUndeployedAsync(WorkloadUndeployedDto workload);
}
