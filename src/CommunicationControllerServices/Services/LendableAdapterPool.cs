namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     One adapter pool as seen from outside its owning tenant (AB#5271): enough of it to mirror
///     into a borrower and to decide whether that borrower may use it.
/// </summary>
/// <remarks>
///     🔴 Lifted off the CK entity on purpose. <c>IAttributeValueList</c> is a live view over an
///     entity bound to its session, and mirroring reads one lender and writes into many borrowers
///     — so the values have to survive the lender's session closing. The same reason
///     <c>TryGetAdapterPoolLendingScopeAsync</c> materialises its allow-list before returning it.
///
///     <para>
///     Carries the pool's identity, its lending configuration and the handful of fields a borrower
///     needs to judge availability. Deliberately NOT the chart name, version or values: those are
///     the lender's deployment detail and have no business being copied into another tenant's
///     database.
///     </para>
/// </remarks>
/// <param name="LenderTenantId">The tenant that owns the pool.</param>
/// <param name="AdapterPoolRtId">The pool's RtId inside that tenant, as a bare 24-char hex string.</param>
/// <param name="Name">The pool's display name, mirrored so a borrower sees something meaningful.</param>
/// <param name="Description">Optional description, mirrored for the same reason.</param>
/// <param name="Scope">
///     The pool's sharing mode and allow-list — what <c>MayLendAsync</c> needs to decide whether
///     this pool lends to a given borrower.
/// </param>
/// <param name="MinReplicas">Lower bound of the pool's replica range; tells a borrower how much capacity stays warm.</param>
/// <param name="MaxReplicas">Upper bound of the replica range.</param>
/// <param name="DeploymentState">
///     Deployment state of the pool. The one mirrored field that explains a queue which never
///     drains: a pool that is not deployed cannot serve a lease.
/// </param>
public sealed record LendableAdapterPool(
    string LenderTenantId,
    string AdapterPoolRtId,
    string Name,
    string? Description,
    LendingScope Scope,
    int MinReplicas,
    int MaxReplicas,
    int DeploymentState);
