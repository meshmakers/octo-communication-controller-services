using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Typed errors of the on-demand workload lifecycle (AB#4914/AB#4918).
/// </summary>
internal class WorkloadLifecycleServiceException : Exception
{
    private WorkloadLifecycleServiceException(string message) : base(message)
    {
    }

    /// <summary>
    ///     The wake gate exhausted its budget without the workload reaching
    ///     <c>ConfigurationState=Configured</c>. The workload's lifecycle state has been
    ///     reverted to <c>Hibernated</c>; the deployment is left scaled up for diagnosis and
    ///     the idle watchdog re-hibernates it after the idle timeout.
    /// </summary>
    internal static Exception WakeTimedOut(string tenantId, OctoObjectId workloadRtId, string? workloadName,
        TimeSpan budget)
    {
        return new WorkloadLifecycleServiceException(
            $"[{tenantId}] Waking workload '{workloadName ?? workloadRtId.ToString()}' did not reach the Configured state " +
            $"within {budget.TotalSeconds:F0}s. The wake request is still in progress in the cluster - retry shortly, " +
            "or check the workload's deployment status.");
    }

    /// <summary>
    ///     The operator reported the scale-to-1 of a waking workload as failed (e.g. the helm
    ///     release has no Deployments). Waiters are failed fast instead of burning the full
    ///     wake budget.
    /// </summary>
    internal static Exception WakeScaleFailed(string tenantId, string? workloadName, string? statusMessage)
    {
        return new WorkloadLifecycleServiceException(
            $"[{tenantId}] Waking workload '{workloadName}' failed: the operator could not scale it up ({statusMessage}). " +
            "Check the workload's deployment status.");
    }
}
