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
                    TryGetCallerControlledPath(kvp.Value, CallerInputScope.RequestRootsOnly, out var path))
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
            CollectTargetTenantHazards(node, identity, CallerInputScope.RequestRootsOnly, findings);
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
    /// Nodes whose target tenant comes from a caller-controlled path (<c>targetTenant(s).tenantIdPath</c>, see
    /// <see cref="CallerInputScope" />: the request roots <c>$.body</c>/<c>$.query</c>/<c>$.files</c>/<c>$.headers</c>,
    /// every multi-match path, and what a trigger other than <c>FromHttpRequest@2</c> writes) and that would act in that
    /// tenant with an identity other than the caller's own: <c>identity: ServiceAccount</c> (or an unrecognised value),
    /// or <c>Caller</c> (also absent) in a pipeline that can run without a verified caller and therefore falls back to
    /// the pipeline's service account. Such a pipeline is refused at deploy time (decision 2 of AB#8673): the caller
    /// would choose the tenant a service account acts in. <c>System</c> nodes are left to
    /// <see cref="FindSystemCrossTenantNodes" />, which refuses every target setting.
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
        var scope = CallerInputScope.ForTriggers(triggers);
        var findings = new List<ConfusedDeputyFinding>();
        foreach (var node in nodes)
        {
            var nodeIdentity = ReadIdentity(node, out var label);
            if (nodeIdentity == NodeIdentity.System)
            {
                continue;
            }

            string identity;
            bool isRefused;
            if (nodeIdentity is NodeIdentity.ServiceAccount or NodeIdentity.Unrecognised)
            {
                // An unrecognised value may bind to ServiceAccount (or System, refused above it) in the adapter.
                identity = label;
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
                CollectTargetTenantHazards(node, identity, scope, findings);
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
        CallerInputScope scope, List<ConfusedDeputyFinding> findings)
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
                    CollectTargetTenantPathHazards(node, identity, kvp.Key, setting, scope, findings);
                    break;
                // A set of targets written as a list of target settings: every entry is inspected.
                case List<object> settings:
                    for (var i = 0; i < settings.Count; i++)
                    {
                        if (settings[i] is Dictionary<object, object> entry)
                        {
                            CollectTargetTenantPathHazards(node, identity, $"{kvp.Key}[{i}]", entry, scope,
                                findings);
                        }
                    }

                    break;
            }
        }
    }

    private static void CollectTargetTenantPathHazards(PipelineNodeProperties node, string identity, string label,
        Dictionary<object, object> setting, CallerInputScope scope, List<ConfusedDeputyFinding> findings)
    {
        foreach (var member in setting)
        {
            var key = member.Key.ToString();
            if (key != null && TargetTenantPathProperties.Contains(key) &&
                TryGetCallerControlledPath(member.Value, scope, out var path))
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
                    TryGetCallerControlledPath(member.Value, CallerInputScope.RequestRootsOnly, out var path))
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
    /// Whether <paramref name="value"/> is a JSONPath the caller controls within <paramref name="scope" />: a path
    /// that can match more than one location (fail closed, it may reach a caller root), or one whose first property
    /// segment is a caller-controlled root of the scope. Accepts an optional leading <c>full.</c> segment (the
    /// ForEach-body view of the trigger data), and both dotted (<c>$.body.x</c>) and bracketed (<c>$['body']</c>)
    /// first segments.
    /// </summary>
    private static bool TryGetCallerControlledPath(object? value, CallerInputScope scope, out string? path)
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

        if (IsCallerControlled(trimmed, scope))
        {
            path = trimmed;
            return true;
        }

        return false;
    }

    private static bool IsCallerControlled(string jsonPath, CallerInputScope scope)
    {
        if (scope.AllPaths || IsMultiMatch(jsonPath))
        {
            return true;
        }

        var root = RootSegment(jsonPath, out var rest);
        // Tolerate the ForEach-body prefix: $.full.body -> body.
        if (root != null && string.Equals(root, "full", StringComparison.OrdinalIgnoreCase))
        {
            root = RootSegment("$" + rest, out _);
        }

        return root == null ? scope.UnreadableRootIsCallerControlled : scope.Roots.Contains(root);
    }

    /// <summary>
    /// Whether a JSONPath can select more than one location: recursive descent (<c>..</c>), wildcards (<c>*</c>),
    /// filters and script expressions (<c>?</c>, <c>@</c>, <c>(</c>), unions (<c>,</c>) and slices (<c>:</c>). Such a
    /// path reaches every root, the caller's included; a quoted name that contains one of these characters is counted
    /// too, fail closed.
    /// </summary>
    private static bool IsMultiMatch(string jsonPath) =>
        jsonPath.Contains("..", StringComparison.Ordinal) || jsonPath.IndexOfAny(['*', '?', '@', '(', ',', ':']) >= 0;

    /// <summary>
    /// The first property name of a single-match JSONPath after the <c>$</c> root — <c>$.body.x</c> and
    /// <c>$['body'].x</c> both yield <c>body</c> with <paramref name="rest" /> <c>.x</c>. Returns <c>null</c> when the
    /// path does not start with a property name: <c>$</c> alone, an index (<c>$[0]</c>) or anything else.
    /// </summary>
    private static string? RootSegment(string jsonPath, out string rest)
    {
        rest = string.Empty;
        if (jsonPath.Length < 2)
        {
            return null;
        }

        if (jsonPath[1] == '.')
        {
            var end = jsonPath.IndexOfAny(['.', '['], 2);
            if (end < 0)
            {
                end = jsonPath.Length;
            }

            rest = jsonPath[end..];
            return end > 2 ? jsonPath[2..end] : null;
        }

        if (jsonPath[1] == '[' && jsonPath.Length > 2 && jsonPath[2] is '\'' or '"')
        {
            var close = jsonPath.IndexOf(jsonPath[2], 3);
            if (close < 0 || close + 1 >= jsonPath.Length || jsonPath[close + 1] != ']')
            {
                return null;
            }

            rest = jsonPath[(close + 2)..];
            return close > 3 ? jsonPath[3..close] : null;
        }

        return null;
    }

    /// <summary>
    /// The data-context roots a caller writes through a pipeline's triggers (AB#9767, review finding 4). The request
    /// roots <c>$.body</c>/<c>$.query</c>/<c>$.files</c>/<c>$.headers</c> are always in; the verified
    /// <c>$.principal</c> of <c>FromHttpRequest@2</c> is not (it comes from the validated token).
    /// </summary>
    private sealed class CallerInputScope
    {
        private static readonly string[] TeamsBotRoots = ["Emails", "Conversation"];

        /// <summary>
        /// Triggers whose output carries no outside input: schedules, the tenant's own entity changes and notification
        /// schedules. A path into what they write is data the pipeline controls.
        /// </summary>
        private static readonly HashSet<string> TriggersWithoutOutsideInput = new(StringComparer.OrdinalIgnoreCase)
        {
            "FromPipelineTriggerEvent@1", "FromWatchRtEntity@1", "FromNotificationSchedule@1"
        };

        private static readonly HashSet<string> RequestTriggers = new(StringComparer.OrdinalIgnoreCase)
        {
            "FromHttpRequest@1", VerifiedCallerHttpTrigger
        };

        private CallerInputScope(bool allPaths, IEnumerable<string> extraRoots, bool unreadableRootIsCallerControlled)
        {
            AllPaths = allPaths;
            Roots = new HashSet<string>(CallerControlledRoots.Concat(extraRoots), StringComparer.OrdinalIgnoreCase);
            UnreadableRootIsCallerControlled = unreadableRootIsCallerControlled;
        }

        /// <summary>Every path is caller-controlled: a trigger whose output is not known, or no trigger at all.</summary>
        public bool AllPaths { get; }

        /// <summary>The caller-controlled roots.</summary>
        public IReadOnlySet<string> Roots { get; }

        /// <summary>Whether a path whose first segment cannot be read (<c>$</c>, <c>$[0]</c>) counts as caller-controlled.</summary>
        public bool UnreadableRootIsCallerControlled { get; }

        /// <summary>
        /// The AB#5128 confused-deputy lint (advisory, triggers not consulted): the request roots and multi-match
        /// paths only, so whole-context paths such as <c>inputPath: $</c> stay silent.
        /// </summary>
        public static CallerInputScope RequestRootsOnly { get; } = new(false, [], false);

        /// <summary>
        /// The scope of the cross-tenant rules, fail closed: the request roots, the roots of the Teams bot trigger,
        /// nothing more for triggers without outside input, and every path when any trigger's output is not known here
        /// (other adapters' triggers, mail, Signal, Graph, pipeline events and commands) or the pipeline has no trigger.
        /// </summary>
        public static CallerInputScope ForTriggers(IReadOnlyList<PipelineNodeProperties> triggers)
        {
            if (triggers.Count == 0)
            {
                return new CallerInputScope(true, [], true);
            }

            var extraRoots = new List<string>();
            foreach (var trigger in triggers)
            {
                var type = trigger.NodeType.Trim();
                if (string.Equals(type, "FromTeamsBot@1", StringComparison.OrdinalIgnoreCase))
                {
                    extraRoots.AddRange(TeamsBotRoots);
                }
                else if (!RequestTriggers.Contains(type) && !TriggersWithoutOutsideInput.Contains(type))
                {
                    return new CallerInputScope(true, [], true);
                }
            }

            return new CallerInputScope(false, extraRoots, true);
        }
    }
}
