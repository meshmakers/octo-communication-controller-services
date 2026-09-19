namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     One adapter pool read in full from its owning tenant, for a BORROWER to inspect (AB#5271).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Deliberately not the same shape as <see cref="LendableAdapterPool" />, and
///         deliberately not mirrored.</b> <c>LendableAdapterPool</c> is what gets copied into a
///         borrower's database, and it stops at the fields a borrower needs to identify the pool and
///         judge availability — chart, sizing and scale-up policy are the lender's deployment detail
///         and have no business being written into another tenant. This record is the answer to a
///         question asked LIVE instead: the caller gets the lender's current values, nothing is
///         persisted anywhere, and a stale mirror can never present itself as the pool's shape.
///     </para>
///     <para>
///         🔴 Every value is materialised off the CK entity before the lender's session closes, for
///         the same reason <c>GetAdapterPoolsForMirroringAsync</c> materialises: <c>IAttributeValueList</c>
///         is a live view bound to that session.
///     </para>
///     <para>
///         <see cref="Scope" /> is carried so the caller can run <c>MayLendAsync</c> against the
///         lender's OWN pool. Reading the pool is not permission to see it — the endpoint answers
///         404 for a pool that does not lend to the asking tenant, exactly as the lease path refuses
///         one.
///     </para>
/// </remarks>
/// <param name="LenderTenantId">The tenant that owns the pool.</param>
/// <param name="AdapterPoolRtId">The pool's RtId inside that tenant, as a bare 24-char hex string.</param>
/// <param name="Name">The pool's display name.</param>
/// <param name="Description">Optional description.</param>
/// <param name="Scope">Sharing mode and allow-list — what <c>MayLendAsync</c> decides against.</param>
/// <param name="MaxConcurrentLeasesPerTenant">
///     How many members one borrowing tenant may hold at once; null means unbounded. The one lending
///     value that describes the CALLER's own ceiling rather than the pool's size.
/// </param>
/// <param name="MinReplicas">Lower bound of the replica range — how much capacity stays warm.</param>
/// <param name="MaxReplicas">Upper bound; once reached, the queue grows instead.</param>
/// <param name="PoolMemberCpuRequest">Kubernetes CPU request of each member, e.g. "200m"; null = chart default.</param>
/// <param name="PoolMemberCpuLimit">Kubernetes CPU limit of each member, e.g. "1"; null = chart default.</param>
/// <param name="PoolMemberMemoryRequest">Kubernetes memory request of each member, e.g. "512Mi"; null = chart default.</param>
/// <param name="PoolMemberMemoryLimit">Kubernetes memory limit of each member, e.g. "1Gi"; null = chart default.</param>
/// <param name="ScaleUpPolicy">Which queue signal starts another member — the numeric key of the PoolScaleUpPolicy CK enum.</param>
/// <param name="ScaleUpQueueDepthThreshold">Waiting items that start another member, when the policy includes the depth signal.</param>
/// <param name="ScaleUpQueueWaitSeconds">How long the oldest waiting item may wait, when the policy includes the wait signal.</param>
/// <param name="ChartName">Helm chart the members run — the "which adapter is this" answer.</param>
/// <param name="ChartVersion">Version of that chart.</param>
/// <param name="DeploymentState">
///     Deployment state of the pool. The field that explains a queue which never drains: a pool that
///     is not deployed cannot serve a lease.
/// </param>
/// <param name="StatusMessage">The lender's last deployment status line, when it set one.</param>
public sealed record AdapterPoolDetails(
    string LenderTenantId,
    string AdapterPoolRtId,
    string Name,
    string? Description,
    LendingScope Scope,
    int? MaxConcurrentLeasesPerTenant,
    int MinReplicas,
    int MaxReplicas,
    string? PoolMemberCpuRequest,
    string? PoolMemberCpuLimit,
    string? PoolMemberMemoryRequest,
    string? PoolMemberMemoryLimit,
    int ScaleUpPolicy,
    int ScaleUpQueueDepthThreshold,
    int ScaleUpQueueWaitSeconds,
    string? ChartName,
    string? ChartVersion,
    int DeploymentState,
    string? StatusMessage);
