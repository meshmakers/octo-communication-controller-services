using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class WorkloadOnDemandCapabilityService(
    ICommunicationRepository communicationRepository,
    IAdapterCache adapterCache,
    IPipelineDefinitionService pipelineDefinitionService)
    : IWorkloadOnDemandCapabilityService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Fallback classification for adapters running SDK versions that do not yet send the
    /// RequiresRunningProcess descriptor flag (AB#4984). Node base names (version-agnostic,
    /// case-insensitive) of the first-party process-bound triggers; see the design doc §5
    /// (docs/concepts/on-demand-adapter-lifecycle.md).
    /// </summary>
    private static readonly HashSet<string> KnownProcessBoundTriggerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FromPolling",
        "FromWatchRtEntity",
        "FromMicrosoftGraphEmail",
        "FromMicrosoftGraph",
        "FromEmail",
        "FromSignal",
        "FromTeamsBot",
        "FromSendNotification",
        "FromLoxoneStateChange",
        // AB#5228 — the audit that followed the LoxonePollTrigger@1 miss. Every name below is a
        // trigger whose firing depends on THIS adapter process staying alive; each one now also
        // carries [NodeRequiresRunningProcess] in its own repo, so a current SDK self-describes it
        // and this list only covers adapters still on an SDK older than 3.4.105 / 0.2-dev.
        "LoxonePollTrigger",        // octo-adapter-loxone: in-process poll loop
        "MqttTrigger",              // octo-adapter-mqtt: long-lived MQTT client subscription
        "DemoTrigger",              // octo-adapter-mqtt + octo-adapter-demos (identical source): TCP listener
        "FromZenonCel",             // octo-plug-zenon: in-process zenon runtime subscription
        "FromZenonVariableChanged", // octo-plug-zenon: in-process zenon runtime subscription
        "FromZenonAml",             // octo-plug-zenon: in-process zenon runtime subscription
        "FromRfcServerCall"         // octo-adapter-sap: RFC server socket hosted in the process
    };

    public async Task<OnDemandCapabilityResult> EvaluateAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        var nodeDescriptors = GetNodeDescriptors(tenantId, adapterRtEntityId);
        var pipelines = await communicationRepository.GetPipelinesAsync(tenantId, adapterRtEntityId);

        var reasons = new List<string>();
        foreach (var pipeline in pipelines)
        {
            var processBoundNodes = GetProcessBoundNodes(pipeline.PipelineDefinition, nodeDescriptors);
            reasons.AddRange(processBoundNodes.Select(nodeType =>
                $"Pipeline '{pipeline.Name ?? pipeline.RtId.ToString()}' uses process-bound trigger '{nodeType}'"));
        }

        return new OnDemandCapabilityResult(reasons.Count == 0, reasons);
    }

    public IReadOnlyList<string> GetProcessBoundNodes(string? pipelineDefinition,
        IReadOnlyList<NodeDescriptorDto>? nodeDescriptors)
    {
        if (string.IsNullOrEmpty(pipelineDefinition))
        {
            return [];
        }

        // Descriptor self-description (new SDKs): qualified name -> RequiresRunningProcess
        var processBoundByQualifiedName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in nodeDescriptors ?? [])
        {
            if (descriptor.RequiresRunningProcess)
            {
                processBoundByQualifiedName.Add($"{descriptor.NodeName}@{descriptor.Version}");
            }
        }

        return pipelineDefinitionService.GetAllNodes(pipelineDefinition)
            .Select(n => n.NodeType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(nodeType => processBoundByQualifiedName.Contains(nodeType) ||
                               KnownProcessBoundTriggerNames.Contains(StripVersion(nodeType)))
            .ToList();
    }

    public async Task RefreshWorkloadCapabilityAsync(string tenantId, RtEntityId adapterRtEntityId)
    {
        try
        {
            var result = await EvaluateAsync(tenantId, adapterRtEntityId);
            await communicationRepository.SetWorkloadOnDemandCapabilityAsync(tenantId, adapterRtEntityId.RtId,
                result.IsCapable, result.BlockingReasons.Count == 0 ? null : string.Join("; ", result.BlockingReasons));

            Logger.Debug("[{TenantId}] Workload '{AdapterRtId}' on-demand capability refreshed: {Capable} ({Reasons})",
                tenantId, adapterRtEntityId, result.IsCapable, result.BlockingReasons.Count);
        }
        catch (Exception e)
        {
            // Best-effort by contract: the persisted value is a Studio display aid; validation
            // paths (PoolService, DeployPipelineAsync) always evaluate live and are unaffected.
            Logger.Warn(e, "[{TenantId}] Failed to refresh on-demand capability for workload '{AdapterRtId}'",
                tenantId, adapterRtEntityId);
        }
    }

    private IReadOnlyList<NodeDescriptorDto>? GetNodeDescriptors(string tenantId, RtEntityId adapterRtEntityId)
    {
        if (adapterCache.TryGetTenant(tenantId, out var adapterTenant) &&
            adapterTenant.AdapterById.TryGetValue(adapterRtEntityId, out var adapter))
        {
            return adapter.NodeDescriptors;
        }

        return null;
    }

    private static string StripVersion(string nodeType)
    {
        var atIndex = nodeType.IndexOf('@');
        return atIndex < 0 ? nodeType : nodeType[..atIndex];
    }
}
