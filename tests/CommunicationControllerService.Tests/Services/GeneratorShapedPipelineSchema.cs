using System.Text;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     Builds synthetic pipeline schemas and drafts in the shape the pipeline SDK's schema generator emits: the root
///     lists <c>triggers</c> and <c>transformations</c> as arrays of <c>$ref</c> to <c>$defs/TriggerNode</c> and
///     <c>$defs/TransformationNode</c>, each a <c>oneOf</c> over every node schema; every node schema is an object with a
///     <c>type</c> property carrying a <c>const</c> discriminator (<c>Name@Version</c>), and every child-capable node
///     has a <c>transformations</c> array that refers back to <c>$defs/TransformationNode</c>.
/// </summary>
internal static class GeneratorShapedPipelineSchema
{
    /// <summary>Child-capable control node used for the nested drafts.</summary>
    public const string IfNode = "If@1";

    /// <summary>A plain transformation node with one required string property.</summary>
    public const string SetValueNode = "SetPrimitiveValue@1";

    /// <summary>The trigger node of the drafts.</summary>
    public const string TriggerNode = "FromHttpRequest@2";

    /// <summary>
    ///     Builds the composite schema with <paramref name="plainNodes" /> plain transformation nodes and
    ///     <paramref name="childCapableNodes" /> child-capable ones (If@1 is always one of them).
    /// </summary>
    public static string Build(int plainNodes = 60, int childCapableNodes = 10)
    {
        var transformationOneOf = new JsonArray
        {
            Node(IfNode, true, new JsonObject { ["condition"] = new JsonObject { ["type"] = "string" } }),
            Node(SetValueNode, false,
                new JsonObject { ["valueText"] = new JsonObject { ["type"] = "string" } }, "valueText"),
        };
        for (var i = 1; i < childCapableNodes; i++)
        {
            transformationOneOf.Add(Node($"Control{i}@1", true,
                new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }));
        }

        for (var i = 1; i < plainNodes; i++)
        {
            transformationOneOf.Add(Node($"Plain{i}@1", false, new JsonObject
            {
                ["targetPath"] = new JsonObject { ["type"] = "string" },
                ["documentMode"] = new JsonObject { ["enum"] = new JsonArray("Ignore", "Replace", "Merge") },
                ["options"] = new JsonObject { ["$ref"] = $"#/$defs/Plain{i}_1_Options" },
            }));
        }

        var defs = new JsonObject
        {
            ["TriggerNode"] = new JsonObject
            {
                ["oneOf"] = new JsonArray(Node(TriggerNode, false,
                    new JsonObject { ["path"] = new JsonObject { ["type"] = "string" } }))
            },
            ["TransformationNode"] = new JsonObject { ["oneOf"] = transformationOneOf },
        };
        for (var i = 1; i < plainNodes; i++)
        {
            defs[$"Plain{i}_1_Options"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject { ["retries"] = new JsonObject { ["type"] = "integer" } },
            };
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["triggers"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/TriggerNode" }
                },
                ["transformations"] = new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/TransformationNode" }
                },
            },
            ["$defs"] = defs,
        }.ToJsonString();
    }

    /// <summary>
    ///     A YAML draft whose transformations nest If@1 nodes <paramref name="depth" /> levels deep, with
    ///     <paramref name="siblings" /> plain nodes next to every If@1. <paramref name="breakInnermost" /> drops the
    ///     required property of the innermost node so the draft is invalid at the deepest level.
    /// </summary>
    public static string NestedIfDraft(int depth, int siblings = 4, bool breakInnermost = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine("triggers:");
        sb.AppendLine($"  - type: {TriggerNode}");
        sb.AppendLine("    path: /visits");
        sb.AppendLine("transformations:");
        AppendLevel(sb, 1, depth, siblings, breakInnermost);
        return sb.ToString();
    }

    private static void AppendLevel(StringBuilder sb, int level, int depth, int siblings, bool breakInnermost)
    {
        var indent = new string(' ', 2 + (level - 1) * 4);
        for (var s = 0; s < siblings; s++)
        {
            sb.AppendLine($"{indent}- type: {SetValueNode}");
            sb.AppendLine(breakInnermost && level == depth && s == siblings - 1
                ? $"{indent}  targetPath: $.x{s}"
                : $"{indent}  valueText: v{level}-{s}");
        }

        sb.AppendLine($"{indent}- type: {IfNode}");
        sb.AppendLine($"{indent}  condition: $.level{level}");
        sb.AppendLine($"{indent}  transformations:");
        if (level < depth)
        {
            AppendLevel(sb, level + 1, depth, siblings, breakInnermost);
        }
        else
        {
            sb.AppendLine($"{indent}    - type: {SetValueNode}");
            sb.AppendLine($"{indent}      valueText: leaf");
        }
    }

    private static JsonObject Node(string qualifiedName, bool childCapable, JsonObject properties,
        params string[] required)
    {
        properties["type"] = new JsonObject { ["type"] = "string", ["const"] = qualifiedName };
        properties["description"] = new JsonObject { ["type"] = "string" };
        if (childCapable)
        {
            properties["transformations"] = new JsonObject
            {
                ["type"] = "array", ["items"] = new JsonObject { ["$ref"] = "#/$defs/TransformationNode" }
            };
        }

        var requiredArray = new JsonArray();
        foreach (var r in required)
        {
            requiredArray.Add(r);
        }

        requiredArray.Add("type");
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = requiredArray,
            ["additionalProperties"] = false,
        };
    }
}
