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
    /// Unregisters a pool operator for a tenant. Called by
    /// <c>OperatorHub.UnregisterPoolAsync</c> when the operator releases
    /// a pool while keeping the hub connection open.
    /// </summary>
    Task UnregisterPoolOperatorAsync(string tenantId, OctoObjectId poolRtId);

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
    /// Sets a pool offline unconditionally.
    /// </summary>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool offline, but only if the cached pool's current connection id still
    /// matches the supplied <paramref name="disconnectingConnectionId"/>. This guards
    /// against stale <c>OnDisconnectedAsync</c> handlers from a previous operator
    /// connection overwriting Online state that a newer operator has already written.
    /// </summary>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId,
        string disconnectingConnectionId);

    /// <summary>
    /// Sets a pool online unconditionally.
    /// </summary>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a pool online and records the connection id that owns it.
    /// Lazy-loads the pool into the cache when it isn't there yet.
    /// </summary>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId, string connectionId);

    /// <summary>
    /// Returns a summary list of all pools for a tenant with typed enum states.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of pool summaries with typed communication, configuration, and deployment states</returns>
    Task<IReadOnlyList<PoolSummaryDto>> GetPoolSummariesAsync(string tenantId);

    /// <summary>
    /// Walks every Pool, Workload (Adapter/Application), Pipeline, and PipelineTrigger of a
    /// tenant and recomputes their DeploymentState according to the Disabled rules:
    /// <list type="bullet">
    /// <item>Edge pools → Disabled (Edge pools are managed externally)</item>
    /// <item>Workloads missing Helm chart name/version, Helm-repository association, or
    /// repository URL → Disabled (independent of pool Environment — edge operators
    /// deploy workloads via the same helm path as central, so Edge alone does not
    /// disable a workload)</item>
    /// <item>Pipelines whose parent adapter is Disabled or missing → Disabled</item>
    /// <item>Triggers whose triggered pipelines are all Disabled or missing → Disabled</item>
    /// </list>
    /// When an entity is currently Disabled but the rules no longer apply (e.g. Helm
    /// fields were filled in), it is moved to Undeployed so the normal deploy lifecycle
    /// can resume. Idempotent — safe to run repeatedly.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task RecomputeAllDeploymentStatesAsync(string tenantId);
}
