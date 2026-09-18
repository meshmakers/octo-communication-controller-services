using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Which adapter pool, in which tenant, a borrowing adapter leases members from (AB#5271).
/// </summary>
/// <remarks>
///     <para>
///         🔴 This is the <b>resolved</b> form of the borrower half, not a stored one. Until AB#5271
///         the two values sat on <c>RtAdapter</c> as the attribute pair
///         <c>LentFromTenantId</c>/<c>LentFromAdapterPoolRtId</c>; they now come from the adapter's
///         <c>LentFrom</c> edge to a <see cref="RtLentAdapterPool" /> in its own tenant, whose
///         <c>Lender</c> record carries the cross-tenant part once per pool instead of twice per
///         adapter.
///     </para>
///     <para>
///         🔴 Resolving it is a repository read, so it is never resolved speculatively and never
///         cached: re-pointing an adapter at a different pool must take effect at once, and a stale
///         answer here would send a lease request to the wrong lender. (The 30 s cache in
///         <see cref="TenantLendingScopeResolver" /> caches the lending <i>scope</i> — who may borrow
///         — which is a different question with a different blast radius.)
///     </para>
///     <para>
///         🔴 It still carries <b>no referential integrity</b>. The mirror is local, but what it
///         names is an <c>AdapterPool</c> in another tenant's database that nothing can constrain.
///         Every consumer must keep treating an unresolvable pool as a refusal with a named reason,
///         never as an impossibility.
///     </para>
/// </remarks>
/// <param name="LenderTenantId">The tenant that owns the pool.</param>
/// <param name="AdapterPoolRtId">The pool's RtId inside that tenant, as a bare 24-char hex string.</param>
public sealed record LentFromReference(string LenderTenantId, string AdapterPoolRtId)
{
    /// <summary>
    ///     Projects a borrower-local mirror onto the pair its consumers need, or null when the mirror
    ///     names no usable pool.
    /// </summary>
    /// <remarks>
    ///     The <c>Lender</c> record is written as one unit by the provisioning sweep, so a half-filled
    ///     one cannot arise from the controller. A tenant can still edit entities in its own database,
    ///     which is exactly why this returns null instead of assuming the record is complete — and why
    ///     the callers keep their "incomplete lender" branches.
    /// </remarks>
    public static LentFromReference? FromMirror(RtLentAdapterPool? mirror)
    {
        var lender = mirror?.Lender;
        if (lender is null ||
            string.IsNullOrWhiteSpace(lender.LenderTenantId) ||
            string.IsNullOrWhiteSpace(lender.LenderAdapterPoolRtId))
        {
            return null;
        }

        return new LentFromReference(lender.LenderTenantId!, lender.LenderAdapterPoolRtId!);
    }
}
