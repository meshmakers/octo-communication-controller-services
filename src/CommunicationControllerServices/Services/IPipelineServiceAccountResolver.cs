using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Where the effective pipeline service account came from (AB#5027).
/// </summary>
public enum PipelineServiceAccountSource
{
    /// <summary>No service account is reachable for the pipeline — deploys are rejected.</summary>
    None = 0,

    /// <summary>
    /// The pipeline carries its own <c>ServiceAccountConfiguration</c> on the generic
    /// <c>Uses</c> role. Beats the adapter default.
    /// </summary>
    PipelineOverride = 1,

    /// <summary>
    /// Inherited from the executing adapter's <c>PipelineServiceAccount</c> association —
    /// the default for every pipeline that adapter runs.
    /// </summary>
    AdapterDefault = 2
}

/// <summary>
/// Outcome of a pipeline service-account resolution (AB#5027).
/// </summary>
/// <param name="ServiceAccount">The effective service account, or <c>null</c> when none is reachable.</param>
/// <param name="Source">Which of the two link kinds produced the result.</param>
public record PipelineServiceAccountResolution(
    RtServiceAccountConfiguration? ServiceAccount,
    PipelineServiceAccountSource Source)
{
    /// <summary>The "nothing linked" result. Deploys of such a pipeline are refused by the guard.</summary>
    public static readonly PipelineServiceAccountResolution Unresolved =
        new(null, PipelineServiceAccountSource.None);

    /// <summary>True when an effective service account exists.</summary>
    public bool IsResolved => ServiceAccount != null;
}

/// <summary>
/// Determines the identity a pipeline executes under (Epic AB#4979 / AB#5027).
///
/// Granularity: <b>one service account per adapter as the default, optionally overridden per
/// pipeline</b>. Resolution order:
/// <list type="number">
///   <item>the pipeline's own <c>ServiceAccountConfiguration</c> (generic <c>Uses</c> edge) —
///     the per-pipeline override; needs no model change, Pipeline→Configuration already exists;</item>
///   <item>the executing adapter's <c>PipelineServiceAccount</c> edge — the adapter-wide default;</item>
///   <item>nothing — <see cref="PipelineServiceAccountResolution.Unresolved"/>.</item>
/// </list>
///
/// The obligation ("every mesh adapter MUST have a service account") is enforced by the deploy
/// guard in <c>AdapterService</c>, not by the CK multiplicity — see
/// <c>ConstructionKit/associations/pipelineServiceAccount.yaml</c>.
/// </summary>
public interface IPipelineServiceAccountResolver
{
    /// <summary>
    /// Resolves the effective service account for a pipeline whose executing adapter is already
    /// known (the deploy paths always know it — saves the extra <c>Executes</c> traversal).
    /// </summary>
    Task<PipelineServiceAccountResolution> ResolveAsync(string tenantId, OctoObjectId pipelineRtId,
        OctoObjectId adapterRtId);

    /// <summary>
    /// Resolves the effective service account for a pipeline, looking the executing adapter up
    /// via the pipeline's <c>Executes</c> edge. A pipeline without an adapter can only be
    /// resolved through its own override.
    /// </summary>
    Task<PipelineServiceAccountResolution> ResolveForPipelineAsync(string tenantId,
        RtEntityId pipelineRtEntityId);

    /// <summary>
    /// The adapter-wide default service account, or <c>null</c> when the adapter has none linked.
    /// Used by the configuration projection, which must know the default independently of any
    /// per-pipeline override.
    /// </summary>
    Task<RtServiceAccountConfiguration?> GetAdapterDefaultAsync(string tenantId, OctoObjectId adapterRtId);
}
