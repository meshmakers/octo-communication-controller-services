namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     The resolved lending scope of one adapter pool (AB#4924).
/// </summary>
/// <remarks>
///     Shaped so the three surfaces (Refinery Studio, <c>octo-cli</c>, the MCP server) can render the
///     same answer without each re-deriving it. <see cref="LendableTenantIds" /> is the useful field:
///     it is the tenant tree, the sharing mode and the allow-list already combined, with the lender
///     itself and all of its ancestors removed — lending never flows upwards.
/// </remarks>
public class AdapterLendingScopeDto
{
    /// <summary>RtId of the adapter pool whose scope this is.</summary>
    public required string AdapterPoolRtId { get; init; }

    /// <summary>
    ///     The pool's <c>AdapterSharingMode</c>: 0 NotShared, 1 Descendants, 2 DescendantsAndSiblings.
    /// </summary>
    public required int SharingMode { get; init; }

    /// <summary>
    ///     The pool's optional allow-list, verbatim. Empty means "no allow-list". It only ever
    ///     narrows <see cref="LendableTenantIds" /> — a tenant listed here that the sharing mode does
    ///     not resolve to is ignored, never granted.
    /// </summary>
    public required IReadOnlyList<string> AllowedTenantIds { get; init; }

    /// <summary>
    ///     Every tenant that may currently borrow a member of this pool, sorted. Already intersected
    ///     with <see cref="AllowedTenantIds" /> and already stripped of the owning tenant and its
    ///     ancestors.
    /// </summary>
    public required IReadOnlyList<string> LendableTenantIds { get; init; }
}
