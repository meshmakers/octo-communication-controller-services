using System.Text;
using System.Text.Json;
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

    private readonly IDeserializer _deserializer;
    private readonly ISerializer _serializer;

    public PipelineDefinitionService()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
        _serializer = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
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
        var root = DeserializeDefinition(pipelineDefinition);
        if (root == null) return [];

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
            string s => s.Contains(':') || s.Contains('#') || s.Contains('"') || s.Contains('\'')
                         || s.StartsWith(' ') || s.EndsWith(' ')
                ? $"\"{s.Replace("\"", "\\\"")}\""
                : s,
            _ => value.ToString() ?? ""
        };
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
