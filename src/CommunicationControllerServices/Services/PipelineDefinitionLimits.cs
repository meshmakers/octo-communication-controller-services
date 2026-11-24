using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.Contracts.Serialization;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Size limits the deploy-time schema validation (<see cref="PipelineSchemaValidator" />) checks before it evaluates
///     a caller-supplied pipeline definition, so one deploy cannot exhaust the memory or the CPU of the shared
///     Communication Controller (AB#9667; the same limits as the MCP server's <c>validate_pipeline_definition</c>,
///     AB#9666). A definition above a limit is refused with a finding that starts with <see cref="RefusedCode" /> and
///     names the limit; it is never evaluated. The limits are far above any real pipeline (the largest shipped
///     pipelines have a few hundred nodes and nest control nodes a handful of levels deep).
/// </summary>
/// <remarks>
///     The YAML side (alias expansion, document depth before the recursive YAML load) is bounded by the shared
///     <c>YamlToJsonConverter</c> of Communication.Contracts, which refuses such a document with
///     <c>YamlDocumentLimitExceededException</c>; <see cref="PipelineSchemaValidator" /> reports that refusal as a finding
///     with the same code.
/// </remarks>
internal static class PipelineDefinitionLimits
{
    /// <summary>Prefix of every finding that refuses a definition because of a limit.</summary>
    public const string RefusedCode = "PIPELINE_DEFINITION_LIMIT_EXCEEDED";

    /// <summary>Maximum length of the definition text, in characters.</summary>
    public const int MaxDefinitionLength = 1_000_000;

    /// <summary>Maximum number of pipeline nodes (triggers, transformations and nested node lists).</summary>
    public const int MaxPipelineNodes = 2_000;

    /// <summary>
    ///     Maximum nesting depth of node lists: a root-level node has depth 1, a node in the <c>transformations</c> of a
    ///     root-level control node depth 2, and so on. A level of If, ForEach or Group adds two JSON levels (the list and
    ///     the node), so for them this is below <see cref="MaxJsonDepth" /> and a definition that nests too many of them
    ///     gets this finding. A level of Switch adds four JSON levels (<c>cases</c>, the case, its <c>transformations</c>
    ///     and the node), so about 16 nested Switch nodes already get the generic <see cref="MaxJsonDepth" /> finding
    ///     first; both refuse the definition.
    /// </summary>
    public const int MaxNestingDepth = 24;

    /// <summary>
    ///     Maximum nesting depth of the JSON document handed to the schema validator. NJsonSchema reads it with
    ///     Newtonsoft.Json, whose reader refuses more than 64 levels with an exception; checking it first turns that
    ///     into a finding.
    /// </summary>
    public const int MaxJsonDepth = 64;

    /// <summary>
    ///     Maximum number of findings returned for one definition. Every invalid node yields one finding that lists the
    ///     result of every node schema of the <c>oneOf</c>; the findings travel into a log line, an exception and the
    ///     REST or method answer, so their number is bounded too.
    /// </summary>
    public const int MaxFindings = 50;

    /// <summary>The finding for a definition longer than <see cref="MaxDefinitionLength" />, or null.</summary>
    public static string? CheckLength(string definition) =>
        definition.Length > MaxDefinitionLength
            ? Refusal($"The definition has {definition.Length} characters; the limit is {MaxDefinitionLength}. " +
                      "Split the pipeline or move large data out of the definition.")
            : null;

    /// <summary>
    ///     Checks the converted definition: its JSON nesting depth, the number of pipeline nodes and the nesting depth of
    ///     node lists.
    /// </summary>
    /// <returns>The refusal finding, or null when the definition is within the limits.</returns>
    public static string? CheckNodes(JsonNode? definition)
    {
        if (MeasureJsonDepth(definition, MaxJsonDepth) > MaxJsonDepth)
        {
            return Refusal($"The definition nests more than {MaxJsonDepth} levels deep; the limit is {MaxJsonDepth}.");
        }

        var (count, depth) = MeasureNodes(definition, MaxPipelineNodes, MaxNestingDepth);
        if (depth > MaxNestingDepth)
        {
            return Refusal($"The definition nests node lists more than {MaxNestingDepth} levels deep (transformations " +
                           $"inside control nodes such as If, ForEach or Switch); the limit is {MaxNestingDepth}.");
        }

        return count > MaxPipelineNodes
            ? Refusal($"The definition has more than {MaxPipelineNodes} nodes; the limit is {MaxPipelineNodes}. Split " +
                      "the pipeline into several pipelines of a data flow.")
            : null;
    }

    /// <summary>
    ///     The limit check every reader of a caller-supplied definition runs before it parses it with its own YAML
    ///     deserializer (AB#9667): the length, the shared converter's alias and depth budget, then
    ///     <see cref="CheckNodes" />. It does not depend on <c>EnablePipelineSchemaValidation</c> or on an adapter
    ///     schema, so the deploy path, the elevation guard and the REST parse endpoints are bounded in every
    ///     configuration.
    /// </summary>
    /// <returns>
    ///     The refusal finding, or null when the definition is within the limits. A definition the converter cannot
    ///     parse for another reason (malformed YAML) also returns null: whether that is an error is the caller's
    ///     decision, as before, and its own parser fails on the same syntax error.
    /// </returns>
    public static string? CheckDefinition(string? definition)
    {
        if (string.IsNullOrEmpty(definition))
        {
            return null;
        }

        if (CheckLength(definition) is { } tooLong)
        {
            return tooLong;
        }

        JsonNode? converted;
        try
        {
            converted = YamlToJsonConverter.ToJsonNodeAutoDetect(definition);
        }
        catch (YamlDocumentLimitExceededException limit)
        {
            // The converter's message names the limit and never quotes the document.
            return Refusal(limit.Message);
        }
        catch (Exception)
        {
            return null;
        }

        return CheckNodes(converted);
    }

    /// <summary>Prefixes <paramref name="message" /> with <see cref="RefusedCode" />.</summary>
    public static string Refusal(string message) => $"{RefusedCode}: {message}";

    /// <summary>
    ///     The nesting depth of <paramref name="node" /> (a scalar or an empty container at the root has depth 1), measured
    ///     iteratively. Stops once the depth exceeds <paramref name="maxDepth" />.
    /// </summary>
    internal static int MeasureJsonDepth(JsonNode? node, int maxDepth)
    {
        if (node is null)
        {
            return 0;
        }

        var deepest = 0;
        var pending = new Stack<(JsonNode Node, int Depth)>();
        pending.Push((node, 1));
        while (pending.Count > 0)
        {
            var (current, depth) = pending.Pop();
            deepest = Math.Max(deepest, depth);
            if (deepest > maxDepth)
            {
                return deepest;
            }

            var children = current switch
            {
                JsonObject obj => obj.Select(p => p.Value),
                JsonArray array => array,
                _ => []
            };
            foreach (var child in children)
            {
                if (child is not null)
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return deepest;
    }

    /// <summary>
    ///     Counts the pipeline nodes and measures the nesting depth of node lists, following the node containers of the
    ///     pipeline SDK: root <c>triggers</c>, <c>transformations</c> and <c>nodes</c>; inside a node its
    ///     <c>transformations</c>, the <c>cases[].transformations</c> and the <c>default</c> list of a Switch. Every key that
    ///     matches a container name case-insensitively is followed. Iterative; stops once either limit is exceeded.
    /// </summary>
    internal static (int Count, int Depth) MeasureNodes(JsonNode? definition, int maxCount, int maxDepth)
    {
        if (definition is not JsonObject root)
        {
            return (0, 0);
        }

        var count = 0;
        var deepest = 0;
        var pending = new Stack<(JsonNode? List, int Depth)>();
        foreach (var container in new[] { "nodes", "transformations", "triggers" })
        {
            foreach (var rootList in Properties(root, container))
            {
                pending.Push((rootList, 1));
            }
        }

        while (pending.Count > 0)
        {
            var (list, depth) = pending.Pop();
            if (list is not JsonArray array)
            {
                continue;
            }

            foreach (var node in array.OfType<JsonObject>())
            {
                count++;
                deepest = Math.Max(deepest, depth);
                if (count > maxCount || deepest > maxDepth)
                {
                    return (count, deepest);
                }

                foreach (var child in Properties(node, "transformations"))
                {
                    pending.Push((child, depth + 1));
                }

                foreach (var cases in Properties(node, "cases").OfType<JsonArray>())
                {
                    foreach (var switchCase in cases.OfType<JsonObject>())
                    {
                        foreach (var child in Properties(switchCase, "transformations"))
                        {
                            pending.Push((child, depth + 1));
                        }
                    }
                }

                foreach (var child in Properties(node, "default"))
                {
                    pending.Push((child, depth + 1));
                }
            }
        }

        return (count, deepest);
    }

    // Every property whose name matches case-insensitively: JSON and the YAML converter keep "Transformations" and
    // "transformations" side by side, and the measurement must not stop at a decoy that differs only in case.
    private static IEnumerable<JsonNode?> Properties(JsonObject obj, string name) =>
        obj.Where(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Select(p => p.Value);
}

/// <summary>
///     A caller-supplied pipeline definition is above a <see cref="PipelineDefinitionLimits" /> limit (AB#9667). The
///     message is the refusal finding: it names the limit and never quotes the definition.
/// </summary>
internal sealed class PipelineDefinitionLimitExceededException(string finding) : Exception(finding);
