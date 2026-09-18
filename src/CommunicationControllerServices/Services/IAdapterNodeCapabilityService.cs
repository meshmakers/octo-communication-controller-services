using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     What a pipeline deployed to one adapter will actually be executed by (AB#4924).
/// </summary>
/// <param name="NodeDescriptors">
///     The node descriptors of the process that will run it, or null when none are known — the
///     callers then degrade to their own name-based fallbacks.
/// </param>
/// <param name="PipelineSchemaJson">
///     The composite pipeline JSON Schema of that process, or null when it reported none.
/// </param>
/// <param name="Source">
///     Human-readable provenance, for log lines and deploy events. Never a decision input.
/// </param>
public sealed record AdapterNodeCapabilities(
    IReadOnlyList<NodeDescriptorDto>? NodeDescriptors,
    string? PipelineSchemaJson,
    string Source);

/// <summary>
///     Answers "whose node descriptors decide what this adapter can run" for both kinds of adapter
///     (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         A <b>dedicated</b> adapter answers for itself: it registered on
///         <c>/{tenantId}/adapterHub</c> and its descriptors sit on the cached
///         <see cref="Caches.Adapters.Adapter" />.
///     </para>
///     <para>
///         A <b>Leased</b> adapter has no process of its own — it never holds an adapter-hub
///         connection and never will. Its pipelines are executed by a member of the pool named by
///         <c>LentFromTenantId</c> / <c>LentFromAdapterPoolRtId</c>, so the pool's members are the only
///         honest source. This seam exists because three separate deploy-time questions — schema
///         validation, process-bound-trigger classification, and the execution class — each used to
///         reach into <c>AdapterById</c> on their own and each would have had to grow the same
///         branch.
///     </para>
/// </remarks>
public interface IAdapterNodeCapabilityService
{
    /// <summary>
    ///     Resolves the capabilities that apply to <paramref name="adapterRtEntityId" />.
    /// </summary>
    /// <param name="tenantId">The tenant owning the adapter — the borrower, for a leased one.</param>
    /// <param name="adapterRtEntityId">The adapter.</param>
    /// <param name="adapter">
    ///     The adapter entity, when the caller has already read it. Required to recognise a
    ///     <c>Leased</c> adapter: the lending pool hangs off the entity's <c>LentFrom</c> edge, not
    ///     off any cache. Passing null resolves the dedicated way, which is what every pre-AB#4924
    ///     caller means.
    /// </param>
    /// <remarks>
    ///     Asynchronous since AB#5271: a leased adapter's lender is a repository read of its
    ///     <c>LentFrom</c> mirror rather than two attributes on the adapter. The dedicated path still
    ///     answers from the cache without touching the database.
    /// </remarks>
    Task<AdapterNodeCapabilities> ResolveAsync(string tenantId, RtEntityId adapterRtEntityId, RtAdapter? adapter);
}
