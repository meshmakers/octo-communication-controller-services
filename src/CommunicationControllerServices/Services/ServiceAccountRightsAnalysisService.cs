using System.Text.RegularExpressions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IServiceAccountRightsAnalysisService" />
internal partial class ServiceAccountRightsAnalysisService(
    ICommunicationRepository communicationRepository,
    IPipelineServiceAccountResolver serviceAccountResolver,
    IPipelineDefinitionService pipelineDefinitionService)
    : IServiceAccountRightsAnalysisService
{
    /// <summary>
    /// Node properties that reference a CK type literally. Matched case-insensitively against the
    /// camelCase YAML keys, at any nesting depth inside a node's configuration — association
    /// updates carry <c>targetCkTypeId</c>/<c>originCkTypeId</c> inside nested update records,
    /// not at the node's top level.
    /// </summary>
    private static readonly string[] TypeReferenceProperties =
        ["ckTypeId", "ckTypeIds", "targetCkTypeId", "originCkTypeId"];

    /// <summary>
    /// A literal, statically analyzable CK type id: <c>Model/Type</c> with an optional element
    /// version suffix (<c>Model/Type-2</c>). Anything else in a type-referencing property (a
    /// template token, a JSONPath) is a dynamic reference and is reported, not resolved.
    /// </summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.]+/[A-Za-z0-9_.]+(-\d+)?$")]
    private static partial Regex LiteralCkTypeIdRegex();

    /// <inheritdoc />
    public async Task<ServiceAccountRightsAnalysisDto> AnalyzeConfigurationAsync(string tenantId,
        RtServiceAccountConfiguration configuration)
    {
        // The effective-account set of a configuration, per the resolver's precedence rules:
        // every pipeline of "its" adapter that actually resolves to it (the adapter default
        // minus pipelines overriding to another account), plus pipelines overriding TO it.
        var pipelines = new Dictionary<OctoObjectId, RtPipeline>();

        var adapter = await communicationRepository.GetAdapterForServiceAccountAsync(tenantId, configuration.RtId);
        if (adapter != null)
        {
            var adapterPipelines = await communicationRepository.GetPipelinesAsync(tenantId,
                new RtEntityId(adapter.CkTypeId!, adapter.RtId));
            foreach (var pipeline in adapterPipelines)
            {
                var resolution = await serviceAccountResolver.ResolveAsync(tenantId, pipeline.RtId, adapter.RtId);
                if (resolution.ServiceAccount?.RtId == configuration.RtId)
                {
                    pipelines[pipeline.RtId] = pipeline;
                }
            }
        }

        // Pipelines linking this configuration via Uses. The resolver decides whether the link is
        // the effective override — a pipeline may link several service accounts, and only the
        // deterministic pick counts (same rule the deploy applies, AB#5027).
        var overrideCandidates =
            await communicationRepository.GetPipelinesUsingServiceAccountAsync(tenantId, configuration.RtId);
        foreach (var pipeline in overrideCandidates)
        {
            if (pipelines.ContainsKey(pipeline.RtId))
            {
                continue;
            }

            var resolution = await serviceAccountResolver.ResolveForPipelineAsync(tenantId,
                new RtEntityId(pipeline.CkTypeId!, pipeline.RtId));
            if (resolution.Source == PipelineServiceAccountSource.PipelineOverride &&
                resolution.ServiceAccount!.RtId == configuration.RtId)
            {
                pipelines[pipeline.RtId] = pipeline;
            }
        }

        return await AnalyzeCoreAsync(tenantId, configuration, pipelines.Values);
    }

    /// <inheritdoc />
    public async Task<ServiceAccountRightsAnalysisDto> AnalyzeAdapterAsync(string tenantId, RtAdapter adapter)
    {
        var configuration = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);

        // The adapter's executing pipelines minus those with their own override — an overriding
        // pipeline executes as someone else, so its rights belong to that account's analysis.
        var pipelines = new List<RtPipeline>();
        var adapterPipelines = await communicationRepository.GetPipelinesAsync(tenantId,
            new RtEntityId(adapter.CkTypeId!, adapter.RtId));
        foreach (var pipeline in adapterPipelines)
        {
            var resolution = await serviceAccountResolver.ResolveAsync(tenantId, pipeline.RtId, adapter.RtId);
            if (resolution.Source != PipelineServiceAccountSource.PipelineOverride)
            {
                pipelines.Add(pipeline);
            }
        }

        return await AnalyzeCoreAsync(tenantId, configuration, pipelines);
    }

    /// <summary>
    /// The shared core: extract touched types from the pipeline set, join them with the tenant's
    /// data policies/permissions/roles, and compute the delta against the declaration.
    /// </summary>
    private async Task<ServiceAccountRightsAnalysisDto> AnalyzeCoreAsync(string tenantId,
        RtServiceAccountConfiguration? configuration, IReadOnlyCollection<RtPipeline> pipelines)
    {
        // ---- 1. Extraction: touched types (with per-type pipeline provenance), dynamic
        //         references, and warnings for pipelines the analysis cannot see into.
        var touchedTypes = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var dynamicUsages = new List<RightsAnalysisDynamicTypeUsageDto>();
        var warnings = new List<RightsAnalysisWarningDto>();
        var analyzedPipelines = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var pipeline in pipelines.OrderBy(p => p.Name ?? p.RtId.ToString(), StringComparer.Ordinal))
        {
            var pipelineName = pipeline.Name ?? pipeline.RtId.ToString();
            analyzedPipelines.Add(pipelineName);

            if (string.IsNullOrWhiteSpace(pipeline.PipelineDefinition))
            {
                warnings.Add(new RightsAnalysisWarningDto(pipelineName,
                    "The pipeline has no definition — nothing to analyze."));
                continue;
            }

            if (!pipelineDefinitionService.TryGetAllNodes(pipeline.PipelineDefinition, out var nodes))
            {
                warnings.Add(new RightsAnalysisWarningDto(pipelineName,
                    "The pipeline definition could not be parsed as YAML — its touched types are unknown. " +
                    "Fix the definition and re-run the analysis."));
                continue;
            }

            foreach (var node in nodes)
            {
                foreach (var property in node.Properties)
                {
                    ScanProperty(pipelineName, node.NodeType, property.Key, property.Value,
                        touchedTypes, dynamicUsages);
                }
            }
        }

        // ---- 2. Join: which touched types are protected, by which policies, granting which
        //         roles. Unprotected types are dropped — no policy targets them, so no role is
        //         needed (Epic AB#4969: a type is protected as soon as any policy targets it).
        var protectedTypes = new List<RightsAnalysisProtectedTypeDto>();
        var fullScopeRolesByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var ownerScopedOnlyCandidates = new HashSet<string>(StringComparer.Ordinal);

        if (touchedTypes.Count > 0)
        {
            var policies = await communicationRepository.GetDataPoliciesAsync(tenantId);

            // A policy matches a touched type on the Model/Type name part, version-insensitively:
            // pipeline YAML references types unversioned, policy targets are stored canonically
            // as SemanticVersionedFullName and may carry an element version.
            var matches = new List<(RtEntity Policy, List<string> Types)>();
            foreach (var policy in policies)
            {
                var targets = policy.GetAttributeStringValuesOrDefault("TargetCkTypeIds");
                if (targets == null)
                {
                    continue;
                }

                var normalizedTargets = new HashSet<string>(
                    targets.Select(NormalizeCkTypeId), StringComparer.Ordinal);
                var matchedTypes = touchedTypes.Keys.Where(normalizedTargets.Contains).ToList();
                if (matchedTypes.Count > 0)
                {
                    matches.Add((policy, matchedTypes));
                }
            }

            // Batch the two edge hops once for all matched policies.
            var permissionsByPolicy = await communicationRepository.GetDataPermissionsForPoliciesAsync(tenantId,
                matches.Select(m => m.Policy.RtId).ToList());
            var allPermissionIds = permissionsByPolicy.Values
                .SelectMany(permissions => permissions.Select(p => p.RtId))
                .Distinct()
                .ToList();
            var rolesByPermission =
                await communicationRepository.GetGrantingRolesForDataPermissionsAsync(tenantId, allPermissionIds);

            foreach (var (policy, matchedTypes) in matches)
            {
                var policyDto = new RightsAnalysisPolicyDto(
                    policy.RtId.ToString(),
                    policy.RtWellKnownName ?? policy.RtDisplayName ?? policy.RtId.ToString(),
                    // Enum keys per the identity CK model: EnforcementMode 0=Enforce/1=AuditOnly,
                    // Scope 0=All/1=OwnedOnly.
                    policy.GetAttributeValueOrStandard<int>("EnforcementMode") == 1 ? "AuditOnly" : "Enforce");
                var ownerScoped = policy.GetAttributeValueOrStandard<int>("Scope") == 1;

                var grantingRoles = new SortedSet<string>(StringComparer.Ordinal);
                if (permissionsByPolicy.TryGetValue(policy.RtId, out var permissions))
                {
                    foreach (var permission in permissions)
                    {
                        if (!rolesByPermission.TryGetValue(permission.RtId, out var roles))
                        {
                            continue;
                        }

                        foreach (var role in roles)
                        {
                            var roleName = role.GetAttributeStringValueOrDefault("Name")
                                           ?? role.RtWellKnownName ?? role.RtId.ToString();
                            grantingRoles.Add(roleName);
                        }
                    }
                }

                foreach (var type in matchedTypes)
                {
                    // 🔴 D7: an owner-scoped grant never helps a service account — it owns next
                    // to nothing, so only full-scope grants count as coverage.
                    string? entryMessage = null;
                    if (ownerScoped)
                    {
                        ownerScopedOnlyCandidates.Add(type);
                        entryMessage =
                            "Owner-scoped (OwnedOnly) grant: a service account owns no entities, so these roles do " +
                            "not give it access to this type — it needs a full-scope grant or stays blind (D7).";
                    }
                    else
                    {
                        if (!fullScopeRolesByType.TryGetValue(type, out var roleSet))
                        {
                            roleSet = new HashSet<string>(StringComparer.Ordinal);
                            fullScopeRolesByType[type] = roleSet;
                        }

                        roleSet.UnionWith(grantingRoles);
                    }

                    protectedTypes.Add(new RightsAnalysisProtectedTypeDto(
                        type,
                        touchedTypes[type].ToList(),
                        policyDto,
                        ownerScoped,
                        grantingRoles.ToList(),
                        entryMessage));
                }
            }

            // Stable order: by type, then by policy name — a type with several policies keeps its
            // entries adjacent.
            protectedTypes = protectedTypes
                .OrderBy(e => e.CkTypeId, StringComparer.Ordinal)
                .ThenBy(e => e.Policy.Name, StringComparer.Ordinal)
                .ToList();
        }

        // Types that are only reachable through owner-scoped grants — no role can help here.
        var blindTypes = ownerScopedOnlyCandidates
            .Where(type => !fullScopeRolesByType.ContainsKey(type))
            .Order(StringComparer.Ordinal)
            .ToList();

        // ---- 3. Delta against the declaration (AB#5111). Legacy configurations (no declaration)
        //         get the recommendation without a delta — the per-type GrantingRoles say what to
        //         declare once the account is opted into declarative role management.
        var declaredRoles = configuration?.AssignedRoleNames?.ToList();
        var missingRoles = new SortedSet<string>(StringComparer.Ordinal);
        var superfluousRoles = new List<string>();
        var baselineRoles = new List<string>();

        if (declaredRoles != null)
        {
            var declared = new HashSet<string>(declaredRoles, StringComparer.Ordinal);
            foreach (var (_, grantingRoles) in fullScopeRolesByType)
            {
                if (grantingRoles.Overlaps(declared))
                {
                    continue; // The declaration already covers this type.
                }

                missingRoles.UnionWith(grantingRoles);
            }

            // A declared role is superfluous when it grants nothing for any touched protected
            // type — unless it is the operational baseline every pipeline service account
            // carries (CommunicationManagement, the role the controller's own APIs require): it
            // never grants data access, so calling it superfluous would tell users to remove a
            // role every pipeline needs.
            var allGrantingRoles = new HashSet<string>(
                protectedTypes.SelectMany(e => e.GrantingRoles), StringComparer.Ordinal);
            foreach (var role in declaredRoles)
            {
                if (PipelineServiceAccountProvisioningService.DefaultAssignedRoleNames
                    .Contains(role, StringComparer.Ordinal))
                {
                    baselineRoles.Add(role);
                }
                else if (!allGrantingRoles.Contains(role))
                {
                    superfluousRoles.Add(role);
                }
            }
        }

        return new ServiceAccountRightsAnalysisDto(
            configuration?.RtId.ToString(),
            configuration?.RtWellKnownName,
            analyzedPipelines.ToList(),
            protectedTypes,
            dynamicUsages,
            warnings,
            declaredRoles,
            missingRoles.ToList(),
            superfluousRoles,
            baselineRoles,
            BuildMessage(configuration, analyzedPipelines.Count, protectedTypes, dynamicUsages.Count,
                warnings.Count, declaredRoles, missingRoles, superfluousRoles, blindTypes));
    }

    /// <summary>
    /// Recursively scans one node property for CK type references. Literal ids land in
    /// <paramref name="touchedTypes"/> (version-normalized, with pipeline provenance); everything
    /// the analysis cannot resolve statically — a <c>*ckTypeIdPath</c> property, or a
    /// type-referencing property whose value is no literal id — lands in
    /// <paramref name="dynamicUsages"/> instead of being silently dropped.
    /// </summary>
    private static void ScanProperty(string pipelineName, string nodeType, string propertyName, object? value,
        SortedDictionary<string, SortedSet<string>> touchedTypes,
        List<RightsAnalysisDynamicTypeUsageDto> dynamicUsages)
    {
        if (propertyName.EndsWith("ckTypeIdPath", StringComparison.OrdinalIgnoreCase))
        {
            // The type is resolved from the data context at runtime — not statically analyzable.
            AddDynamicUsage(dynamicUsages, pipelineName, nodeType, propertyName);
            return;
        }

        if (TypeReferenceProperties.Contains(propertyName, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var candidate in EnumerateScalarValues(value))
            {
                if (candidate is string { Length: > 0 } literal && LiteralCkTypeIdRegex().IsMatch(literal))
                {
                    var normalized = NormalizeCkTypeId(literal);
                    if (!touchedTypes.TryGetValue(normalized, out var pipelineNames))
                    {
                        pipelineNames = new SortedSet<string>(StringComparer.Ordinal);
                        touchedTypes[normalized] = pipelineNames;
                    }

                    pipelineNames.Add(pipelineName);
                }
                else
                {
                    // A template token, a JSONPath, an empty value — dynamic, not analyzable.
                    AddDynamicUsage(dynamicUsages, pipelineName, nodeType, propertyName);
                }
            }

            return;
        }

        // Recurse into complex values: type references also live inside nested structures (an
        // association update's targetCkTypeId, a Switch case's child configuration, ...).
        switch (value)
        {
            case Dictionary<object, object> nested:
                foreach (var kvp in nested)
                {
                    ScanProperty(pipelineName, nodeType, kvp.Key.ToString() ?? string.Empty, kvp.Value,
                        touchedTypes, dynamicUsages);
                }

                break;

            case List<object> list:
                foreach (var item in list)
                {
                    if (item is Dictionary<object, object> itemDict)
                    {
                        foreach (var kvp in itemDict)
                        {
                            ScanProperty(pipelineName, nodeType, kvp.Key.ToString() ?? string.Empty, kvp.Value,
                                touchedTypes, dynamicUsages);
                        }
                    }
                }

                break;
        }
    }

    /// <summary>
    /// The scalar candidates of a type-referencing property: the value itself, or — for the
    /// plural <c>ckTypeIds</c> shape — each list element.
    /// </summary>
    private static IEnumerable<object?> EnumerateScalarValues(object? value)
    {
        if (value is List<object> list)
        {
            return list;
        }

        return [value];
    }

    private static void AddDynamicUsage(List<RightsAnalysisDynamicTypeUsageDto> dynamicUsages,
        string pipelineName, string nodeType, string propertyName)
    {
        var usage = new RightsAnalysisDynamicTypeUsageDto(pipelineName, nodeType, propertyName);
        if (!dynamicUsages.Contains(usage))
        {
            dynamicUsages.Add(usage);
        }
    }

    /// <summary>
    /// Version-insensitive canonical form of a CK type id: <c>Model/Type-2</c> →
    /// <c>Model/Type</c>. Pipeline YAML references types unversioned; policy targets are stored
    /// canonically (SemanticVersionedFullName — the element carries the version, the model does
    /// not), so both sides are normalized before matching.
    /// </summary>
    internal static string NormalizeCkTypeId(string ckTypeId)
    {
        var trimmed = ckTypeId.Trim();
        var dashIndex = trimmed.LastIndexOf('-');
        if (dashIndex > trimmed.IndexOf('/') && dashIndex < trimmed.Length - 1 &&
            trimmed[(dashIndex + 1)..].All(char.IsAsciiDigit))
        {
            return trimmed[..dashIndex];
        }

        return trimmed;
    }

    /// <summary>The human half of the answer, mirroring the AB#5112 style of one readable summary.</summary>
    private static string BuildMessage(RtServiceAccountConfiguration? configuration, int pipelineCount,
        IReadOnlyList<RightsAnalysisProtectedTypeDto> protectedTypes, int dynamicCount, int warningCount,
        IReadOnlyList<string>? declaredRoles, IReadOnlyCollection<string> missingRoles,
        IReadOnlyCollection<string> superfluousRoles, IReadOnlyList<string> blindTypes)
    {
        if (pipelineCount == 0)
        {
            return "No pipelines execute under this service account — nothing to analyze.";
        }

        var distinctTypes = protectedTypes.Select(e => e.CkTypeId).Distinct().Count();
        var parts = new List<string>
        {
            $"Analyzed {pipelineCount} pipeline(s): {distinctTypes} protected CK type(s) touched."
        };

        if (dynamicCount > 0)
        {
            parts.Add($"{dynamicCount} type reference(s) are dynamic and could not be analyzed statically — " +
                      "the analysis may be incomplete.");
        }

        if (warningCount > 0)
        {
            parts.Add($"{warningCount} pipeline(s) could not be analyzed (see warnings).");
        }

        if (blindTypes.Count > 0)
        {
            parts.Add("🔴 Only owner-scoped grants exist for: " + string.Join(", ", blindTypes) +
                      " — no role gives a service account access to these types (D7); " +
                      "a full-scope DataPermission is needed.");
        }

        if (configuration == null)
        {
            parts.Add("No service account configuration exists yet — the per-type granting roles are the " +
                      "recommendation for its declaration.");
        }
        else if (declaredRoles == null)
        {
            parts.Add("The configuration declares no AssignedRoleNames (pre-3.32.0 legacy account), so no delta " +
                      "is computed — the per-type granting roles are the recommendation.");
        }
        else
        {
            if (missingRoles.Count > 0)
            {
                parts.Add("Missing roles (would grant access to touched protected types): " +
                          string.Join(", ", missingRoles) + ".");
            }

            if (superfluousRoles.Count > 0)
            {
                parts.Add("Superfluous roles (grant nothing for any touched type): " +
                          string.Join(", ", superfluousRoles) + ".");
            }

            if (missingRoles.Count == 0 && superfluousRoles.Count == 0)
            {
                parts.Add("The declared roles match the touched protected types.");
            }
        }

        return string.Join(" ", parts);
    }
}
