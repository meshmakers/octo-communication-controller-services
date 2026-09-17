using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Manages connected operator instances and provides methods to notify them of
/// Cloud deploymentSite deploy / undeploy events.
/// </summary>
public interface IOperatorConnectionManager
{
    /// <summary>
    /// Registers an operator connection.
    /// </summary>
    void AddOperator(string connectionId);

    /// <summary>
    /// Records the operator's declared <c>AutoManageDeploymentSites</c> mode for this
    /// connection. <c>true</c> = central operator (Cloud deploymentSites only),
    /// <c>false</c> = edge operator (Edge deploymentSites only), <c>null</c> = legacy
    /// operator that did not declare a mode (no enforcement). Read back by
    /// <c>OperatorHub.RegisterDeploymentSiteAsync</c> via <see cref="GetOperatorMode"/>
    /// to validate deploymentSite ownership against <c>RtDeploymentSite.Environment</c>.
    /// </summary>
    void SetOperatorMode(string connectionId, bool? autoManageDeploymentSites);

    /// <summary>
    /// Returns the operator's declared <c>AutoManageDeploymentSites</c> mode for this
    /// connection, or <c>null</c> if none was declared (legacy operator) or
    /// the connection is not known. <see cref="SetOperatorMode"/> for the
    /// semantics.
    /// </summary>
    bool? GetOperatorMode(string connectionId);

    /// <summary>
    /// Removes an operator connection and returns the (tenant, deploymentSiteRtId)
    /// tuples that connection had registered via
    /// <see cref="RegisterDeploymentSiteForConnection"/>. The caller is expected to
    /// flip every returned deploymentSite's <c>CommunicationState</c> to
    /// <c>Offline</c> — this happens on every operator disconnect, planned
    /// or otherwise.
    /// </summary>
    IReadOnlyCollection<(string TenantId, string DeploymentSiteRtId)> RemoveOperator(string connectionId);

    /// <summary>
    /// Records that the given operator connection now hosts the deploymentSite
    /// identified by <paramref name="deploymentSiteRtId"/>. Used by
    /// <c>OperatorHub.RegisterDeploymentSiteAsync</c> so the controller can reset
    /// the deploymentSite's state when the SignalR connection drops.
    /// </summary>
    void RegisterDeploymentSiteForConnection(string connectionId, string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Removes a single (connection, tenant, deploymentSiteRtId) tuple — called on a
    /// graceful <c>UnregisterDeploymentSiteAsync</c> while the operator keeps the
    /// connection open for other deploymentSites.
    /// </summary>
    void UnregisterDeploymentSiteForConnection(string connectionId, string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Returns all currently-deployed Cloud deploymentSites across every tenant. Used as
    /// the response to a freshly (re)connecting operator's
    /// <c>RegisterOperatorAsync</c> call so it can synchronize its desired
    /// state.
    /// </summary>
    IEnumerable<DeployedDeploymentSiteDto> GetDeployedDeploymentSites();

    /// <summary>
    /// Returns the deploymentSite RtIds that this controller has notified operators
    /// of as deployed for the given tenant. Backed by in-memory tracking
    /// that is updated whenever <c>NotifyDeploymentSiteDeployedAsync</c> or
    /// <c>NotifyDeploymentSiteUndeployedAsync</c> is invoked. The cascade in
    /// <c>PreDeleteTenant</c> reads from here instead of the tenant
    /// repository because the CK-cache for the tenant may already be torn
    /// down by the parallel <c>PreUpdatePreDeleteTenantConsumer</c>.
    /// </summary>
    IReadOnlyCollection<string> GetDeployedDeploymentSitesForTenant(string tenantId);

    /// <summary>
    /// Notifies all connected operators that a Cloud deploymentSite was deployed.
    /// </summary>
    Task NotifyDeploymentSiteDeployedAsync(DeployedDeploymentSiteDto deploymentSite);

    /// <summary>
    /// Notifies all connected operators that a Cloud deploymentSite was undeployed.
    /// </summary>
    Task NotifyDeploymentSiteUndeployedAsync(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Returns the workloads this controller has notified operators of as
    /// deployed for the given tenant. Same pattern as
    /// <see cref="GetDeployedDeploymentSitesForTenant"/> — backed by in-memory tracking
    /// driven by <c>NotifyWorkloadDeployedAsync</c> / <c>NotifyWorkloadUndeployedAsync</c>.
    /// </summary>
    IReadOnlyCollection<WorkloadUndeployedDto> GetDeployedWorkloadsForTenant(string tenantId);

    /// <summary>
    /// Notifies all connected operators that an Adapter or Application managed
    /// by a Cloud deploymentSite was deployed. Secret-flagged value overrides on
    /// <paramref name="workload"/> are expected to be decrypted by the caller
    /// (controller side) before invocation — the wire is TLS-secured.
    /// </summary>
    Task NotifyWorkloadDeployedAsync(WorkloadDeployedDto workload);

    /// <summary>
    /// Notifies all connected operators that an Adapter or Application should
    /// be undeployed (helm uninstall).
    /// </summary>
    Task NotifyWorkloadUndeployedAsync(WorkloadUndeployedDto workload);

    /// <summary>
    /// Notifies the operator owning the workload's deploymentSite that the workload
    /// should be scaled to the given replica count (AB#4917, on-demand
    /// lifecycle AB#4914). Same deploymentSite-scoped routing and pending-queue
    /// semantics as deploy/undeploy; does not touch the deploy tracking maps
    /// (a hibernated workload stays tracked as deployed — its helm release
    /// still exists).
    /// </summary>
    Task NotifyWorkloadScaleAsync(ScaleWorkloadDto workload);

    /// <summary>
    /// Replays workload deploy/undeploy notifications that were queued
    /// because no operator connection owned the target deploymentSite at notify time
    /// (AB#4371 — e.g. the operator's deploymentSite registration failed transiently
    /// and it re-registered later). Called by <c>OperatorHub.RegisterDeploymentSiteAsync</c>
    /// right after the (connection, tenant, deploymentSiteRtId) tuple is registered.
    /// No-op when nothing is pending. A replay that fails to send is
    /// re-queued so the next registration of the deploymentSite retries it.
    /// </summary>
    Task FlushPendingWorkloadNotificationsAsync(string connectionId, string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Server→client fanout of the tenant pre-update signal. Operators use
    /// this to let in-flight work settle before the CK-cache is unloaded.
    /// Replaces the legacy <c>IPoolHubCallbacks.PreUpdateTenantAsync</c>
    /// which was tied to the now-defunct per-deploymentSite connection.
    /// </summary>
    Task NotifyPreUpdateTenantAsync(string tenantId);

    /// <summary>
    /// Returns the SignalR connection ids of every operator that currently
    /// claims the <c>(tenantId, deploymentSiteRtId)</c> tuple via
    /// <see cref="RegisterDeploymentSiteForConnection"/>. Used by
    /// <c>DeploymentSiteService.SetCommunicationStateOfflineAsync</c> to detect that a
    /// disconnect should NOT flip the deploymentSite offline because another operator
    /// connection (e.g. a still-alive replica or the surviving end of a
    /// rolling restart with brief overlap) is still hosting it.
    /// </summary>
    IReadOnlyList<string> GetConnectionsForDeploymentSite(string tenantId, string deploymentSiteRtId);

    /// <summary>
    /// Adds <paramref name="deploymentSite"/> to the deployed-deploymentSite tracking map WITHOUT
    /// firing a SignalR notification. Used by the reverse-sync handshake
    /// (<c>OperatorHub.ReportDeployedStateAsync</c>) so a reconnecting Cloud
    /// operator can rebuild the per-tenant tracking the controller lost when
    /// the previous connection dropped — keeping <c>PreDeleteTenant</c>
    /// cascade and undeploy fan-out working after an operator restart.
    /// </summary>
    void TrackDeployedDeploymentSite(DeployedDeploymentSiteDto deploymentSite);

    /// <summary>
    /// Adds <paramref name="workload"/> to the deployed-workload tracking
    /// map WITHOUT firing a SignalR notification. Companion to
    /// <see cref="TrackDeployedDeploymentSite"/> for the workload tracking surface
    /// (<see cref="GetDeployedWorkloadsForTenant"/>).
    /// </summary>
    void TrackDeployedWorkload(WorkloadUndeployedDto workload);
}
