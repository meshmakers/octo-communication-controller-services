namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

/// <summary>
///     Keeps each borrowing tenant's <c>LentAdapterPool</c> mirrors in step with the
///     <c>AdapterPool</c>s its ancestors and siblings actually lend it (AB#5271).
/// </summary>
/// <remarks>
///     <para>
///         Follows <c>ClientMirrorProvisioningService</c> in octo-identity-services: the owning
///         tenant pushes down, the borrower never polls up, and every operation is idempotent so the
///         startup re-check and a backfill are one code path.
///     </para>
///     <para>
///         🔴 <b>It differs from that precedent in one deliberate way: there is no tracking row.</b>
///         Client mirroring records a <c>ClientMirror</c> in the parent to remember what it
///         provisioned. Here the mirror's own <c>Lender</c> record is the key, and the desired set is
///         recomputed from the tenant tree on every run. So a mirror somebody deleted by hand comes
///         back, a mirror the controller failed to remove is removed on the next pass, and no second
///         piece of state can drift from the first. The cost is that a run reads every candidate
///         lender's pools rather than a short list — bounded by the tenant's ancestry, which is a
///         handful of tenants.
///     </para>
///     <para>
///         🔴 <b>This service is on the critical path</b> (concept §7.3). Since AB#5271 the
///         association is the truth, so an adapter cannot name its lender until the mirror exists: a
///         sync that silently does nothing is an outage, not a display defect. That is why the
///         result type counts what happened instead of returning void, and why failures are reported
///         per lender rather than swallowed into a single boolean.
///     </para>
/// </remarks>
public interface IAdapterPoolMirrorProvisioningService
{
    /// <summary>
    ///     Reconciles every mirror in <paramref name="borrowerTenantId" /> against what its
    ///     candidate lenders currently lend it.
    /// </summary>
    /// <remarks>
    ///     🔴 Removal never cascades into the borrower's adapters (concept §6). An adapter left
    ///     pointing at a mirror that has gone is a defined, reported state — a refused deploy with a
    ///     named reason — and deleting a tenant's adapters because a lender narrowed its scope would
    ///     be a far worse failure than a blocked deploy.
    /// </remarks>
    Task<AdapterPoolMirrorSyncResult> ProvisionForBorrowerAsync(string borrowerTenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Fans a change on <paramref name="lenderTenantId" />'s pools out to every tenant that
    ///     could be borrowing from it.
    /// </summary>
    /// <remarks>
    ///     Each borrower is reconciled in full rather than incrementally: the per-borrower pass is
    ///     the only place that knows the whole desired set, and a narrower "just this pool" update
    ///     would need its own removal rules and its own bugs. A failing borrower does not stop the
    ///     others.
    /// </remarks>
    Task<AdapterPoolMirrorSyncResult> ProvisionForLenderAsync(string lenderTenantId,
        CancellationToken cancellationToken = default);
}

/// <summary>What one reconciliation run did (AB#5271).</summary>
/// <param name="TenantsReconciled">How many borrowing tenants were walked.</param>
/// <param name="MirrorsCreatedOrUpdated">
///     Mirrors actually written — created, or updated because the lender changed something. A
///     mirror that already said the right thing is not counted, so a reconcile over an unchanged
///     estate reports zero rather than reporting every pool as work.
/// </param>
/// <param name="MirrorsRemoved">Mirrors dropped because the pool no longer lends here, or is gone.</param>
/// <param name="LendersUnreadable">
///     Candidate lenders whose pools could not be read. Counted rather than thrown: a tenant being
///     created or deleted while the sweep runs must not stop every other borrower from being
///     reconciled — but a persistently non-zero value means somebody is missing mirrors.
/// </param>
/// <param name="AdaptersRelinked">
///     Leased adapters whose <c>LentFrom</c> edge was restored from the attribute values AB#5271
///     orphaned (AB#5349). One-time per adapter, so a persistently non-zero value would mean the
///     edge is not sticking. 🔴 It counts towards <see cref="IsNoOp" />: the run that surfaced this
///     defect answered <c>isNoOp: true</c> while a borrower sat unlinked, because the backfill had no
///     opinion about the adapter's edge — a repair that reports nothing is how the gap stayed
///     invisible.
/// </param>
public sealed record AdapterPoolMirrorSyncResult(
    int TenantsReconciled,
    int MirrorsCreatedOrUpdated,
    int MirrorsRemoved,
    int LendersUnreadable,
    int AdaptersRelinked = 0)
{
    /// <summary>A run that did nothing — the neutral element for <see cref="Add" />.</summary>
    public static readonly AdapterPoolMirrorSyncResult Nothing = new(0, 0, 0, 0, 0);

    /// <summary>Accumulates a per-tenant result into a fan-out total.</summary>
    public AdapterPoolMirrorSyncResult Add(AdapterPoolMirrorSyncResult other)
    {
        return new AdapterPoolMirrorSyncResult(
            TenantsReconciled + other.TenantsReconciled,
            MirrorsCreatedOrUpdated + other.MirrorsCreatedOrUpdated,
            MirrorsRemoved + other.MirrorsRemoved,
            LendersUnreadable + other.LendersUnreadable,
            AdaptersRelinked + other.AdaptersRelinked);
    }

    /// <summary>True when the run changed nothing — the normal outcome of a startup re-check.</summary>
    public bool IsNoOp => MirrorsCreatedOrUpdated == 0 && MirrorsRemoved == 0 && AdaptersRelinked == 0;
}
