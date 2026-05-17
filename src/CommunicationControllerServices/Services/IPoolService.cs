using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Manages pools for all tenants and their state. Workload (Adapter/Application) deploys
/// are fanned out by <see cref="DeployPoolAsync"/> via the Helm-based workload path on
/// the central Communication Operator — there is no legacy adapter-deploy callback path
/// any more.
/// </summary>
public interface IPoolService
{
    /// <summary>
    /// Registers a pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="connectionId">Connection id to client</param>
    /// <returns></returns>
    Task<OctoObjectId> RegisterPoolOperatorAsync(string tenantId, string poolName, string connectionId);

    /// <summary>
    /// Unregisters a pool operator for a tenant
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <returns></returns>
    Task UnregisterPoolOperatorAsync(string tenantId, string poolName);

    /// <summary>
    /// Updates an entire tenant before a tenant is deleted or disabled for communication.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns></returns>
    Task PreUpdateTenantAsync(string tenantId);

    /// <summary>
    /// Loads an entire tenant after a tenant has been created or enabled.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <returns></returns>
    Task PosUpdateTenantAsync(string tenantId);

    /// <summary>
    /// Deploys a pool: marks it as Deployed and, when the pool's
    /// <c>Environment</c> attribute is <c>Cloud</c>, notifies the central
    /// Communication Operator via the <c>/operatorHub</c> SignalR channel so
    /// it provisions the corresponding CommunicationPool CR and broker secret
    /// and Helm-deploys every workload managed by the pool. Edge-environment
    /// pools transition state without any operator notification — they are
    /// installed and run by an external operator.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    Task DeployPoolAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Undeploys a pool: marks it as Undeployed and, when the pool's
    /// <c>Environment</c> is <c>Cloud</c>, notifies the operator to remove
    /// its CommunicationPool CR and broker secret.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the pool</param>
    Task UndeployPoolAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Deploys a single workload (Adapter or Application). Resolves the
    /// workload's parent pool, builds the deploy DTO from the entity's
    /// chart reference + values, and fires <c>NotifyWorkloadDeployedAsync</c>
    /// on the operator hub. Independent of <see cref="DeployPoolAsync"/>:
    /// the pool must already be deployed, but no fan-out happens here.
    /// </summary>
    Task DeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    /// Undeploys a single workload (Adapter or Application).
    /// </summary>
    Task UndeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    /// Undeploys every Cloud pool of a tenant. Used when a tenant is being
    /// deleted/detached so that the central Communication Operator cleans up
    /// all CommunicationPool CRs and broker secrets that were auto-managed
    /// for the tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task UndeployAllCloudPoolsAsync(string tenantId);

    /// <summary>
    /// Sets a pool offline
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool offline, but only if the cached pool's current connection id still
    /// matches the supplied <paramref name="disconnectingConnectionId"/>. This guards
    /// against stale <c>OnDisconnectedAsync</c> handlers from a previous operator
    /// connection overwriting Online state that a newer operator has already written.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="disconnectingConnectionId">SignalR connection id whose disconnect triggered this call</param>
    Task SetCommunicationStateOfflineAsync(string tenantId, string poolName, string disconnectingConnectionId);

    /// <summary>
    /// Sets a pool online
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">Identifier of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool online using the connection id
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="connectionId">Connection id of pool</param>
    /// <returns></returns>
    Task SetCommunicationStateOnlineAsync(string tenantId, string poolName, string connectionId);

    /// <summary>
    /// Updates the deployment state of an adapter in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="adapterRtEntityId">The object id of the adapter</param>
    /// <param name="deploymentState">The new deployment state</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, RtEntityId adapterRtEntityId,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Updates the deployment state of an adapter in a pool
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolName">Name of pool</param>
    /// <param name="adapterRtEntityIds">The object id of the adapters</param>
    /// <param name="deploymentState">The new deployment state</param>
    /// <returns></returns>
    Task SetAdapterDeploymentStateAsync(string tenantId, string poolName, ICollection<RtEntityId> adapterRtEntityIds,
        RtDeploymentStateEnum deploymentState);

    /// <summary>
    /// Returns a summary list of all pools for a tenant with typed enum states.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of pool summaries with typed communication, configuration, and deployment states</returns>
    Task<IReadOnlyList<PoolSummaryDto>> GetPoolSummariesAsync(string tenantId);
}
