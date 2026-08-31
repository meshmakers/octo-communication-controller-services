using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Owns the on-demand workload lifecycle state machine (AB#4914):
///     Running → Draining → Hibernated → Waking → Running. AB#4917 lands the scale-ack
///     handling; the idle watchdog and the wake gates (AB#4918) drive the remaining
///     transitions through this service.
/// </summary>
public interface IWorkloadLifecycleService
{
    /// <summary>
    ///     Processes the operator's scale status report. A successful scale-to-0 ack completes
    ///     <c>Draining → Hibernated</c>; a failed scale-to-0 reverts <c>Draining → Running</c>
    ///     (the pod is still up); wake acks (replicas ≥ 1) only log — the
    ///     <c>Waking → Running</c> transition is driven by <c>ConfigurationState=Configured</c>,
    ///     not by the scale ack. Best-effort: failures are logged and audited, never thrown.
    /// </summary>
    Task OnScaleStatusReportedAsync(WorkloadScaleStatusDto status);

    /// <summary>
    ///     Sends a scale request for the workload to the operator owning its pool. The caller
    ///     (idle watchdog / wake gate) is responsible for having set the appropriate
    ///     <c>LifecycleState</c> (Draining before scale-0, Waking before scale-1) — this method
    ///     only resolves the pool routing and fires the notification. Throws when the workload
    ///     has no parent pool.
    /// </summary>
    Task RequestScaleAsync(string tenantId, RtDeployableWorkload workload, int replicas);

    /// <summary>
    ///     Wake gate for the execute-pipeline path (AB#4918): resolves the pipeline's adapter
    ///     and ensures it is running before the (non-durable) execute command is sent. No-op
    ///     on tenants without scale-to-zero, on AlwaysOn workloads and on unknown pipelines.
    ///     Throws <c>WorkloadLifecycleServiceException</c> when a wake fails or times out.
    /// </summary>
    Task EnsureWorkloadRunningForPipelineAsync(string tenantId, OctoObjectId pipelineRtId);

    /// <summary>
    ///     Wake gate by workload rtId (config pushes, wake API, cron co-wake). No-op on
    ///     tenants without scale-to-zero, on AlwaysOn workloads and on unknown workloads.
    /// </summary>
    Task EnsureWorkloadRunningAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    ///     Wake gate with a pre-loaded workload. Hibernated/Draining → Waking + scale-1 +
    ///     wait for Configured (budget <c>LifecycleWakeBudgetSeconds</c>); Waking → join the
    ///     wait; Running → stamp <c>LastActivityAt</c>.
    /// </summary>
    Task EnsureWorkloadRunningAsync(string tenantId, RtDeployableWorkload workload);

    /// <summary>
    ///     Called when a workload's configuration push was acked (<c>ConfigurationState=Configured</c>,
    ///     the wake readiness signal per AB#4594). Releases wake waiters and transitions the
    ///     workload to Running. Best-effort — never throws.
    /// </summary>
    Task NotifyWorkloadConfiguredAsync(string tenantId, OctoObjectId workloadRtId);

    /// <summary>
    ///     True while a wake wait is registered on this pod for the workload. Used by the idle
    ///     watchdog to distinguish an in-flight wake from a stale <c>Waking</c> state left
    ///     behind by a controller restart.
    /// </summary>
    bool HasActiveWake(string tenantId, string workloadRtId);

    /// <summary>
    ///     True when the workload is down on purpose or on its way there (<c>Draining</c> /
    ///     <c>Hibernated</c>), so an observed disconnect is the expected outcome of a scale-to-zero
    ///     rather than an incident (AB#4919). Callers use it to keep intentional hibernation out of
    ///     the offline audit trail; the state writes themselves stay unchanged, because
    ///     <c>CommunicationState=Offline</c> remains factually true while hibernated.
    ///     Fast-path <c>false</c> on tenants without scale-to-zero, so the disconnect path pays no
    ///     repository lookup. Never throws: a workload that cannot be read is reported as not
    ///     hibernating, which keeps the established offline handling.
    /// </summary>
    Task<bool> IsIntentionallyDownAsync(string tenantId, OctoObjectId workloadRtId);
}
