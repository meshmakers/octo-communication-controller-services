using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     Everything a BORROWING tenant may know about an adapter pool lent to it —
///     <c>GET {borrowerTenantId}/v1/adapterPool/lent/{lenderTenantId}/{adapterPoolRtId}</c> (AB#5271).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Read-only, and live rather than mirrored.</b> The borrower's <c>LentAdapterPool</c>
///         entity carries only what it needs to identify a pool and judge availability; chart,
///         sizing and scale-up policy are the lender's deployment detail and are deliberately not
///         copied into another tenant's database (see <c>LendableAdapterPool</c>). They are served
///         from here instead, per request, so a borrower reads the lender's current values and a
///         stale mirror can never present itself as the pool's shape.
///     </para>
///     <para>
///         🔴 <b>Declared here and not in <c>Communication.Contracts</c>, deliberately.</b> The only
///         consumer today is the Refinery Studio talking to the controller over plain HTTP, the same
///         interim arrangement <c>AdapterPoolMemberDto</c> and <c>ServiceAccountHealthDto</c> live
///         under. Putting it in the contracts package would make every change to this screen a
///         package lift plus a version cascade through every service that takes the package, for a
///         type no .NET client consumes yet. Move it there when a typed client needs it, together
///         with <c>AdapterPoolMemberDto</c>.
///     </para>
/// </remarks>
public class LentAdapterPoolDetailsDto
{
    /// <summary>The tenant that owns the pool.</summary>
    public string LenderTenantId { get; set; } = string.Empty;

    /// <summary>The pool's RtId inside the lending tenant — NOT the RtId of the borrower's mirror.</summary>
    public string AdapterPoolRtId { get; set; } = string.Empty;

    /// <summary>The pool's display name, as the lender has it right now.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional description.</summary>
    public string? Description { get; set; }

    /// <summary>Helm chart the members run — the "which adapter is this" answer.</summary>
    public string? ChartName { get; set; }

    /// <summary>Version of that chart.</summary>
    public string? ChartVersion { get; set; }

    /// <summary>
    ///     Deployment state of the pool (numeric key of the <c>DeploymentState</c> CK enum). The
    ///     field that explains a queue which never drains: a pool that is not deployed cannot serve
    ///     a lease.
    /// </summary>
    public int DeploymentState { get; set; }

    /// <summary>The lender's last deployment status line, when it set one.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Lower bound of the replica range — how much capacity stays warm.</summary>
    public int MinReplicas { get; set; }

    /// <summary>Upper bound. Once reached the pool stops growing and the queue grows instead.</summary>
    public int MaxReplicas { get; set; }

    /// <summary>Kubernetes CPU request of each member, e.g. "200m". Null means the chart default.</summary>
    public string? PoolMemberCpuRequest { get; set; }

    /// <summary>Kubernetes CPU limit of each member, e.g. "1". Null means the chart default.</summary>
    public string? PoolMemberCpuLimit { get; set; }

    /// <summary>Kubernetes memory request of each member, e.g. "512Mi". Null means the chart default.</summary>
    public string? PoolMemberMemoryRequest { get; set; }

    /// <summary>Kubernetes memory limit of each member, e.g. "1Gi". Null means the chart default.</summary>
    public string? PoolMemberMemoryLimit { get; set; }

    /// <summary>Which queue signal starts another member — numeric key of the <c>PoolScaleUpPolicy</c> CK enum.</summary>
    public int ScaleUpPolicy { get; set; }

    /// <summary>Waiting items that start another member, when the policy includes the depth signal.</summary>
    public int ScaleUpQueueDepthThreshold { get; set; }

    /// <summary>How long the oldest waiting item may wait, when the policy includes the wait signal.</summary>
    public int ScaleUpQueueWaitSeconds { get; set; }

    /// <summary>Numeric key of the <c>AdapterSharingMode</c> CK enum the pool lends under.</summary>
    public int SharingMode { get; set; }

    /// <summary>
    ///     How many members ONE borrowing tenant may hold at once; null means unbounded. The one
    ///     lending value that describes the caller's own ceiling rather than the pool's size.
    /// </summary>
    /// <remarks>
    ///     The allow-list behind the sharing mode is deliberately NOT here. A borrower has already
    ///     been told the answer that concerns it — it is reading this — and the list names the
    ///     lender's other customers.
    /// </remarks>
    public int? MaxConcurrentLeasesPerTenant { get; set; }

    /// <summary>Live capacity: how many members are serving this pool.</summary>
    public LentAdapterPoolMembersDto Members { get; set; } = new();

    /// <summary>The queue, as much of it as this tenant may see.</summary>
    public LentAdapterPoolQueueDto Queue { get; set; } = new();
}

/// <summary>
///     Aggregate live capacity of a lent adapter pool (AB#5271).
/// </summary>
/// <remarks>
///     ⚠️ <b>Per controller instance, not per cluster.</b> A member's SignalR connection lives on
///     exactly one controller pod, so these counts are what <i>that</i> pod can reach — the same
///     property <c>IOperatorConnectionManager</c> has. With more than one replica the answer is
///     partial by construction, which is why <see cref="IsPartialView" /> travels with it instead of
///     being a footnote somebody has to remember.
///
///     <para>
///     🔴 Counts only, never member ids. A member id plus its active lease tenant would tell a
///     borrower which of the lender's other customers is running right now.
///     </para>
/// </remarks>
public class LentAdapterPoolMembersDto
{
    /// <summary>Members connected to this controller instance.</summary>
    public int Connected { get; set; }

    /// <summary>Of those, how many currently hold a lease — for any tenant, not only the caller's.</summary>
    public int Busy { get; set; }

    /// <summary>Of those, how many were told to drain and will take no further lease.</summary>
    public int Draining { get; set; }

    /// <summary>
    ///     Always <c>true</c> today: see the per-instance caveat on this type. It is a field rather
    ///     than a constant so a future cluster-wide member registry can answer <c>false</c> without
    ///     a wire change, and so a surface has something to render the caveat from.
    /// </summary>
    public bool IsPartialView { get; set; } = true;
}

/// <summary>
///     What a borrowing tenant may see of a lent pool's queue (AB#5271).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The caller's own entries in full, everyone else's as a count.</b> A pool's queue
///         spans tenant databases — the pool belongs to the lender and every entry to some borrower
///         — so serving the lender's whole queue here would hand one borrower the execution ids,
///         pipeline names and tenant ids of the lender's other customers. The aggregate numbers say
///         everything a borrower legitimately needs ("how much is ahead of me") and nothing about
///         who is in front.
///     </para>
///     <para>
///         🔴 There is still no global rank, for the reason <c>AdapterPoolQueueEntryDto</c> gives:
///         round-robin has none to report.
///     </para>
/// </remarks>
public class LentAdapterPoolQueueDto
{
    /// <summary>
    ///     This tenant's own entries — waiting and leased — with the full
    ///     <c>AdapterPoolQueueEntryDto</c> shape every other surface uses.
    /// </summary>
    public IList<AdapterPoolQueueEntryDto> MyEntries { get; set; } = new List<AdapterPoolQueueEntryDto>();

    /// <summary>Of those, how many are still waiting for a lease.</summary>
    public int MyWaitingCount { get; set; }

    /// <summary>Of those, how many are already running on a member.</summary>
    public int MyLeasedCount { get; set; }

    /// <summary>When this tenant's oldest waiting item entered the queue; null when none waits.</summary>
    public DateTime? MyOldestQueuedAtUtc { get; set; }

    /// <summary>Work items of OTHER tenants waiting for a lease. A count, never their identities.</summary>
    public int OtherTenantsWaitingCount { get; set; }

    /// <summary>Work items of other tenants currently running on a member.</summary>
    public int OtherTenantsLeasedCount { get; set; }

    /// <summary>
    ///     How many OTHER tenants currently have work in this pool. With round-robin this is the
    ///     number that actually governs how long the caller waits — each of them takes a turn.
    /// </summary>
    public int OtherTenantsInRotation { get; set; }
}
