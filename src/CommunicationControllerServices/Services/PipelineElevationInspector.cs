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
/// One node that takes its target tenant from data instead of naming it statically (AB#9767): a member such as
/// <c>targetTenant.tenantIdPath</c>, or a target setting the controller cannot read as static.
/// </summary>
/// <param name="NodeType">The node's qualified type.</param>
/// <param name="NodeIndex">Zero-based occurrence index of that type.</param>
/// <param name="Identity">The node's identity label (<c>Caller</c>, <c>ServiceAccount</c>, <c>System</c> or the unrecognised value).</param>
/// <param name="PropertyName">The data-driven member, e.g. <c>targetTenant.tenantIdPath</c>.</param>
/// <param name="Value">Its value (the path), or the type name of a value that is no text.</param>
public record TargetTenantFinding(string NodeType, int NodeIndex, string Identity, string PropertyName, string Value)
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

    // Label of the non-elevated identity in findings.
    private const string CallerIdentity = "Caller";

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

    /// <summary>
    /// The target-tenant settings of the mesh adapter's cross-tenant nodes: <c>targetTenant</c> (one target) and
    /// <c>targetTenants</c> (a set). A node that carries one reaches another tenant of the pipeline's subtree.
    /// </summary>
    private static readonly string[] TargetTenantKeys = ["targetTenant", "targetTenants"];

    /// <summary>
    /// Suffix of the members of a target-tenant setting that read the target from the data context
    /// (<c>tenantIdPath</c>, and any later path variant). Matched ignoring case, fail closed.
    /// </summary>
    private const string PathMemberSuffix = "Path";

    // Field-filter item properties whose value is a caller-supplied JSONPath steering which
    // entities match (and thus which the elevated op touches). "fieldFilters" is the array key.
    private const string FieldFiltersKey = "fieldFilters";
    private static readonly HashSet<string> FieldFilterPathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "comparisonValuePath", "attributePath"
    };

    /// <summary>
    /// Returns the elevated nodes among the parsed nodes, in document order: <c>ServiceAccount</c>, <c>System</c> and
    /// every unrecognised identity value (fail closed, see <see cref="FindUnrecognisedIdentityNodes" />). Only an
    /// absent identity, <c>Caller</c> or <c>0</c> is not elevated.
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
    /// Nodes whose <c>identity</c> is none of the accepted spellings: absent, <c>Caller</c>/<c>0</c>,
    /// <c>ServiceAccount</c>/<c>1</c>, <c>System</c>/<c>2</c> (trimmed, any case). The mesh adapter binds the value
    /// with YamlDotNet's <c>Enum.Parse(ignoreCase)</c>, which also accepts <c>'01'</c>, <c>'+2'</c> and comma-joined
    /// names (<c>Caller, ServiceAccount</c> = ServiceAccount), so a spelling the controller does not know may run
    /// elevated there. Such a pipeline is refused at deploy (AB#9767); the other rules treat the node as elevated.
    /// </summary>
    public static IReadOnlyList<ElevatedNode> FindUnrecognisedIdentityNodes(IReadOnlyList<PipelineNodeProperties> nodes)
    {
        var result = new List<ElevatedNode>();
        foreach (var node in nodes)
        {
            var identity = ReadIdentity(node, out var label);
            if (identity == NodeIdentity.Unrecognised)
            {
                result.Add(new ElevatedNode(node.NodeType, node.NodeIndex, label));
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

    /// <summary>
    /// Nodes that request the <c>System</c> identity (or an unrecognised one, fail closed) <b>and</b> carry a
    /// target-tenant setting. The system context is never accepted for another tenant (no exception), so such a
    /// pipeline is refused at deploy time. The mesh adapter's cross-tenant guard refuses it again at run time; this
    /// rule only reports it early.
    /// </summary>
    public static IReadOnlyList<ElevatedNode> FindSystemCrossTenantNodes(IReadOnlyList<PipelineNodeProperties> nodes)
    {
        var result = new List<ElevatedNode>();
        foreach (var node in nodes)
        {
            var identity = ReadIdentity(node, out var label);
            if ((identity is NodeIdentity.System or NodeIdentity.Unrecognised) && HasTargetTenantSetting(node))
            {
                result.Add(new ElevatedNode(node.NodeType, node.NodeIndex, label));
            }
        }

        return result;
    }

    /// <summary>
    /// Nodes that take their target tenant from data instead of naming it statically, split by identity (AB#9767):
    /// with <paramref name="elevated" /> the nodes that run as <c>ServiceAccount</c>, <c>System</c> or an unrecognised
    /// identity, which are refused at deploy and on every push (an elevated node may name its target tenant only
    /// statically, <c>tenantId</c>/<c>tenantIds</c>); without it the <c>Caller</c> nodes (also absent identity), which
    /// deploy with a warning — the mesh adapter refuses a data-driven target at run time unless the caller is verified.
    /// No path is classified: every data-driven target counts, whatever it reads.
    /// </summary>
    /// <param name="nodes">All nodes of the definition, nested ones included.</param>
    /// <param name="elevated">Whether to return the elevated nodes (refused) or the caller nodes (warned).</param>
    public static IReadOnlyList<TargetTenantFinding> FindDataDrivenTargetTenants(
        IReadOnlyList<PipelineNodeProperties> nodes, bool elevated)
    {
        var findings = new List<TargetTenantFinding>();
        foreach (var node in nodes)
        {
            var isElevated = ReadIdentity(node, out var label) != NodeIdentity.Caller;
            if (isElevated != elevated)
            {
                continue;
            }

            foreach (var kvp in node.Properties)
            {
                if (TargetTenantKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
                {
                    CollectDataDrivenTargets(node, label, kvp.Key, kvp.Value, findings);
                }
            }
        }

        return findings;
    }

    /// <summary>
    /// Whether a node carries a target-tenant setting. Checked by key presence: an empty or malformed setting still
    /// declares the intent to cross a tenant, and the deploy rule fails closed on it.
    /// </summary>
    private static bool HasTargetTenantSetting(PipelineNodeProperties node) =>
        node.Properties.Keys.Any(key => TargetTenantKeys.Contains(key, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Collects the data-driven parts of one target-tenant setting, fail closed: a mapping member whose name ends in
    /// <c>Path</c>, and every value that is no mapping (a scalar, or a list entry that is no mapping), because the
    /// controller cannot show that such a value names the tenant statically.
    /// </summary>
    private static void CollectDataDrivenTargets(PipelineNodeProperties node, string identity, string label,
        object? value, List<TargetTenantFinding> findings)
    {
        switch (value)
        {
            case null:
                return;
            case Dictionary<object, object> setting:
                foreach (var member in setting)
                {
                    var key = member.Key.ToString();
                    if (key != null && member.Value != null &&
                        key.Trim().EndsWith(PathMemberSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new TargetTenantFinding(node.NodeType, node.NodeIndex, identity, $"{label}.{key}",
                            Describe(member.Value)));
                    }
                }

                return;
            case List<object> entries:
                for (var i = 0; i < entries.Count; i++)
                {
                    CollectDataDrivenTargets(node, identity, $"{label}[{i}]", entries[i], findings);
                }

                return;
            default:
                findings.Add(new TargetTenantFinding(node.NodeType, node.NodeIndex, identity, label, Describe(value)));
                return;
        }
    }

    private static string Describe(object value) => value as string ?? value.GetType().Name;

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
    /// The elevated identity label of a node (<c>ServiceAccount</c>, <c>System</c> or the unrecognised value), or
    /// <c>null</c> when it runs as the caller.
    /// </summary>
    private static string? ReadElevatedIdentity(PipelineNodeProperties node) =>
        ReadIdentity(node, out var label) == NodeIdentity.Caller ? null : label;

    /// <summary>
    /// Reads a node's <c>identity</c>, fail closed (AB#9767). Absent, <c>null</c>, <c>Caller</c> and <c>0</c> are the
    /// caller; <c>ServiceAccount</c>/<c>1</c> and <c>System</c>/<c>2</c> are elevated; names match trimmed and ignoring
    /// case. Every other value is <see cref="NodeIdentity.Unrecognised" />: the adapter's binder accepts spellings
    /// such as <c>'01'</c>, <c>'+2'</c> or <c>Caller, ServiceAccount</c> and may run them elevated.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <param name="label">The label used in findings: the identity name, or the unrecognised value.</param>
    private static NodeIdentity ReadIdentity(PipelineNodeProperties node, out string label)
    {
        // Keys that differ only in case are all read and the most privileged wins: which one a binder takes is not
        // the controller's to guess.
        var identity = NodeIdentity.Caller;
        label = CallerIdentity;
        foreach (var property in node.Properties.Where(p =>
                     string.Equals(p.Key, IdentityKey, StringComparison.OrdinalIgnoreCase)))
        {
            var candidate = ReadIdentityValue(property.Value, out var candidateLabel);
            if (candidate > identity)
            {
                identity = candidate;
                label = candidateLabel;
            }
        }

        return identity;
    }

    private static NodeIdentity ReadIdentityValue(object? raw, out string label)
    {
        if (raw == null)
        {
            label = CallerIdentity;
            return NodeIdentity.Caller;
        }

        var value = raw is string s ? s.Trim() : null;
        if (string.Equals(value, CallerIdentity, StringComparison.OrdinalIgnoreCase) || value == "0")
        {
            label = CallerIdentity;
            return NodeIdentity.Caller;
        }

        if (string.Equals(value, ServiceAccountIdentity, StringComparison.OrdinalIgnoreCase) || value == "1")
        {
            label = ServiceAccountIdentity;
            return NodeIdentity.ServiceAccount;
        }

        if (string.Equals(value, SystemIdentity, StringComparison.OrdinalIgnoreCase) || value == "2")
        {
            label = SystemIdentity;
            return NodeIdentity.System;
        }

        // A list or mapping is no identity either; it never names a value, so the label says what it is.
        label = $"unrecognised identity '{value ?? raw.GetType().Name}'";
        return NodeIdentity.Unrecognised;
    }

    private enum NodeIdentity
    {
        Caller,
        ServiceAccount,
        System,
        Unrecognised
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
