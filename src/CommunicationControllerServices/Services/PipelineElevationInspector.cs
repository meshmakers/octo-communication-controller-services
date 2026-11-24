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

    // Labels of the non-elevated identity in cross-tenant findings (decision 2 of AB#8673).
    private const string CallerIdentity = "Caller";
    private const string CallerWithServiceAccountFallback = "Caller, service-account fallback";

    // The only trigger that admits a verified caller only (unless allowAnonymous is set).
    private const string VerifiedCallerHttpTrigger = "FromHttpRequest@2";
    private const string AllowAnonymousKey = "allowAnonymous";

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
    /// Members of a target-tenant setting that select the target from the data context. A caller-controlled path
    /// there lets the caller steer which tenant an elevated node acts in.
    /// </summary>
    private static readonly HashSet<string> TargetTenantPathProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "tenantIdPath", "tenantIdsPath"
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

            // A data-driven target tenant is target-selecting too: it decides which tenant the node acts in.
            CollectTargetTenantHazards(node, identity, findings);
        }

        return findings;
    }

    /// <summary>
    /// Nodes that request the <c>System</c> identity <b>and</b> carry a target-tenant setting. The system context is
    /// never accepted for another tenant (no exception), so such a pipeline is refused at deploy time. The mesh
    /// adapter's cross-tenant guard refuses it again at run time; this rule only reports it early.
    /// </summary>
    public static IReadOnlyList<ElevatedNode> FindSystemCrossTenantNodes(IReadOnlyList<PipelineNodeProperties> nodes)
    {
        var result = new List<ElevatedNode>();
        foreach (var node in nodes)
        {
            if (ReadElevatedIdentity(node) == SystemIdentity && HasTargetTenantSetting(node))
            {
                result.Add(new ElevatedNode(node.NodeType, node.NodeIndex, SystemIdentity));
            }
        }

        return result;
    }

    /// <summary>
    /// Nodes whose target tenant comes from a caller-controlled path (<c>targetTenant(s).tenantIdPath</c> into
    /// <c>$.body</c>/<c>$.query</c>/<c>$.files</c>/<c>$.headers</c>) and that would act in that tenant with an identity
    /// other than the caller's own: <c>identity: ServiceAccount</c>, or <c>Caller</c> (also absent) in a pipeline that can
    /// run without a verified caller and therefore falls back to the pipeline's service account. Such a pipeline is
    /// refused at deploy time (decision 2 of AB#8673): the caller would choose the tenant a service account acts in.
    /// <c>System</c> nodes are left to <see cref="FindSystemCrossTenantNodes" />, which refuses every target setting.
    /// </summary>
    /// <param name="nodes">All nodes of the definition (triggers and transformations).</param>
    /// <param name="triggers">The nodes of the definition's <c>triggers:</c> section only.</param>
    public static IReadOnlyList<ConfusedDeputyFinding> FindRefusedCallerSteeredTargetTenants(
        IReadOnlyList<PipelineNodeProperties> nodes, IReadOnlyList<PipelineNodeProperties> triggers) =>
        FindCallerSteeredTargetTenants(nodes, triggers, refused: true);

    /// <summary>
    /// Nodes that run as the pure caller (<c>identity: Caller</c> or absent, in a pipeline whose every trigger requires
    /// a verified caller) and take their target tenant from a caller-controlled path. Allowed, but reported as a
    /// deploy warning: the caller steers the tenant, and the mesh adapter's guard proves the caller's own right there
    /// (token exchange) before anything is read or written, so the caller gains no right it does not hold.
    /// </summary>
    /// <param name="nodes">All nodes of the definition (triggers and transformations).</param>
    /// <param name="triggers">The nodes of the definition's <c>triggers:</c> section only.</param>
    public static IReadOnlyList<ConfusedDeputyFinding> FindCallerSteeredTargetTenantWarnings(
        IReadOnlyList<PipelineNodeProperties> nodes, IReadOnlyList<PipelineNodeProperties> triggers) =>
        FindCallerSteeredTargetTenants(nodes, triggers, refused: false);

    private static List<ConfusedDeputyFinding> FindCallerSteeredTargetTenants(
        IReadOnlyList<PipelineNodeProperties> nodes, IReadOnlyList<PipelineNodeProperties> triggers, bool refused)
    {
        var callerAlwaysVerified = EveryTriggerRequiresVerifiedCaller(triggers);
        var findings = new List<ConfusedDeputyFinding>();
        foreach (var node in nodes)
        {
            var elevated = ReadElevatedIdentity(node);
            if (elevated == SystemIdentity)
            {
                continue;
            }

            string identity;
            bool isRefused;
            if (elevated == ServiceAccountIdentity)
            {
                identity = ServiceAccountIdentity;
                isRefused = true;
            }
            else if (callerAlwaysVerified)
            {
                identity = CallerIdentity;
                isRefused = false;
            }
            else
            {
                identity = CallerWithServiceAccountFallback;
                isRefused = true;
            }

            if (isRefused == refused)
            {
                CollectTargetTenantHazards(node, identity, findings);
            }
        }

        return findings;
    }

    /// <summary>
    /// Whether every trigger of the pipeline admits only a verified caller, so that a <c>Caller</c> node never falls
    /// back to the pipeline's service account. True only when there is at least one trigger and every trigger is
    /// <c>FromHttpRequest@2</c> without <c>allowAnonymous: true</c>. Every other trigger fails closed: schedules,
    /// watches and events run without a caller, <c>FromHttpRequest@1</c> and <c>FromTeamsBot@1</c> publish anonymous
    /// routes, and an unreadable <c>allowAnonymous</c> value counts as anonymous.
    /// </summary>
    private static bool EveryTriggerRequiresVerifiedCaller(IReadOnlyList<PipelineNodeProperties> triggers)
    {
        if (triggers.Count == 0)
        {
            return false;
        }

        foreach (var trigger in triggers)
        {
            if (!string.Equals(trigger.NodeType.Trim(), VerifiedCallerHttpTrigger, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var anonymous = trigger.Properties
                .FirstOrDefault(p => string.Equals(p.Key, AllowAnonymousKey, StringComparison.OrdinalIgnoreCase));
            if (anonymous.Key == null || anonymous.Value == null)
            {
                continue;
            }

            var isFalse = anonymous.Value switch
            {
                bool b => !b,
                string s => string.Equals(s.Trim(), "false", StringComparison.OrdinalIgnoreCase),
                _ => false
            };
            if (!isFalse)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a node carries a target-tenant setting. Checked by key presence: an empty or malformed setting still
    /// declares the intent to cross a tenant, and the deploy rule fails closed on it.
    /// </summary>
    private static bool HasTargetTenantSetting(PipelineNodeProperties node) =>
        node.Properties.Keys.Any(key => TargetTenantKeys.Contains(key, StringComparer.OrdinalIgnoreCase));

    private static void CollectTargetTenantHazards(PipelineNodeProperties node, string identity,
        List<ConfusedDeputyFinding> findings)
    {
        foreach (var kvp in node.Properties)
        {
            if (!TargetTenantKeys.Contains(kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (kvp.Value)
            {
                case Dictionary<object, object> setting:
                    CollectTargetTenantPathHazards(node, identity, kvp.Key, setting, findings);
                    break;
                // A set of targets written as a list of target settings: every entry is inspected.
                case List<object> settings:
                    for (var i = 0; i < settings.Count; i++)
                    {
                        if (settings[i] is Dictionary<object, object> entry)
                        {
                            CollectTargetTenantPathHazards(node, identity, $"{kvp.Key}[{i}]", entry, findings);
                        }
                    }

                    break;
            }
        }
    }

    private static void CollectTargetTenantPathHazards(PipelineNodeProperties node, string identity, string label,
        Dictionary<object, object> setting, List<ConfusedDeputyFinding> findings)
    {
        foreach (var member in setting)
        {
            var key = member.Key.ToString();
            if (key != null && TargetTenantPathProperties.Contains(key) &&
                TryGetCallerControlledPath(member.Value, out var path))
            {
                findings.Add(new ConfusedDeputyFinding(node.NodeType, node.NodeIndex, identity, $"{label}.{key}",
                    path!));
            }
        }
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
