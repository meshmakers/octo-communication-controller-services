using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IPipelineExecutionClassService" />
internal class PipelineExecutionClassService(
    IAdapterNodeCapabilityService adapterNodeCapabilityService,
    IPipelineDefinitionService pipelineDefinitionService)
    : IPipelineExecutionClassService
{
    /// <summary>Key 0 of the <c>PipelineExecutionClass</c> CK enum.</summary>
    private const int Interactive = 0;

    /// <summary>Key 1 of the <c>PipelineExecutionClass</c> CK enum, and the CK attribute default.</summary>
    private const int Batch = 1;

    /// <summary>
    ///     Fallback classification for adapters whose node descriptors are not available (AB#4924).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>This list exists because descriptors are not persisted.</b>
    ///         <c>Adapter.NodeDescriptors</c> is in-memory only and is null whenever the adapter has
    ///         not registered during this controller process's lifetime — an offline adapter, a
    ///         hibernated one, or simply a controller that restarted. Without a fallback, the class
    ///         of a pipeline would depend on whether its adapter happened to be connected at the
    ///         moment somebody saved it, which is a non-deterministic answer to a question the
    ///         surfaces have to be able to explain.
    ///     </para>
    ///     <para>
    ///         ⚠️ It is knowingly the same wart as <c>KnownProcessBoundTriggerNames</c> in
    ///         <see cref="WorkloadOnDemandCapabilityService" />: a hardcoded list drifts from the
    ///         nodes it describes. It is kept deliberately short — only the triggers where a human
    ///         is demonstrably waiting — and a self-describing descriptor always wins over it, so
    ///         the drift degrades to "classified Batch", never to a wrong Interactive.
    ///     </para>
    /// </remarks>
    private static readonly HashSet<string> KnownInteractiveTriggerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FromHttpRequest",
        "FromExecutePipelineCommand"
    };

    public int Resolve(string? pipelineDefinition, IReadOnlyList<NodeDescriptorDto>? nodeDescriptors)
    {
        if (string.IsNullOrEmpty(pipelineDefinition))
        {
            return Batch;
        }

        // TryGetTriggerNodes, not GetAllNodes: malformed YAML must not silently produce "no
        // triggers → Batch" indistinguishably from a valid pipeline that has none (AB#5113 made
        // that distinction available; this is the same trap).
        if (!pipelineDefinitionService.TryGetTriggerNodes(pipelineDefinition, out var triggers) ||
            triggers.Count == 0)
        {
            return Batch;
        }

        var interactiveByQualifiedName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in nodeDescriptors ?? [])
        {
            // Only a trigger's declaration counts. The descriptor scan records a class for any
            // node that carries the attribute, but a transform saying "Interactive" means nothing.
            if (descriptor is { IsTrigger: true, ExecutionClass: Interactive })
            {
                interactiveByQualifiedName.Add($"{descriptor.NodeName}@{descriptor.Version}");
            }
        }

        // A pipeline with several triggers takes the most urgent class any of them implies: if one
        // way of starting this pipeline has somebody waiting on it, the work is interactive
        // whenever it arrives that way. Erring towards Interactive here is bounded — the class only
        // orders work inside one tenant's own turn and can never displace another tenant.
        foreach (var trigger in triggers)
        {
            if (interactiveByQualifiedName.Contains(trigger.NodeType) ||
                KnownInteractiveTriggerNames.Contains(StripVersion(trigger.NodeType)))
            {
                return Interactive;
            }
        }

        return Batch;
    }

    public async Task<int> ResolveForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId,
        string? pipelineDefinition, RtAdapter? adapter = null)
    {
        // 🔴 AB#4924: for a Leased adapter this resolves against the LENDING POOL's member
        // descriptors, not against AdapterById. A leased adapter never holds an adapter-hub
        // connection, so the old cache lookup always missed and every leased pipeline was persisted
        // with the CK default Batch — which made the Interactive-before-Batch ordering of the
        // scheduler unobservable on the only path where it matters.
        var capabilities = await adapterNodeCapabilityService.ResolveAsync(tenantId, adapterRtEntityId, adapter);
        return Resolve(pipelineDefinition, capabilities.NodeDescriptors);
    }

    private static string StripVersion(string nodeType)
    {
        var atIndex = nodeType.IndexOf('@');
        return atIndex < 0 ? nodeType : nodeType[..atIndex];
    }
}
