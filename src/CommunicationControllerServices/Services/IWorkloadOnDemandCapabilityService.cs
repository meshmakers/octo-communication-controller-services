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
    /// Evaluates and persists OnDemandCapable / OnDemandBlockingReasons on the workload
    /// entity for display in the Studio. Best-effort: never throws.
    /// </summary>
    Task RefreshWorkloadCapabilityAsync(string tenantId, RtEntityId adapterRtEntityId);
}
