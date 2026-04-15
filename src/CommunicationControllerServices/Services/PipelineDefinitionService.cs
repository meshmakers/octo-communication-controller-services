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
        var root = DeserializeDefinition(pipelineDefinition);
        if (root == null) return null;

        var matchIndex = 0;
        Dictionary<object, object>? targetNode = null;

        // Find the target node
        if (root.TryGetValue(TriggersKey, out var triggersObj) && triggersObj is List<object> triggers)
        {
            targetNode = FindNodeDict(triggers, nodeType, nodeIndex, ref matchIndex);
        }

        if (targetNode == null && root.TryGetValue(TransformationsKey, out var transformationsObj) &&
            transformationsObj is List<object> transformations)
        {
            targetNode = FindNodeDict(transformations, nodeType, nodeIndex, ref matchIndex);
        }

        if (targetNode == null) return null;

        // Update properties on the found node
        foreach (var kvp in properties)
        {
            var key = kvp.Key;
            // Don't allow overwriting the type discriminator or child transformations
            if (string.Equals(key, TypeKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(key, TransformationsKey, StringComparison.OrdinalIgnoreCase)) continue;

            if (kvp.Value == null)
            {
                targetNode.Remove(key);
            }
            else
            {
                // Convert JsonElement to primitive types for YAML serialization
                targetNode[key] = UnwrapJsonElement(kvp.Value);
            }
        }

        // Serialize back to YAML
        return _serializer.Serialize(root);
    }

    /// <summary>
    /// Recursively searches for a node dictionary by type and index (without creating PipelineNodeProperties).
    /// </summary>
    private static Dictionary<object, object>? FindNodeDict(
        List<object> nodes, string nodeType, int targetIndex, ref int matchIndex)
    {
        foreach (var item in nodes)
        {
            if (item is not Dictionary<object, object> node) continue;

            var type = GetNodeType(node);
            if (type == nodeType)
            {
                if (matchIndex == targetIndex) return node;
                matchIndex++;
            }

            // Recurse into nested transformations
            if (node.TryGetValue(TransformationsKey, out var childObj) && childObj is List<object> children)
            {
                var result = FindNodeDict(children, nodeType, targetIndex, ref matchIndex);
                if (result != null) return result;
            }
        }

        return null;
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
