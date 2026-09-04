namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// One data node that requests an elevated execution identity (AB#5127 / AB#5128, Epic AB#4979).
/// </summary>
/// <param name="NodeType">The node's qualified type, e.g. <c>GetRtEntitiesById@1</c>.</param>
/// <param name="NodeIndex">Zero-based occurrence index of that type in the definition.</param>
/// <param name="Identity">The elevated identity value read from the node config (<c>ServiceAccount</c> or <c>System</c>).</param>
public record ElevatedNode(string NodeType, int NodeIndex, string Identity)
{
    /// <summary>Human-readable label used in refusal / warning messages, e.g. <c>GetRtEntitiesById@1[0] (System)</c>.</summary>
    public string Label => $"{NodeType}[{NodeIndex}] ({Identity})";
}

/// <summary>
/// One confused-deputy hazard (AB#5128 part 2): an elevated node whose <b>target-selecting</b>
/// input reads a raw, caller-controlled path. The caller may legitimately TRIGGER the elevated
/// operation but must never get to STEER what it acts on.
/// </summary>
/// <param name="NodeType">The elevated node's qualified type.</param>
/// <param name="NodeIndex">Zero-based occurrence index of that type.</param>
/// <param name="Identity">The elevated identity value.</param>
/// <param name="PropertyName">The target-selecting property that carries the caller-controlled path.</param>
/// <param name="CallerControlledPath">The offending JSONPath value (e.g. <c>$.body.rtId</c>).</param>
public record ConfusedDeputyFinding(string NodeType, int NodeIndex, string Identity, string PropertyName,
    string CallerControlledPath)
{
    /// <summary>Human-readable label of the node the finding belongs to.</summary>
    public string NodeLabel => $"{NodeType}[{NodeIndex}] ({Identity})";
}

/// <summary>
/// Pure, dependency-free analysis of parsed pipeline nodes for the AB#5128 deploy-time elevation
/// gate and the confused-deputy lint. Deliberately keeps the AB#5113 parser's philosophy of not
/// depending on the Sdk.Common type system: it reads the node's <c>identity</c> property as a
/// plain string (the enum member name YamlDotNet deserialises, or its numeric value) rather than
/// binding to <c>NodeExecutionIdentity</c>.
/// </summary>
internal static class PipelineElevationInspector
{
    /// <summary>The node config property that carries the execution identity (camelCase, as parsed).</summary>
    private const string IdentityKey = "identity";

    // The two elevated identity values (AB#5127). Matched case-insensitively against both the enum
    // member name and its numeric value, so a definition authored either way is caught.
    private const string ServiceAccountIdentity = "ServiceAccount";
    private const string SystemIdentity = "System";

    /// <summary>
    /// The caller-controlled DataContext roots the <c>FromHttpRequest@2</c> trigger writes
    /// (<c>HttpRequestService</c>): the request body, query string, uploaded files and headers.
    /// A target-selecting path rooted here is fully attacker-steerable. The verified
    /// <c>$.principal</c> subset is deliberately NOT listed — it is derived from the validated
    /// token, not free caller input. Inside <c>ForEach</c> bodies these roots appear under
    /// <c>$.full.</c>, which the matcher tolerates.
    /// </summary>
    private static readonly string[] CallerControlledRoots = ["body", "query", "files", "headers"];

    /// <summary>
    /// Target-selecting ("which entity / where") properties across the 15 caller-scoped data-node
    /// configs (AB#5127). These steer the entity or set an elevated node reads or writes — the
    /// confused-deputy surface. Deliberately excludes result/output destinations
    /// (<c>rtIdTargetPath</c>, <c>ckTypeIdTargetPath</c>, <c>filteredOutputPath</c>,
    /// <c>outputPathAll</c>, …), which write into the DataContext rather than steer the operation.
    /// </summary>
    private static readonly HashSet<string> TargetSelectingProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Entity / type selectors
        "rtId", "rtIds", "rtIdPath", "rtIdsPath",
        "ckTypeId", "ckTypeIdPath",
        "wellKnownName", "wellKnownNamePath",
        "originRtIdPath", "originCkTypeIdPath", "targetCkTypeIdPath", "associationRoleIdPath",
        // Write payloads (define both which entity and what changes)
        "entityUpdatesPath", "associationUpdatesPath",
        "inputPath", "candidateAssociationsInputPath"
    };

    // Field-filter item properties whose value is a caller-supplied JSONPath steering which
    // entities match (and thus which the elevated op touches). "fieldFilters" is the array key.
    private const string FieldFiltersKey = "fieldFilters";
    private static readonly HashSet<string> FieldFilterPathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "comparisonValuePath", "attributePath"
    };

    /// <summary>
    /// Returns the elevated nodes (Identity=ServiceAccount|System) among the parsed nodes, in
    /// document order. <c>Caller</c>, absent or unrecognised identities are not elevated.
    /// </summary>
    public static IReadOnlyList<ElevatedNode> FindElevatedNodes(IReadOnlyList<PipelineNodeProperties> nodes)
    {
        var result = new List<ElevatedNode>();
        foreach (var node in nodes)
        {
            var identity = ReadElevatedIdentity(node);
            if (identity != null)
            {
                result.Add(new ElevatedNode(node.NodeType, node.NodeIndex, identity));
            }
        }

        return result;
    }

    /// <summary>
    /// Scans every elevated node for target-selecting properties whose value is a raw path into a
    /// caller-controlled root, and returns one finding per such property (AB#5128 part 2).
    /// Non-elevated nodes are never a confused-deputy hazard and are skipped.
    /// </summary>
    public static IReadOnlyList<ConfusedDeputyFinding> FindConfusedDeputyHazards(
        IReadOnlyList<PipelineNodeProperties> nodes)
    {
        var findings = new List<ConfusedDeputyFinding>();
        foreach (var node in nodes)
        {
            var identity = ReadElevatedIdentity(node);
            if (identity == null)
            {
                continue;
            }

            foreach (var kvp in node.Properties)
            {
                if (TargetSelectingProperties.Contains(kvp.Key) &&
                    TryGetCallerControlledPath(kvp.Value, out var path))
                {
                    findings.Add(new ConfusedDeputyFinding(node.NodeType, node.NodeIndex, identity, kvp.Key, path!));
                }
            }

            // Field filters are a nested array of objects; inspect their path-valued members.
            if (node.Properties.TryGetValue(FieldFiltersKey, out var filtersValue))
            {
                CollectFieldFilterHazards(node, identity, filtersValue, findings);
            }
        }

        return findings;
    }

    private static void CollectFieldFilterHazards(PipelineNodeProperties node, string identity, object? filtersValue,
        List<ConfusedDeputyFinding> findings)
    {
        if (filtersValue is not List<object> filters)
        {
            return;
        }

        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] is not Dictionary<object, object> filter)
            {
                continue;
            }

            foreach (var member in filter)
            {
                var key = member.Key.ToString();
                if (key != null && FieldFilterPathProperties.Contains(key) &&
                    TryGetCallerControlledPath(member.Value, out var path))
                {
                    findings.Add(new ConfusedDeputyFinding(node.NodeType, node.NodeIndex, identity,
                        $"{FieldFiltersKey}[{i}].{key}", path!));
                }
            }
        }
    }

    /// <summary>
    /// The elevated identity string of a node, or <c>null</c> when it runs as <c>Caller</c> (the
    /// default), has no identity property, or names an unrecognised value.
    /// </summary>
    private static string? ReadElevatedIdentity(PipelineNodeProperties node)
    {
        if (!node.Properties.TryGetValue(IdentityKey, out var raw) || raw == null)
        {
            return null;
        }

        var value = raw.ToString()?.Trim();
        if (string.Equals(value, ServiceAccountIdentity, StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            return ServiceAccountIdentity;
        }

        if (string.Equals(value, SystemIdentity, StringComparison.OrdinalIgnoreCase) || value == "2")
        {
            return SystemIdentity;
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a JSONPath rooted in a caller-controlled root. Accepts
    /// an optional leading <c>full.</c> segment (the ForEach-body view of the trigger data), and
    /// both dotted (<c>$.body.x</c>) and bracketed (<c>$['body']</c>) first segments.
    /// </summary>
    private static bool TryGetCallerControlledPath(object? value, out string? path)
    {
        path = null;
        if (value is not string s)
        {
            return false;
        }

        var trimmed = s.Trim();
        if (!trimmed.StartsWith("$", StringComparison.Ordinal))
        {
            return false;
        }

        var root = FirstSegment(trimmed);
        if (root == null)
        {
            return false;
        }

        // Tolerate the ForEach-body prefix: $.full.body -> body.
        if (string.Equals(root, "full", StringComparison.OrdinalIgnoreCase))
        {
            var afterFull = trimmed[(trimmed.IndexOf("full", StringComparison.OrdinalIgnoreCase) + "full".Length)..];
            root = FirstSegment("$" + afterFull);
        }

        if (root != null && CallerControlledRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            path = trimmed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The first property segment of a JSONPath after the <c>$</c> root — <c>$.body.x</c> and
    /// <c>$['body'].x</c> both yield <c>body</c>; returns <c>null</c> when there is none.
    /// </summary>
    private static string? FirstSegment(string jsonPath)
    {
        var i = 1; // skip '$'
        while (i < jsonPath.Length && (jsonPath[i] == '.' || jsonPath[i] == '['))
        {
            i++;
        }

        // Bracketed form: skip optional quote.
        if (i < jsonPath.Length && (jsonPath[i] == '\'' || jsonPath[i] == '"'))
        {
            i++;
        }

        var start = i;
        while (i < jsonPath.Length && (char.IsLetterOrDigit(jsonPath[i]) || jsonPath[i] == '_'))
        {
            i++;
        }

        return i > start ? jsonPath[start..i] : null;
    }
}
