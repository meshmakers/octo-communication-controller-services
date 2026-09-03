using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Read-only rights analysis for pipeline service accounts (AB#5113, Epic AB#4979): answers
/// "which roles does this service account need?" by joining three tenant-local data sources —
/// the CK types the pipeline set's YAML definitions touch, the tenant's
/// <c>System.Identity</c> data policies/permissions (Epic AB#4969), and the account's declared
/// <c>AssignedRoleNames</c> (AB#5111).
///
/// <para>
/// The pipeline set follows the <see cref="IPipelineServiceAccountResolver" /> semantics:
/// <list type="bullet">
///   <item>configuration-scoped — every pipeline whose <em>effective</em> account is this
///     configuration: its adapter's default pipelines minus those overriding to another account,
///     plus pipelines overriding <em>to</em> it via their <c>Uses</c> edge;</item>
///   <item>adapter-scoped — the adapter's executing pipelines minus those carrying their own
///     override (they execute as someone else; their rights are their own analysis).</item>
/// </list>
/// </para>
///
/// <para>
/// Robustness contract: a pipeline whose definition cannot be parsed contributes a warning entry
/// and never fails the analysis; dynamic type references (<c>ckTypeIdPath</c>, templated ids) are
/// reported as "not statically analyzable" instead of silently ignored; an empty pipeline set
/// yields an empty-but-valid result.
/// </para>
/// </summary>
public interface IServiceAccountRightsAnalysisService
{
    /// <summary>
    /// Rights analysis for one <c>ServiceAccountConfiguration</c>, addressed directly — for
    /// callers that hold the configuration (Studio's configuration view, a per-pipeline
    /// override account).
    /// </summary>
    Task<ServiceAccountRightsAnalysisDto> AnalyzeConfigurationAsync(string tenantId,
        RtServiceAccountConfiguration configuration);

    /// <summary>
    /// Rights analysis for the adapter's default pipeline service account — the default case:
    /// all pipelines the adapter executes under that account. Works even when the adapter has no
    /// account linked yet (the recommendation is then reported without a declaration delta, so
    /// the analysis can inform the account's creation).
    /// </summary>
    Task<ServiceAccountRightsAnalysisDto> AnalyzeAdapterAsync(string tenantId, RtAdapter adapter);
}
