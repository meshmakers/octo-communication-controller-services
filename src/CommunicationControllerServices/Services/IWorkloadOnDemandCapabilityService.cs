using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Result of an on-demand capability evaluation (AB#4984).
/// </summary>
/// <param name="IsCapable">True iff no pipeline of the workload uses a process-bound trigger.</param>
/// <param name="BlockingReasons">
///     One human-readable reason per process-bound trigger usage
///     (e.g. "Pipeline 'Invoices' uses process-bound trigger 'FromPolling@1'"). Empty when capable.
/// </param>
public record OnDemandCapabilityResult(bool IsCapable, IReadOnlyList<string> BlockingReasons);

/// <summary>
/// Computes whether a workload can safely run with LifecycleMode=OnDemand (AB#4984).
/// A workload is on-demand capable iff none of its pipelines uses a process-bound trigger —
/// a trigger that only fires while the adapter process is running (in-process polling,
/// in-memory subscriptions). Hibernating such a workload would silently stop the trigger.
/// Classification sources: the RequiresRunningProcess flag on registered node descriptors
/// (self-description, new SDKs) with a known-name fallback list for adapters on older SDKs.
/// </summary>
public interface IWorkloadOnDemandCapabilityService
{
    /// <summary>
    /// Evaluates on-demand capability for an adapter workload from its persisted pipeline
    /// definitions. Works while the adapter is offline or hibernated — definitions are
    /// stored on the pipeline entities; live node descriptors only refine the classification.
    /// </summary>
    Task<OnDemandCapabilityResult> EvaluateAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// Classifies a single pipeline definition; returns the qualified names of all
    /// process-bound trigger nodes it uses (empty = on-demand compatible).
    /// </summary>
    IReadOnlyList<string> GetProcessBoundNodes(string? pipelineDefinition,
        IReadOnlyList<NodeDescriptorDto>? nodeDescriptors);

    /// <summary>
    /// AB#5278: the <c>Leased</c> evaluation. Stricter than <see cref="EvaluateAsync"/>: besides the
    /// process-bound triggers (which a borrowed process can never fire) it refuses the triggers that
    /// are wake-capable on a dedicated adapter but cannot produce a lease, because their message never
    /// reaches the controller — <c>FromHttpRequest</c> (the pool has no ingress yet, AB#5258) and
    /// <c>FromPipelineDataEvent</c> (adapter-to-adapter over the broker, AB#5231). A cron
    /// <c>PipelineTrigger</c> and an explicit <c>ExecutePipeline</c> are the two ways work reaches a
    /// leased adapter. Explicit beats silent: a pipeline that passed this gate runs on a lease; one
    /// that did not is refused here instead of never running.
    /// </summary>
    Task<OnDemandCapabilityResult> EvaluateForLeaseAsync(string tenantId, RtEntityId adapterRtEntityId);

    /// <summary>
    /// AB#5278: the qualified names of the trigger nodes in a definition that cannot produce a lease
    /// (see <see cref="EvaluateForLeaseAsync"/>). Empty = every trigger of the pipeline reaches the
    /// controller. Independent of process-boundness, which <see cref="GetProcessBoundNodes"/> answers.
    /// </summary>
    IReadOnlyList<string> GetLeaseIncapableNodes(string? pipelineDefinition);

    /// <summary>
    /// Evaluates and persists OnDemandCapable / OnDemandBlockingReasons on the workload
    /// entity for display in the Studio. Best-effort: never throws.
    /// </summary>
    Task RefreshWorkloadCapabilityAsync(string tenantId, RtEntityId adapterRtEntityId);
}
