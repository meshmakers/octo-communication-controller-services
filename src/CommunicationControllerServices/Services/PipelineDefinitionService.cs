using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.Contracts.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Parses YAML pipeline definitions and extracts node property values.
/// Uses dictionary-based YAML deserialization (no dependency on Sdk.Common type system).
/// </summary>
internal class PipelineDefinitionService : IPipelineDefinitionService
{
    private const string TypeKey = "type";
    private const string TransformationsKey = "transformations";
    private const string TriggersKey = "triggers";

    /// <summary>Throwaway mapping key for the plain-scalar round-trip probe.</summary>
    private const string RoundTripProbeKey = "v";

    // Read-only on purpose: UpdateNodeProperties edits the definition line by line so comments and
    // formatting survive, so nothing here ever re-serializes a parsed document. (A SerializerBuilder
    // used to be built alongside this one and was never called.)
    private readonly IDeserializer _deserializer;

    public PipelineDefinitionService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    /// <inheritdoc />
    public PipelineNodeProperties? GetNodeProperties(string pipelineDefinition, string nodeType, int nodeIndex)
    {
        var root = DeserializeDefinition(pipelineDefinition);
        if (root == null) return null;

        var matchIndex = 0;

        // Search triggers
        if (root.TryGetValue(TriggersKey, out var triggersObj) && triggersObj is List<object> triggers)
        {
            var result = FindNodeByType(triggers, nodeType, nodeIndex, ref matchIndex);
            if (result != null) return result;
        }

        // Search transformations
        if (root.TryGetValue(TransformationsKey, out var transformationsObj) &&
            transformationsObj is List<object> transformations)
        {
            var result = FindNodeByType(transformations, nodeType, nodeIndex, ref matchIndex);
            if (result != null) return result;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<PipelineNodeProperties> GetAllNodes(string pipelineDefinition)
    {
        TryGetAllNodes(pipelineDefinition, out var nodes);
        return nodes;
    }

    /// <inheritdoc />
    public bool TryGetAllNodes(string pipelineDefinition, out IReadOnlyList<PipelineNodeProperties> nodes)
    {
        Dictionary<object, object>? root;
        try
        {
            root = _deserializer.Deserialize<Dictionary<object, object>>(pipelineDefinition);
        }
        catch
        {
            // Malformed YAML — the caller decides whether that is a silent empty result
            // (GetAllNodes, historical behavior) or a reportable finding (AB#5113).
            nodes = [];
            return false;
        }

        // A null root is an empty (whitespace/comment-only) document: valid YAML, zero nodes.
        nodes = root == null ? [] : CollectAllNodes(root);
        return true;
    }

    private static IReadOnlyList<PipelineNodeProperties> CollectAllNodes(Dictionary<object, object> root)
    {
        var nodes = new List<PipelineNodeProperties>();
        var typeCounters = new Dictionary<string, int>();

        // Collect from triggers
        if (root.TryGetValue(TriggersKey, out var triggersObj) && triggersObj is List<object> triggers)
        {
            CollectAllNodes(triggers, nodes, typeCounters);
        }

        // Collect from transformations
        if (root.TryGetValue(TransformationsKey, out var transformationsObj) &&
            transformationsObj is List<object> transformations)
        {
            CollectAllNodes(transformations, nodes, typeCounters);
        }

        return nodes;
    }

    private Dictionary<object, object>? DeserializeDefinition(string pipelineDefinition)
    {
        try
        {
            return _deserializer.Deserialize<Dictionary<object, object>>(pipelineDefinition);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Recursively searches a list of nodes for the N-th occurrence of a node type.
    /// </summary>
    private static PipelineNodeProperties? FindNodeByType(
        List<object> nodes, string nodeType, int targetIndex, ref int matchIndex)
    {
        foreach (var item in nodes)
        {
            if (item is not Dictionary<object, object> node) continue;

            var type = GetNodeType(node);
            if (type == nodeType)
            {
                if (matchIndex == targetIndex)
                {
                    return CreateNodeProperties(node, nodeType, targetIndex);
                }

                matchIndex++;
            }

            // Recurse into nested transformations
            if (node.TryGetValue(TransformationsKey, out var childObj) && childObj is List<object> children)
            {
                var result = FindNodeByType(children, nodeType, targetIndex, ref matchIndex);
                if (result != null) return result;
            }
        }

        return null;
    }

    /// <summary>
    /// Recursively collects all nodes with their types and occurrence indices.
    /// </summary>
    private static void CollectAllNodes(
        List<object> nodes, List<PipelineNodeProperties> result, Dictionary<string, int> typeCounters)
    {
        foreach (var item in nodes)
        {
            if (item is not Dictionary<object, object> node) continue;

            var type = GetNodeType(node);
            if (type != null)
            {
                if (!typeCounters.TryGetValue(type, out var index))
                {
                    index = 0;
                }

                result.Add(CreateNodeProperties(node, type, index));
                typeCounters[type] = index + 1;
            }

            // Recurse into nested transformations
            if (node.TryGetValue(TransformationsKey, out var childObj) && childObj is List<object> children)
            {
                CollectAllNodes(children, result, typeCounters);
            }
        }
    }

    /// <inheritdoc />
    public string? UpdateNodeProperties(string pipelineDefinition, string nodeType, int nodeIndex,
        IDictionary<string, object?> properties)
    {
        // Line-based approach: find the target node's `- type:` line, then update
        // individual property lines in-place, preserving comments and formatting.
        var lines = pipelineDefinition.Split('\n');
        var typeLineIndex = FindNodeTypeLine(lines, nodeType, nodeIndex);
        if (typeLineIndex < 0) return null;

        // Determine the indentation of the `- type:` line
        var typeLine = lines[typeLineIndex];
        var baseIndent = typeLine.Length - typeLine.TrimStart().Length;
        // Property lines are indented 2 more than the list item marker
        var propIndent = new string(' ', baseIndent + 2);

        // Find the range of lines belonging to this node (until next node or end of block)
        var nodeEndLine = FindNodeEndLine(lines, typeLineIndex, baseIndent);

        var result = new List<string>(lines);
        var updatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Update existing property lines
        for (var i = typeLineIndex + 1; i < nodeEndLine; i++)
        {
            var line = result[i];
            var trimmed = line.TrimStart();

            // Skip comments and blank lines
            if (trimmed.StartsWith('#') || string.IsNullOrWhiteSpace(trimmed)) continue;

            // Check if this is a direct property line (same indent as propIndent, not deeper)
            var lineIndent = line.Length - trimmed.Length;
            if (lineIndent != baseIndent + 2) continue;

            // Parse the key from "key: value" or "key:"
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex <= 0) continue;

            var key = trimmed[..colonIndex].Trim();

            // Skip protected keys
            if (string.Equals(key, TypeKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, TransformationsKey, StringComparison.OrdinalIgnoreCase)) continue;

            if (properties.TryGetValue(key, out var newValue))
            {
                updatedKeys.Add(key);

                if (newValue == null)
                {
                    // Remove the property line (and any child lines like array items)
                    var removeEnd = FindPropertyEndLine(result, i, lineIndent);
                    result.RemoveRange(i, removeEnd - i);
                    nodeEndLine -= (removeEnd - i);
                    i--; // Re-check this index
                }
                else
                {
                    var unwrapped = UnwrapJsonElement(newValue);
                    // Only update simple scalar values inline
                    if (IsSimpleScalar(unwrapped))
                    {
                        result[i] = $"{propIndent}{key}: {FormatScalarValue(unwrapped)}";
                    }
                    // Complex values (arrays, objects) are left unchanged for now
                }
            }
        }

        // Add new properties that weren't in the original (insert before nodeEndLine)
        foreach (var kvp in properties)
        {
            if (updatedKeys.Contains(kvp.Key)) continue;
            if (string.Equals(kvp.Key, TypeKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(kvp.Key, TransformationsKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (kvp.Value == null) continue;

            var unwrapped = UnwrapJsonElement(kvp.Value);
            if (IsSimpleScalar(unwrapped))
            {
                result.Insert(nodeEndLine, $"{propIndent}{kvp.Key}: {FormatScalarValue(unwrapped)}");
                nodeEndLine++;
            }
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Finds the line index of the N-th `- type: {nodeType}` occurrence.
    /// </summary>
    private static int FindNodeTypeLine(string[] lines, string nodeType, int targetIndex)
    {
        var matchIndex = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("- type:") || trimmed.StartsWith("- type :"))
            {
                var colonPos = trimmed.IndexOf(':');
                var value = trimmed[(colonPos + 1)..].Trim();
                if (string.Equals(value, nodeType, StringComparison.Ordinal))
                {
                    if (matchIndex == targetIndex) return i;
                    matchIndex++;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Finds the end line of a node block (next sibling node or parent-level content).
    /// </summary>
    private static int FindNodeEndLine(string[] lines, int startLine, int baseIndent)
    {
        for (var i = startLine + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            var indent = line.Length - trimmed.Length;

            // A line at the same or lower indent level means the node block ended
            if (indent <= baseIndent) return i;
        }

        return lines.Length;
    }

    /// <summary>
    /// Finds the end of a property (including multi-line values like arrays).
    /// </summary>
    private static int FindPropertyEndLine(List<string> lines, int startLine, int propIndent)
    {
        for (var i = startLine + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#')) continue;

            var indent = line.Length - trimmed.Length;
            if (indent <= propIndent) return i;
        }

        return lines.Count;
    }

    private static bool IsSimpleScalar(object? value)
    {
        return value is null or string or bool or int or long or float or double or decimal;
    }

    private static string FormatScalarValue(object? value)
    {
        return value switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => FormatStringValue(s),
            // Invariant culture: a German-locale server would otherwise write `1,5`, which reads
            // back as the string "1,5" instead of the number the caller sent.
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

    /// <summary>
    ///     Emits a string value as a YAML scalar, quoting it unless the plain (unquoted) form reads
    ///     back as exactly the same string.
    /// </summary>
    /// <remarks>
    ///     The round-trip is checked against <see cref="YamlToJsonConverter" /> — the same reader the
    ///     deploy-time schema validation and the MCP server use — so the write path and the read path
    ///     cannot disagree about what a scalar means. The previous heuristic only looked for
    ///     <c>:</c>, <c>#</c>, quotes and surrounding spaces, which let a string that merely LOOKS
    ///     like another YAML type through unquoted: <c>"true"</c> came back as a boolean,
    ///     <c>"3"</c> as a number, <c>"null"</c> and <c>"~"</c> as null (the value simply gone), and
    ///     a value opening with a block-sequence indicator such as <c>"- x"</c> made the whole
    ///     definition unparseable.
    /// </remarks>
    private static string FormatStringValue(string value)
    {
        return PlainFormRoundTrips(value) ? value : QuoteScalar(value);
    }

    private static bool PlainFormRoundTrips(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        try
        {
            var probe = YamlToJsonConverter.ToJsonNode($"{RoundTripProbeKey}: {value}");
            return probe?[RoundTripProbeKey] is JsonValue scalar
                   && scalar.GetValueKind() == JsonValueKind.String
                   && scalar.GetValue<string>() == value;
        }
        catch (Exception)
        {
            // The plain form does not even parse (a leading `-`, an embedded `: `, …) — quote it.
            return false;
        }
    }

    /// <summary>Emits a YAML double-quoted scalar, which can carry any string content.</summary>
    private static string QuoteScalar(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>
    /// Recursively converts System.Text.Json.JsonElement values to YAML-friendly primitives.
    /// ASP.NET Core deserializes Dictionary&lt;string, object?&gt; values as JsonElement,
    /// which YamlDotNet would serialize as complex objects with valueKind metadata.
    /// </summary>
    private static object? UnwrapJsonElement(object? value)
    {
        if (value is not JsonElement element) return value;

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray()
                .Select(e => UnwrapJsonElement(e))
                .ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => (object)p.Name, p => UnwrapJsonElement(p.Value)),
            _ => element.ToString()
        };
    }

    private static string? GetNodeType(Dictionary<object, object> node)
    {
        return node.TryGetValue(TypeKey, out var typeValue) ? typeValue?.ToString() : null;
    }

    private static PipelineNodeProperties CreateNodeProperties(
        Dictionary<object, object> node, string nodeType, int nodeIndex)
    {
        var properties = new Dictionary<string, object?>();
        foreach (var kvp in node)
        {
            var key = kvp.Key.ToString()!;
            // Skip 'type' — it's the discriminator, not a user property
            if (string.Equals(key, TypeKey, StringComparison.OrdinalIgnoreCase)) continue;
            // Skip nested transformations — they are child nodes, not properties
            if (string.Equals(key, TransformationsKey, StringComparison.OrdinalIgnoreCase)) continue;

            properties[key] = kvp.Value;
        }

        return new PipelineNodeProperties
        {
            NodeType = nodeType,
            NodeIndex = nodeIndex,
            Properties = properties
        };
    }
}
