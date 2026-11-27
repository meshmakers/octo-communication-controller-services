using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>Where an execute command of a manual adapter goes (AB#10015).</summary>
internal enum ExecuteCommandRoute
{
    /// <summary>The execute address that <c>FromExecutePipelineCommand@1</c> consumes (also: unknown definition).</summary>
    ExecuteCommand,

    /// <summary>The dry-run address that only <c>FromHttpRequest@2</c> consumes.</summary>
    HttpDryRun
}

/// <summary>
///     Decides, from the trigger section of the pipeline definition, where the execute command of a manual adapter
///     goes, or refuses it before anything is sent (AB#10015).
/// </summary>
/// <remarks>
///     <para>
///         The execute address <c>execute-pipeline-{tenant}-pipeline-{rtId}</c> is consumed only by
///         <c>FromExecutePipelineCommand@1</c>. A pipeline triggered only by <c>FromHttpRequest@2</c> had no consumer
///         there: every execute and dry run waited for the 30 s timeout and failed with "adapter did not answer".
///     </para>
///     <para>
///         🔴 <b>Invariant: an HTTP-only pipeline runs for real only through its route.</b> A real execute of it is
///         refused here; a dry run goes to <see cref="AddressSuffix" />, where the adapter's HTTP trigger applies the
///         route's caller rules again. A pipeline with an execute trigger keeps the execute address (one consumer per
///         address, never two exclusive endpoints). Pipelines with other triggers only (cron, events) keep today's
///         path: out of scope of AB#10015.
///     </para>
///     <para>Must stay in sync with <c>HttpTriggerDryRun</c> in octo-mesh-adapter.</para>
/// </remarks>
internal static class HttpTriggerDryRunRouting
{
    /// <summary>Appended to the execute address of the pipeline; the mesh adapter uses the same suffix.</summary>
    internal const string AddressSuffix = "-http-dry-run";

    private const string ExecuteTrigger = "FromExecutePipelineCommand";
    private const string HttpTrigger = "FromHttpRequest";
    private const string HttpTriggerV2 = "FromHttpRequest@2";

    /// <summary>
    ///     The route of the command, or the reason it is refused. <paramref name="triggerNodeTypes" /> <c>null</c> means
    ///     the definition is unknown or does not parse: the command keeps the execute address, as before.
    /// </summary>
    internal static (ExecuteCommandRoute Route, string? Refusal) Decide(IReadOnlyCollection<string>? triggerNodeTypes,
        bool isDryRun, OctoObjectId pipelineRtId)
    {
        if (triggerNodeTypes == null || triggerNodeTypes.Any(t => IsNode(t, ExecuteTrigger)))
        {
            return (ExecuteCommandRoute.ExecuteCommand, null);
        }

        var httpTriggers = triggerNodeTypes.Where(t => IsNode(t, HttpTrigger)).ToArray();
        if (httpTriggers.Length == 0)
        {
            return (ExecuteCommandRoute.ExecuteCommand, null);
        }

        if (!httpTriggers.Contains(HttpTriggerV2, StringComparer.OrdinalIgnoreCase))
        {
            return (ExecuteCommandRoute.ExecuteCommand,
                $"Pipeline {pipelineRtId} is triggered only by {string.Join(", ", httpTriggers)} and has no " +
                "FromExecutePipelineCommand trigger: it can neither be executed nor dry-run through the execute " +
                "command. Call its route, or move it to FromHttpRequest@2, which supports a dry run.");
        }

        if (!isDryRun)
        {
            return (ExecuteCommandRoute.ExecuteCommand,
                $"Pipeline {pipelineRtId} is triggered by its HTTP route (FromHttpRequest@2) and has no " +
                "FromExecutePipelineCommand trigger: it runs for real only through its route. A dry run runs it with " +
                "the input as request body.");
        }

        return (ExecuteCommandRoute.HttpDryRun, null);
    }

    private static bool IsNode(string nodeType, string name)
    {
        var at = nodeType.IndexOf('@');
        return string.Equals(at < 0 ? nodeType : nodeType[..at], name, StringComparison.OrdinalIgnoreCase);
    }
}
