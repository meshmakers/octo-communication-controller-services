namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     The data policy protecting a touched CK type (AB#5113).
/// </summary>
/// <param name="RtId">RtId of the <c>System.Identity/DataPolicy</c> entity.</param>
/// <param name="Name">
///     Its <c>RtWellKnownName</c> (falling back to the display name), or the rtId when neither is
///     set — for the human half of the answer.
/// </param>
/// <param name="Mode">
///     The policy's enforcement mode: <c>Enforce</c> (violations are rejected/filtered) or
///     <c>AuditOnly</c> (violations are only logged — the migration mode of Epic AB#4969).
/// </param>
public sealed record RightsAnalysisPolicyDto(
    string RtId,
    string Name,
    string Mode);

/// <summary>
///     One protected CK type a pipeline set touches, joined with one policy protecting it
///     (AB#5113). A type targeted by several policies produces one entry per policy — the entry is
///     the (type × policy) join, so <c>CkTypeId</c> may repeat.
/// </summary>
/// <param name="CkTypeId">
///     The touched CK type, version-normalized to <c>Model/Type</c> (pipeline YAML references
///     types unversioned; policy targets may carry an element version — matching strips it).
/// </param>
/// <param name="TouchedByPipelines">Names of the analyzed pipelines referencing this type (provenance).</param>
/// <param name="Policy">The protecting policy.</param>
/// <param name="OwnerScoped">
///     <c>true</c> when the policy grants <c>OwnedOnly</c> scope. 🔴 D7: a service account owns
///     next to nothing, so an owner-scoped grant leaves it effectively blind on this type — it
///     needs a full-scope grant per type, and the delta below counts only full-scope grants as
///     coverage.
/// </param>
/// <param name="GrantingRoles">
///     Roles holding this policy's permission (via the <c>GrantsPermission</c> edges) — the roles
///     that would give the service account access under this policy.
/// </param>
/// <param name="Message">Human-readable finding for this entry; <c>null</c> when nothing is noteworthy.</param>
public sealed record RightsAnalysisProtectedTypeDto(
    string CkTypeId,
    IReadOnlyList<string> TouchedByPipelines,
    RightsAnalysisPolicyDto Policy,
    bool OwnerScoped,
    IReadOnlyList<string> GrantingRoles,
    string? Message = null);

/// <summary>
///     A type reference the analysis cannot resolve statically (AB#5113): a <c>ckTypeIdPath</c>
///     property (the type is looked up from the data context at runtime) or a type-referencing
///     property whose value is not a literal <c>Model/Type</c> id (e.g. a template token).
///     Reported instead of silently ignored — the analysis is incomplete for these nodes.
/// </summary>
/// <param name="PipelineName">The pipeline containing the node.</param>
/// <param name="NodeType">The node type (e.g. <c>SetPrimitiveValue@1</c>).</param>
/// <param name="PropertyName">The property carrying the dynamic reference.</param>
public sealed record RightsAnalysisDynamicTypeUsageDto(
    string PipelineName,
    string NodeType,
    string PropertyName);

/// <summary>
///     A pipeline the analysis could not evaluate (AB#5113) — an unparsable or empty
///     <c>PipelineDefinition</c>. The analysis continues over the remaining pipelines; robustness
///     rule of the work item.
/// </summary>
/// <param name="PipelineName">The affected pipeline.</param>
/// <param name="Message">Why it was skipped.</param>
public sealed record RightsAnalysisWarningDto(
    string PipelineName,
    string Message);

/// <summary>
///     Answer of the AB#5113 rights-analysis endpoints
///     (<c>GET {tenantId}/v1/serviceAccount/{configurationRtId}/rightsAnalysis</c> and
///     <c>GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rightsAnalysis</c>): which roles
///     the pipeline service account needs, computed by joining the CK types the pipeline set
///     touches with the tenant's data policies/permissions and the account's declared roles.
/// </summary>
/// <remarks>
///     Read-only and side-effect free, like the AB#5112 health aggregate. Touched types without
///     any protecting policy are dropped — an unprotected type needs no role. Controller-local
///     for now; promoting it into <c>Communication.Contracts</c> is the follow-up that wires
///     CLI/MCP/Studio onto the endpoint (same plan as the reconcile and health DTOs).
/// </remarks>
/// <param name="ConfigurationRtId">RtId of the analyzed configuration, or <c>null</c> when none exists (adapter without a default account).</param>
/// <param name="ConfigurationWellKnownName">Its <c>RtWellKnownName</c>, when one exists.</param>
/// <param name="AnalyzedPipelines">Names of the pipelines in the analyzed set (after scope resolution), in stable order.</param>
/// <param name="ProtectedTypes">The (touched type × protecting policy) join — see <see cref="RightsAnalysisProtectedTypeDto" />.</param>
/// <param name="DynamicTypeUsages">Type references that are dynamic and not statically analyzable.</param>
/// <param name="Warnings">Pipelines skipped because their definition could not be analyzed.</param>
/// <param name="DeclaredRoles">
///     The configuration's <c>AssignedRoleNames</c> declaration, or <c>null</c> for a legacy
///     (pre-3.32.0) configuration without one — then the delta lists below stay empty and the
///     per-type <c>GrantingRoles</c> are the recommendation.
/// </param>
/// <param name="MissingRoles">
///     Roles that would grant (full-scope) access to touched protected types the declaration does
///     not cover yet. Empty for a legacy configuration.
/// </param>
/// <param name="SuperfluousRoles">
///     Declared roles granting nothing for any touched protected type — candidates for removal.
///     Baseline roles are excluded (see <paramref name="BaselineRoles" />).
/// </param>
/// <param name="BaselineRoles">
///     Declared roles that are the operational baseline every pipeline service account carries
///     (<c>CommunicationManagement</c> — the role the controller's own APIs require). They grant
///     no data access and would otherwise always look superfluous, so they are reported here
///     instead.
/// </param>
/// <param name="Message">Human-readable summary of the whole analysis.</param>
public sealed record ServiceAccountRightsAnalysisDto(
    string? ConfigurationRtId,
    string? ConfigurationWellKnownName,
    IReadOnlyList<string> AnalyzedPipelines,
    IReadOnlyList<RightsAnalysisProtectedTypeDto> ProtectedTypes,
    IReadOnlyList<RightsAnalysisDynamicTypeUsageDto> DynamicTypeUsages,
    IReadOnlyList<RightsAnalysisWarningDto> Warnings,
    IReadOnlyList<string>? DeclaredRoles,
    IReadOnlyList<string> MissingRoles,
    IReadOnlyList<string> SuperfluousRoles,
    IReadOnlyList<string> BaselineRoles,
    string Message);
