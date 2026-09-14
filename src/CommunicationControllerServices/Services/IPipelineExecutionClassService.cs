using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Resolves the scheduling class of a pipeline from its trigger node (AB#4924, concept §5).
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <see cref="IWorkloadOnDemandCapabilityService" />: a capability declared by the
///         trigger node, picked up by the reflection-based descriptor scan, with a known-name
///         fallback for adapters whose descriptors are not available. The difference is where the
///         answer lands — on-demand capability is a property of a <b>workload</b>, the execution
///         class is a property of a <b>pipeline</b>.
///     </para>
///     <para>
///         🔴 Resolved when the pipeline is <b>saved</b> and persisted on the entity, not derived at
///         dequeue time. Parsing YAML on every dequeue would put a parser on the scheduler's hot
///         path, and — the reason that actually matters — it would leave the class invisible until
///         something ran. Persisted, the scheduler reads a field, the three surfaces can explain
///         why a job sits where it does, and a pipeline whose class changed shows up as a change.
///     </para>
///     <para>
///         ⚠️ Same caveat as <c>OnDemandCapable</c>: a pipeline edited to a different trigger changes
///         class <b>on save</b>, so every surface must read the current value rather than cache it.
///     </para>
/// </remarks>
public interface IPipelineExecutionClassService
{
    /// <summary>
    ///     Resolves the class a pipeline definition implies. Returns <c>Batch</c> for a definition
    ///     with no trigger, an unrecognised trigger, or malformed YAML — the conservative answer,
    ///     because a pipeline nobody could classify must never jump a queue.
    /// </summary>
    int Resolve(string? pipelineDefinition, IReadOnlyList<NodeDescriptorDto>? nodeDescriptors);

    /// <summary>
    ///     Resolves the class for a pipeline about to be deployed to an adapter, using that
    ///     adapter's live node descriptors when it is connected.
    /// </summary>
    int ResolveForAdapter(string tenantId, RtEntityId adapterRtEntityId, string? pipelineDefinition);
}
