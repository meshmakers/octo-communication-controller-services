using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Manages deploymentSites for all tenants and their state. Workload (Adapter/Application) deploys
/// are fanned out by <see cref="DeployDeploymentSiteAsync"/> via the Helm-based workload path on
/// the central Communication Operator — there is no legacy adapter-deploy callback path
/// any more.
/// </summary>
public interface IDeploymentSiteService
{
    /// <summary>
    /// Unregisters a deploymentSite operator for a tenant. Called by
    /// <c>OperatorHub.UnregisterDeploymentSiteAsync</c> when the operator releases
    /// a deploymentSite while keeping the hub connection open.
    /// </summary>
    Task UnregisterDeploymentSiteOperatorAsync(string tenantId, OctoObjectId poolRtId);

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
    /// Deploys a deploymentSite: marks it as Deployed and, when the deploymentSite's
    /// <c>Environment</c> attribute is <c>Cloud</c>, notifies the central
    /// Communication Operator via the <c>/operatorHub</c> SignalR channel so
    /// it provisions the corresponding DeploymentSite CR and broker secret
    /// and Helm-deploys every workload managed by the deploymentSite. Edge-environment
    /// deploymentSites transition state without any operator notification — they are
    /// installed and run by an external operator.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the deploymentSite</param>
    Task DeployDeploymentSiteAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Undeploys a deploymentSite: marks it as Undeployed and, when the deploymentSite's
    /// <c>Environment</c> is <c>Cloud</c>, notifies the operator to remove
    /// its DeploymentSite CR and broker secret.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="poolRtId">The object id of the deploymentSite</param>
    Task UndeployDeploymentSiteAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Deploys a single workload (Adapter or Application). Resolves the
    /// workload's parent deploymentSite, builds the deploy DTO from the entity's
    /// chart reference + values, and fires <c>NotifyWorkloadDeployedAsync</c>
    /// on the operator hub. Independent of <see cref="DeployDeploymentSiteAsync"/>:
    /// the deploymentSite must already be deployed, but no fan-out happens here.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="workloadRtId">The object id of the workload</param>
    /// <param name="isReconciliation">
    /// Set only by <see cref="ReconcilePendingWorkloadsAsync"/>: this dispatch restores what was
    /// already supposed to be running instead of acting on a release decision. It reaches the
    /// operator on the deploy DTO and keeps a workload with an empty ChartVersion on the chart it
    /// already has installed, rather than resolving "newest in the repository" again (AB#4955).
    /// </param>
    Task DeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId, bool isReconciliation = false);

    /// <summary>
    /// Undeploys a single workload (Adapter or Application).
    /// </summary>
    Task UndeployWorkloadAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    /// Scales a deployed <c>AdapterPool</c> to <paramref name="replicas"/> members (AB#4924 §7.2).
    /// </summary>
    /// <remarks>
    /// A deploymentSite is one workload with a replica range, so this is the AB#4917 <c>ScaleWorkloadDto</c>
    /// verb and nothing new: the operator patches the release's Deployments, the release history is
    /// untouched, and the chart values the members were installed from stay as they are. The
    /// request is held inside the deploymentSite's declared <c>MinReplicas..MaxReplicas</c> range — asking for
    /// less than <c>MinReplicas</c> is a request the deploymentSite cannot honour, not a configuration change.
    /// Returns the replica count actually requested from the operator, which may differ from
    /// <paramref name="replicas"/> for exactly that reason.
    /// </remarks>
    /// <param name="tenantId">Tenant owning the deploymentSite</param>
    /// <param name="poolWorkloadRtId">The object id of the <c>AdapterPool</c> workload</param>
    /// <param name="replicas">Desired member count</param>
    Task<int> ScaleAdapterPoolAsync(string tenantId, OctoObjectId poolWorkloadRtId, int replicas);

    /// <summary>
    /// Re-dispatches every workload of the deploymentSite that is stuck in
    /// <c>DeploymentState = Pending</c> (AB#4894). Called when an operator (re-)registers the
    /// deploymentSite: a deploy notification sent while the previous operator pod was being replaced is
    /// lost silently (SignalR SendAsync is fire-and-forget), leaving the entity Pending forever —
    /// the reverse-sync restores Deployed state but never re-dispatches pending deploys.
    /// Best effort: failures are logged per workload and never fail the registration.
    /// A re-dispatch racing a genuinely in-flight deploy is safe — the operator queue is serial
    /// and <c>helm upgrade --install</c> is idempotent.
    /// </summary>
    Task ReconcilePendingWorkloadsAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Undeploys every Cloud deploymentSite of a tenant. Used when a tenant is being
    /// deleted/detached so that the central Communication Operator cleans up
    /// all DeploymentSite CRs and broker secrets that were auto-managed
    /// for the tenant.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task UndeployAllCloudDeploymentSitesAsync(string tenantId);

    /// <summary>
    /// Sets a deploymentSite offline unconditionally.
    /// </summary>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a deploymentSite offline, but only if the cached deploymentSite's current connection id still
    /// matches the supplied <paramref name="disconnectingConnectionId"/>. This guards
    /// against stale <c>OnDisconnectedAsync</c> handlers from a previous operator
    /// connection overwriting Online state that a newer operator has already written.
    /// </summary>
    Task SetCommunicationStateOfflineAsync(string tenantId, OctoObjectId poolRtId,
        string disconnectingConnectionId);

    /// <summary>
    /// Sets a deploymentSite online unconditionally.
    /// </summary>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId);

    /// <summary>
    /// Sets a deploymentSite online and records the connection id that owns it.
    /// Lazy-loads the deploymentSite into the cache when it isn't there yet.
    /// </summary>
    Task SetCommunicationStateOnlineAsync(string tenantId, OctoObjectId poolRtId, string connectionId);

    /// <summary>
    /// Returns a summary list of all deploymentSites for a tenant with typed enum states.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <returns>List of deploymentSite summaries with typed communication, configuration, and deployment states</returns>
    Task<IReadOnlyList<DeploymentSiteSummaryDto>> GetDeploymentSiteSummariesAsync(string tenantId);

    /// <summary>
    /// Lists the deploymentSites and workloads (Adapters/Applications) of the tenant that still own
    /// operator-managed resources according to their persisted <c>DeploymentState</c>
    /// (see <see cref="ActiveDeployment.IsActive"/>). Reads the repository, not the in-memory
    /// operator tracking, so the answer survives controller restarts and covers Edge leftovers.
    /// DeploymentSites first, then workloads, each ordered by name. Read failures propagate — an unreadable
    /// tenant must never look torn down. Used by the Communication disable guard (AB#4255).
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    Task<IReadOnlyList<ActiveDeployment>> GetActiveDeploymentsAsync(string tenantId);

    /// <summary>
    /// Walks every DeploymentSite, Workload (Adapter/Application), Pipeline, and PipelineTrigger of a
    /// tenant and recomputes their DeploymentState according to the Disabled rules:
    /// <list type="bullet">
    /// <item>Edge deploymentSites → Disabled (Edge deploymentSites are managed externally)</item>
    /// <item>Workloads missing Helm chart name/version, Helm-repository association, or
    /// repository URL → Disabled (independent of deploymentSite Environment — edge operators
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

    /// <summary>
    /// Reverse-sync handshake from a freshly (re)connected Cloud operator:
    /// every reported deploymentSite whose <c>DeploymentState</c> is not already
    /// <c>Deployed</c> is restored to <c>Deployed</c>, and the operator's
    /// SignalR connection is wired back into the per-deploymentSite tracking so
    /// undeploy fan-out (<c>UndeployAllCloudDeploymentSitesAsync</c>) keeps working
    /// after an operator restart. Same treatment for the workloads listed
    /// inside each deploymentSite report.
    ///
    /// Cloud-only: callers must ensure the operator's
    /// <c>AutoManageDeploymentSites</c> mode is <c>true</c> before invoking
    /// (<c>OperatorHub.ReportDeployedStateAsync</c> enforces this with a
    /// <c>HubException</c>). Per-deploymentSite guard rejects entries whose
    /// <c>Environment</c> is not Cloud so a misbehaving operator cannot
    /// revive Edge state via this path.
    /// </summary>
    /// <param name="operatorConnectionId">The SignalR connection id of the
    /// reporting operator. Used to re-register deploymentSite ownership for the new
    /// connection.</param>
    /// <param name="deployedDeploymentSites">The operator's view of which deploymentSites and
    /// workloads currently have a healthy helm release.</param>
    Task RestoreDeployedStateAsync(string operatorConnectionId,
        IReadOnlyList<OperatorDeployedDeploymentSiteReportDto> deployedDeploymentSites);
}
