using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
/// Hub for operator management connections.
/// Operators register here to receive tenant lifecycle notifications,
/// register / unregister deploymentSites they own, and report workload deploy
/// outcomes. Not tenant-scoped — one operator process keeps one
/// connection regardless of how many deploymentSites / tenants it manages.
/// </summary>
public class OperatorHub : Hub, IOperatorHub
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly IOperatorConnectionManager _connectionManager;
    private readonly ICommunicationRepository _communicationRepository;
    private readonly IDeploymentSiteService _deploymentSiteService;
    private readonly IShutdownState _shutdownState;
    private readonly ICommunicationEventService _eventService;
    private readonly IWorkloadLifecycleService _workloadLifecycleService;

    /// <summary>
    /// Constructor
    /// </summary>
    public OperatorHub(IOperatorConnectionManager connectionManager,
        ICommunicationRepository communicationRepository,
        IDeploymentSiteService poolService,
        IShutdownState shutdownState,
        ICommunicationEventService eventService,
        IWorkloadLifecycleService workloadLifecycleService)
    {
        _connectionManager = connectionManager;
        _communicationRepository = communicationRepository;
        _deploymentSiteService = poolService;
        _shutdownState = shutdownState;
        _eventService = eventService;
        _workloadLifecycleService = workloadLifecycleService;
    }

    /// <inheritdoc />
    public override Task OnConnectedAsync()
    {
        Logger.Info("Operator connected with connection id '{ConnectionId}'", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <inheritdoc />
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var disconnectingConnectionId = Context.ConnectionId;
        Logger.Info("Operator disconnected with connection id '{ConnectionId}'", disconnectingConnectionId);

        // Rolling-upgrade race guard: during this pod's own shutdown the
        // operator has already (or is about to) reconnect to a surviving
        // controller pod, which writes Online for every deploymentSite it manages.
        // If we still ran the Offline-on-disconnect path here, our write
        // would land in MongoDB AFTER the new pod's Online write (later
        // timestamp wins the AttributeNewerThanGuard), and the UI would
        // stay stuck at Offline for the rest of the new pod's lifetime.
        // The new pod is the authoritative state holder once we're
        // stopping — skip the writes entirely.
        if (_shutdownState.IsShuttingDown)
        {
            Logger.Info(
                "App is stopping; skipping Offline writes for connection '{ConnectionId}'. " +
                "Surviving pod will reconcile deploymentSite CommunicationState.",
                disconnectingConnectionId);
            // Drop the connection-level entry locally so any late hub
            // method calls don't see a stale connection, but skip the
            // per-deploymentSite state writes.
            _connectionManager.RemoveOperator(disconnectingConnectionId);
            await base.OnDisconnectedAsync(exception);
            return;
        }

        // Drop the connection-level entry and reset every deploymentSite it claimed.
        // Same call site whether the disconnect was graceful (operator
        // shutdown) or a crash — the hub guarantees this fires exactly once.
        // The disconnecting connection id is passed to DeploymentSiteService so a stale
        // disconnect (a previous connection's handler firing late, after a
        // newer connection has already taken over) does not overwrite the
        // Online state written by the newer connection.
        var orphaned = _connectionManager.RemoveOperator(disconnectingConnectionId);
        foreach (var (tenantId, deploymentSiteRtId) in orphaned)
        {
            try
            {
                await _deploymentSiteService.SetCommunicationStateOfflineAsync(tenantId,
                    new OctoObjectId(deploymentSiteRtId), disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark deploymentSite (rtId {DeploymentSiteRtId}) offline after operator disconnect (tenant '{TenantId}')",
                    deploymentSiteRtId, tenantId);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <inheritdoc />
    public Task<IEnumerable<DeployedDeploymentSiteDto>> RegisterOperatorAsync(bool? autoManageDeploymentSites = null)
    {
        Logger.Info(
            "Operator registered with connection id '{ConnectionId}' (mode: {Mode})",
            Context.ConnectionId,
            autoManageDeploymentSites.HasValue
                ? (autoManageDeploymentSites.Value ? "central (AutoManageDeploymentSites=true)" : "edge (AutoManageDeploymentSites=false)")
                : "legacy (unknown)");
        _connectionManager.AddOperator(Context.ConnectionId);
        _connectionManager.SetOperatorMode(Context.ConnectionId, autoManageDeploymentSites);
        return Task.FromResult(_connectionManager.GetDeployedDeploymentSites());
    }

    /// <inheritdoc />
    public async Task ReportDeployedStateAsync(IReadOnlyList<OperatorDeployedDeploymentSiteReportDto> deployedDeploymentSites)
    {
        var operatorMode = _connectionManager.GetOperatorMode(Context.ConnectionId);
        if (operatorMode != true)
        {
            // Edge operators and legacy (mode==null) operators must not
            // restore state via this path — their helm releases live on a
            // different cluster than the controller-managed Cloud deploymentSites.
            // Throw a typed HubException so the SDK surfaces a useful
            // error message instead of a generic SignalR failure.
            var modeLabel = operatorMode == false ? "edge (AutoManageDeploymentSites=false)" : "legacy (unknown)";
            await _eventService.StoreErrorEventAsync(string.Empty,
                $"ReportDeployedStateAsync rejected: operator connection '{Context.ConnectionId}' is in {modeLabel} mode. " +
                "Only Cloud operators (AutoManageDeploymentSites=true) may reverse-sync deployed state.");
            throw new HubException(
                $"ReportDeployedStateAsync is only allowed for Cloud operators (AutoManageDeploymentSites=true); " +
                $"this connection declared mode: {modeLabel}.");
        }

        Logger.Info(
            "Operator '{ConnectionId}' reverse-syncs deployed state: {Count} deploymentSite report(s)",
            Context.ConnectionId, deployedDeploymentSites.Count);

        await _deploymentSiteService.RestoreDeployedStateAsync(Context.ConnectionId, deployedDeploymentSites);
    }

    /// <inheritdoc />
    public async Task UnregisterOperatorAsync()
    {
        var disconnectingConnectionId = Context.ConnectionId;
        Logger.Info("Operator unregistered with connection id '{ConnectionId}'", disconnectingConnectionId);
        var orphaned = _connectionManager.RemoveOperator(disconnectingConnectionId);
        foreach (var (tenantId, deploymentSiteRtId) in orphaned)
        {
            try
            {
                await _deploymentSiteService.SetCommunicationStateOfflineAsync(tenantId,
                    new OctoObjectId(deploymentSiteRtId), disconnectingConnectionId);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex,
                    "Failed to mark deploymentSite (rtId {DeploymentSiteRtId}) offline on operator unregister (tenant '{TenantId}')",
                    deploymentSiteRtId, tenantId);
            }
        }
    }

    /// <inheritdoc />
    public async Task RegisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        Logger.Info(
            "Operator '{ConnectionId}' claims deploymentSite (rtId {DeploymentSiteRtId}) for tenant '{TenantId}'",
            Context.ConnectionId, deploymentSiteRtId, tenantId);

        // Validate deploymentSiteRtId up-front. An empty / malformed value used to
        // surface as a FormatException from `new OctoObjectId(deploymentSiteRtId)`
        // a few lines down, which SignalR then wrapped in a generic
        // HubException ("'' is not a valid 24 digit hex string"). The
        // operator could not act on that — it just kept retrying the
        // same broken CR. Failing here returns a typed HubException with
        // a message that names the offending field, so the operator log
        // immediately points at the misconfigured CR spec.
        if (!OctoObjectId.TryParse(deploymentSiteRtId, out var poolObjectId))
        {
            Logger.Warn(
                "Rejecting RegisterPool: deploymentSiteRtId '{DeploymentSiteRtId}' is not a valid 24-character hex ObjectId " +
                "(tenant '{TenantId}', connection '{ConnectionId}')",
                deploymentSiteRtId, tenantId, Context.ConnectionId);
            throw new HubException(
                $"Invalid deploymentSiteRtId '{deploymentSiteRtId}' (tenant '{tenantId}'): " +
                "must be a 24-character hex ObjectId. Check the DeploymentSite CR spec.");
        }

        // Validate that the calling operator's mode matches the deploymentSite's
        // Environment before flipping state Online. This blocks an edge
        // operator that picked up a CR for a Cloud deploymentSite (e.g. one materialized
        // by the now-fixed reconnect bug, or by a misfired
        // deploy-edge-deploymentSite.yml run) from claiming ownership and receiving
        // workload deploy events. A legacy operator that did not declare a
        // mode is logged + audited but allowed through, so rolling upgrades
        // do not break existing connections.
        var operatorMode = _connectionManager.GetOperatorMode(Context.ConnectionId);
        if (operatorMode.HasValue)
        {
            var rtDeploymentSite = (await _communicationRepository.GetDeploymentSitesAsync(tenantId))
                .FirstOrDefault(p => p.RtId == poolObjectId);
            if (rtDeploymentSite == null)
            {
                Logger.Warn(
                    "Rejecting RegisterPool: no RtDeploymentSite with rtId {DeploymentSiteRtId} for tenant '{TenantId}' " +
                    "(connection '{ConnectionId}')",
                    deploymentSiteRtId, tenantId, Context.ConnectionId);
                await _eventService.StoreErrorEventAsync(tenantId,
                    $"Operator (connection '{Context.ConnectionId}', AutoManageDeploymentSites={operatorMode.Value}) " +
                    $"attempted to register deployment site rtId {deploymentSiteRtId} but no such deploymentSite exists.");
                throw new HubException(
                    $"Deployment site rtId {deploymentSiteRtId} does not exist for tenant '{tenantId}'.");
            }

            var poolIsCloud = rtDeploymentSite.Environment == RtEnvironmentEnum.Cloud;
            var operatorIsCentral = operatorMode.Value;
            if (poolIsCloud != operatorIsCentral)
            {
                var operatorRole = operatorIsCentral ? "central (AutoManageDeploymentSites=true)" : "edge (AutoManageDeploymentSites=false)";
                var poolEnv = poolIsCloud ? "Cloud" : "Edge";
                Logger.Warn(
                    "Rejecting RegisterPool: operator is {OperatorRole} but deploymentSite '{DeploymentSiteName}' (rtId {DeploymentSiteRtId}) " +
                    "is {PoolEnv} (tenant '{TenantId}', connection '{ConnectionId}')",
                    operatorRole, rtDeploymentSite.Name, deploymentSiteRtId, poolEnv, tenantId, Context.ConnectionId);
                await _eventService.StoreErrorEventAsync(tenantId,
                    $"Rejected deploymentSite registration: deploymentSite '{rtDeploymentSite.Name}' (rtId {deploymentSiteRtId}) " +
                    $"is {poolEnv} but operator (connection '{Context.ConnectionId}') is {operatorRole}. " +
                    "Check the DeploymentSite CR and the operator's deployment mode.");
                throw new HubException(
                    $"DeploymentSite '{rtDeploymentSite.Name}' (rtId {deploymentSiteRtId}) is {poolEnv}; " +
                    $"a {operatorRole} operator cannot claim it. " +
                    "Check the operator's AutoManageDeploymentSites setting and the deploymentSite's Environment.");
            }
        }
        else
        {
            Logger.Info(
                "Operator '{ConnectionId}' registered without mode declaration (legacy); skipping " +
                "Environment/mode check for deployment site rtId {DeploymentSiteRtId} (tenant '{TenantId}')",
                Context.ConnectionId, deploymentSiteRtId, tenantId);
            await _eventService.StoreInformationEventAsync(tenantId,
                $"Legacy operator (connection '{Context.ConnectionId}') registered deployment site rtId {deploymentSiteRtId} " +
                "without declaring a mode; Environment/mode enforcement skipped.");
        }

        // Track the (connection, tenant, deploymentSite) tuple before flipping state —
        // if state-write fails we still want OnDisconnectedAsync to clean
        // up so the entity doesn't stay stuck on Online.
        _connectionManager.RegisterDeploymentSiteForConnection(Context.ConnectionId, tenantId, deploymentSiteRtId);

        try
        {
            await _deploymentSiteService.SetCommunicationStateOnlineAsync(tenantId,
                poolObjectId, Context.ConnectionId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex,
                "Failed to mark deploymentSite (rtId {DeploymentSiteRtId}) online (tenant '{TenantId}')",
                deploymentSiteRtId, tenantId);
            throw;
        }

        // Replay any workload deploy/undeploy the controller had to queue
        // while no operator owned this deploymentSite (AB#4371) — e.g. an undeploy
        // triggered while the operator's registration was failing
        // transiently. Runs after the state write so a failed registration
        // (rethrown above) does not consume the queue.
        await _connectionManager.FlushPendingWorkloadNotificationsAsync(
            Context.ConnectionId, tenantId, deploymentSiteRtId);

        // Re-dispatch workloads stranded in Pending (AB#4894): a deploy
        // notification sent to the PREVIOUS operator pod while it was being
        // replaced is lost silently and never enters the AB#4371 queue (the
        // deploymentSite had a registered — dying — owner at send time). Best effort,
        // never fails the registration.
        await _deploymentSiteService.ReconcilePendingWorkloadsAsync(tenantId, poolObjectId);
    }

    /// <inheritdoc />
    public async Task UnregisterDeploymentSiteAsync(string tenantId, string deploymentSiteRtId)
    {
        Logger.Info(
            "Operator '{ConnectionId}' releases deploymentSite (rtId {DeploymentSiteRtId}) for tenant '{TenantId}'",
            Context.ConnectionId, deploymentSiteRtId, tenantId);

        // Validate up-front (same rationale as RegisterDeploymentSiteAsync). Bad
        // input here used to surface as FormatException from
        // `new OctoObjectId(deploymentSiteRtId)`, swallowed by the catch below as
        // a generic warning that obscured the actual cause.
        if (!OctoObjectId.TryParse(deploymentSiteRtId, out var poolObjectId))
        {
            Logger.Warn(
                "Rejecting UnregisterPool: deploymentSiteRtId '{DeploymentSiteRtId}' is not a valid 24-character hex ObjectId " +
                "(tenant '{TenantId}', connection '{ConnectionId}')",
                deploymentSiteRtId, tenantId, Context.ConnectionId);
            throw new HubException(
                $"Invalid deploymentSiteRtId '{deploymentSiteRtId}' (tenant '{tenantId}'): " +
                "must be a 24-character hex ObjectId.");
        }

        _connectionManager.UnregisterDeploymentSiteForConnection(Context.ConnectionId, tenantId, deploymentSiteRtId);

        try
        {
            await _deploymentSiteService.UnregisterDeploymentSiteOperatorAsync(tenantId, poolObjectId);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex,
                "Failed to unregister deploymentSite (rtId {DeploymentSiteRtId}); state may stay Online until disconnect (tenant '{TenantId}')",
                deploymentSiteRtId, tenantId);
        }
    }

    /// <inheritdoc />
    public async Task ReportWorkloadDeploymentStatusAsync(WorkloadDeploymentStatusDto status)
    {
        Logger.Info(
            "Workload deployment status report: tenant '{TenantId}', workload '{WorkloadName}' (rtId {WorkloadRtId}), success={Success}",
            status.TenantId, status.WorkloadName, status.WorkloadRtId, status.Success);

        if (string.IsNullOrWhiteSpace(status.TenantId) || string.IsNullOrWhiteSpace(status.WorkloadRtId))
        {
            Logger.Warn("Ignoring deployment status report with missing tenant id or workload rt id");
            return;
        }

        var newState = status.Success
            ? RtDeploymentStateEnum.Deployed
            : RtDeploymentStateEnum.Error;

        try
        {
            // The DTO doesn't carry the workload's CK type, so we read the
            // entity to discover whether it's an Adapter or Application and
            // route to the matching repository setter. (Earlier this method
            // always wrote to the Adapter setter — Application status reports
            // never landed in MongoDB and the UI stayed stuck at Pending.)
            var workloadRtId = new OctoObjectId(status.WorkloadRtId);
            var workload = await _communicationRepository.GetWorkloadByRtIdAsync(status.TenantId, workloadRtId);
            if (workload == null)
            {
                Logger.Warn(
                    "Workload '{WorkloadRtId}' (tenant '{TenantId}') reported deployment status but no entity exists in the repository; skipping",
                    status.WorkloadRtId, status.TenantId);
                return;
            }

            switch (workload)
            {
                case RtApplication:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workloadRtId);
                        await _communicationRepository.SetApplicationDeploymentStateAsync(
                            status.TenantId, rtEntityId, newState, status.StatusMessage);
                        break;
                    }
                // 🔴 AB#4924 — before RtAdapter, and present at all. An adapter pool is the third
                // DeployableWorkload and this switch only ever knew two: its helm release rolled
                // out, its pod came up, its member registered, and the entity sat at Pending for
                // ever while the log said "unsupported type 'RtAdapterPool'; skipping status
                // persist". Same hole the deploy path had (DeploymentSiteService
                // .SetWorkloadDeploymentStateAsync) and the same fix; this one was missed because
                // the deploy path is what the increment's tests exercise, and nothing asserts what
                // the OPERATOR reports back. Observed on a local kind cluster.
                case RtAdapterPool:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterPoolTypeId, workloadRtId);
                        await _communicationRepository.SetAdapterPoolDeploymentStateAsync(
                            status.TenantId, rtEntityId, newState, status.StatusMessage);
                        break;
                    }
                case RtAdapter:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workloadRtId);
                        await _communicationRepository.SetAdapterDeploymentStateAsync(
                            status.TenantId, rtEntityId, newState, status.StatusMessage);
                        break;
                    }
                default:
                    Logger.Warn(
                        "Workload '{WorkloadRtId}' (tenant '{TenantId}') is of unsupported type '{Type}'; skipping status persist",
                        status.WorkloadRtId, status.TenantId, workload.GetType().Name);
                    break;
            }
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

    /// <inheritdoc />
    public async Task ReportWorkloadScaleStatusAsync(WorkloadScaleStatusDto status)
    {
        Logger.Info(
            "Workload scale status report: tenant '{TenantId}', workload '{WorkloadName}' (rtId {WorkloadRtId}), replicas={Replicas}, success={Success}",
            status.TenantId, status.WorkloadName, status.WorkloadRtId, status.Replicas, status.Success);

        if (string.IsNullOrWhiteSpace(status.TenantId) || string.IsNullOrWhiteSpace(status.WorkloadRtId))
        {
            Logger.Warn("Ignoring scale status report with missing tenant id or workload rt id");
            return;
        }

        // The lifecycle service owns the state machine and is itself best-effort
        // (same contract as the deployment status reports: a failed state write
        // must not break the hub for the rest of the connection's traffic).
        await _workloadLifecycleService.OnScaleStatusReportedAsync(status);
    }

    /// <inheritdoc />
    public async Task ReportWorkloadDeploymentProgressAsync(WorkloadDeploymentProgressDto progress)
    {
        Logger.Debug(
            "Workload deployment progress report: tenant '{TenantId}', workload '{WorkloadName}' (rtId {WorkloadRtId})",
            progress.TenantId, progress.WorkloadName, progress.WorkloadRtId);

        if (string.IsNullOrWhiteSpace(progress.TenantId) || string.IsNullOrWhiteSpace(progress.WorkloadRtId))
        {
            Logger.Warn("Ignoring deployment progress report with missing tenant id or workload rt id");
            return;
        }

        try
        {
            // Same routing logic as ReportWorkloadDeploymentStatusAsync (above):
            // look up the entity to discover whether it's an Adapter or
            // Application and dispatch to the matching repository setter.
            var workloadRtId = new OctoObjectId(progress.WorkloadRtId);
            var workload = await _communicationRepository.GetWorkloadByRtIdAsync(progress.TenantId, workloadRtId);
            if (workload == null)
            {
                Logger.Warn(
                    "Workload '{WorkloadRtId}' (tenant '{TenantId}') reported deployment progress but no entity exists in the repository; skipping",
                    progress.WorkloadRtId, progress.TenantId);
                return;
            }

            // Stays Pending — helm may still recover. The terminal
            // ReportWorkloadDeploymentStatusAsync is the only path that
            // writes Deployed / Error. ApplyDeploymentErrorTracking clears
            // LastDeploymentError for Pending which is correct here:
            // we're still inside a single attempt, the message belongs on
            // StatusMessage (live), not LastDeploymentError (persistent).
            switch (workload)
            {
                case RtApplication:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, workloadRtId);
                        await _communicationRepository.SetApplicationDeploymentStateAsync(
                            progress.TenantId, rtEntityId, RtDeploymentStateEnum.Pending, progress.Message);
                        break;
                    }
                // AB#4924 — the third DeployableWorkload, same omission as in the terminal report
                // above. Less damaging here (progress only ever writes Pending, which is where a
                // deploying pool already is) but it means a pool's live status message never
                // reaches the UI, which is the one thing progress exists for.
                case RtAdapterPool:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterPoolTypeId, workloadRtId);
                        await _communicationRepository.SetAdapterPoolDeploymentStateAsync(
                            progress.TenantId, rtEntityId, RtDeploymentStateEnum.Pending, progress.Message);
                        break;
                    }
                case RtAdapter:
                    {
                        var rtEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkAdapterTypeId, workloadRtId);
                        await _communicationRepository.SetAdapterDeploymentStateAsync(
                            progress.TenantId, rtEntityId, RtDeploymentStateEnum.Pending, progress.Message);
                        break;
                    }
                default:
                    Logger.Warn(
                        "Workload '{WorkloadRtId}' (tenant '{TenantId}') is of unsupported type '{Type}'; skipping progress persist",
                        progress.WorkloadRtId, progress.TenantId, workload.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Same rationale as ReportWorkloadDeploymentStatusAsync: progress
            // is best-effort, a transient write failure must not break the
            // hub for the rest of this connection's traffic.
            Logger.Error(ex,
                "Failed to persist deployment progress for workload '{WorkloadName}' (tenant '{TenantId}')",
                progress.WorkloadName, progress.TenantId);
        }
    }
}
