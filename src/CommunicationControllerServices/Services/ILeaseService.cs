using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     What a caller asks for when it wants a pool member (AB#4924).
/// </summary>
/// <param name="BorrowerTenantId">The tenant whose work is to be executed.</param>
/// <param name="BorrowerAdapterRtId">
///     RtId of the borrowing tenant's <c>Adapter</c> — the one whose <c>LifecycleMode</c> is
///     <c>Leased</c>. Its <c>LentFromTenantId</c> / <c>LentFromAdapterPoolRtId</c> pair is what makes the
///     borrowing relationship the borrower's own declaration rather than something the lender can
///     impose.
/// </param>
/// <param name="ExecutionId">
///     The pipeline execution the lease serves, when there is one. Empty while leasing is driven by
///     hand; the scheduler always sets it (increment 7).
/// </param>
/// <param name="Ttl">
///     How long the lease is valid. Null takes <see cref="ILeaseService.DefaultLeaseTtl" />.
/// </param>
/// <param name="PipelineRtId">
///     The pipeline the member is to run (AB#4924 §9.9 / D4). Null on a hand-driven lease, which
///     carries no work; the scheduler always names it, because it — not the member — decided which
///     queued item this lease serves.
/// </param>
/// <param name="PipelineInput">
///     The queued work item's input, carried onto the lease verbatim. Null when the trigger supplied
///     none.
/// </param>
/// <param name="Caller">
///     The invoker the work item was queued for (AB#5279), carried onto the lease so the member runs
///     the pipeline as that principal. Null when the item was queued without one.
/// </param>
/// <param name="CallerAccessToken">
///     The invoker's token as stored on the queued execution — encrypted at rest. The lease service
///     decrypts it and drops it when it has expired before it reaches the lease.
/// </param>
public record LeaseRequest(
    string BorrowerTenantId,
    OctoObjectId BorrowerAdapterRtId,
    string? ExecutionId = null,
    TimeSpan? Ttl = null,
    OctoObjectId? PipelineRtId = null,
    string? PipelineInput = null,
    ExecutePipelineCaller? Caller = null,
    string? CallerAccessToken = null);

/// <summary>
///     Outcome of a lease attempt (AB#4924).
/// </summary>
/// <param name="Granted">Whether a member is now holding the lease.</param>
/// <param name="LeaseId">Identifier of the granted lease. Null when nothing was granted.</param>
/// <param name="MemberId">
///     The member that took it — the value the borrower's execution records as
///     <c>LeasedOnMemberId</c>. Null when nothing was granted.
/// </param>
/// <param name="StatusMessage">Why nothing was granted, or a note on what was.</param>
/// <param name="Reason">
///     🔴 The <b>machine-readable</b> half of the refusal (AB#4924 increment 9, plan §11).
///     <see cref="StatusMessage" /> names the tenant, the adapter and the pool so a human can act,
///     and that is exactly what makes it useless as a metric label — it would produce one series per
///     name that ever appeared in a message. The enum is what <c>octo.lease.refused.count</c> is
///     tagged with. Keeping both is what lets a dashboard say "refusals are up, and they are all
///     pipeline projection failures" while the log line still says which borrower.
/// </param>
public record LeaseGrantResult(bool Granted, string? LeaseId, string? MemberId, string? StatusMessage,
    LeaseRefusalReason Reason = LeaseRefusalReason.None)
{
    /// <summary>A refusal with a named reason. Never a bare false — a caller has to be able to act.</summary>
    public static LeaseGrantResult Refused(LeaseRefusalReason reason, string message) =>
        new(false, null, null, message, reason);
}

/// <summary>
///     Grants and releases leases of adapter-pool members (AB#4924, concept §4).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>There is no queue here, deliberately.</b> Increment 6 ships the wire contract: a
///         lease can be granted, routed to a member, executed and released. Deciding <i>which</i>
///         borrower goes next — round-robin across tenants, interactive before batch within a tenant's
///         turn, the per-tenant concurrency cap — is increment 7, and building half of it here would
///         produce a second scheduling policy nobody intended to write. A request that finds no idle
///         member is refused with a reason; it is not parked.
///     </para>
///     <para>
///         <b>What this service does own</b> is the part that must not be duplicated later: resolving
///         whether the borrowing relationship is real (the borrower's own declaration, intersected
///         with the lender's sharing scope), producing the borrower's credential, and keeping the
///         controller's view of who holds what consistent with the members'.
///     </para>
/// </remarks>
public interface ILeaseService
{
    /// <summary>
    ///     How long a lease is valid when the caller names no TTL. Long enough for a normal work item
    ///     including its warm-up, short enough that a member that died without releasing is reclaimed
    ///     within one operator's attention span (concept §6, "release never arrives").
    /// </summary>
    public static readonly TimeSpan DefaultLeaseTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Grants a lease on a member of <paramref name="poolRtId" /> in
    ///     <paramref name="lenderTenantId" /> to the borrower named in <paramref name="request" />.
    /// </summary>
    /// <param name="lenderTenantId">Tenant that owns the pool.</param>
    /// <param name="poolRtId">RtId of the <c>AdapterPool</c> in that tenant.</param>
    /// <param name="request">Which borrower, which adapter, for how long.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="admissionGate">
    ///     <para>
    ///         Optional last check, run <b>after</b> an idle member has been reserved and
    ///         <b>before</b> the lease is pushed to it. Returning <c>false</c> undoes the reservation
    ///         and refuses the grant.
    ///     </para>
    ///     <para>
    ///         🔴 That position in the sequence is the whole point, and it is why this is a seam
    ///         rather than something the caller does around this method. The scheduler (increment 7)
    ///         uses it to take the work item out of the queue: the claim needs the member id, which
    ///         does not exist until a member is reserved, and it must land before the member is
    ///         handed the lease, because a second controller pod claiming the same work item first
    ///         has to be able to stop this one from dispatching it.
    ///     </para>
    /// </param>
    Task<LeaseGrantResult> GrantLeaseAsync(string lenderTenantId, OctoObjectId poolRtId,
        LeaseRequest request, CancellationToken cancellationToken = default,
        Func<LeaseDto, PoolMemberConnection, CancellationToken, Task<bool>>? admissionGate = null);

    /// <summary>
    ///     Applies everything a release implies on the borrower's execution: <c>LeaseReleasedAt</c>
    ///     always, and a terminal status when nothing else has completed the execution yet
    ///     (AB#4924 §9.1).
    /// </summary>
    Task ApplyLeaseOutcomeAsync(LeaseDto lease, bool success, string? statusMessage,
        string? outputData = null);

    /// <summary>
    ///     Marks the attempt a lease was serving <c>Interrupted</c>, stamps <c>LeaseReleasedAt</c>
    ///     on it, and enqueues a fresh attempt in its place (AB#4924 §9.3, concept §6).
    /// </summary>
    /// <remarks>
    ///     At-least-once, as the concept states: the contract with pipeline authors — idempotency —
    ///     is unchanged from today's adapter disconnect path. The retry is a <b>new</b> execution
    ///     entity; see <see cref="Repository.InterruptedLeasedExecution" /> for why it cannot be the
    ///     old one moved back to <c>Queued</c>.
    /// </remarks>
    /// <param name="lease">The lease that ended without a release.</param>
    /// <param name="interruptReason">
    ///     Which of the two mid-lease failures this is. Separate from <paramref name="reason" />
    ///     because that one is prose for the tenant's event log and this one is the metric label
    ///     (AB#4924 increment 9).
    /// </param>
    /// <param name="reason">Human-readable explanation, stored on the interrupted attempt.</param>
    /// <returns>The execution id of the re-queued attempt, or null when nothing was re-queued.</returns>
    Task<string?> InterruptAndRequeueAsync(LeaseDto lease, LeaseInterruptReason interruptReason, string reason);

    /// <summary>
    ///     Tells a member to drain: it finishes what it holds, takes no further lease and exits, so
    ///     the pool workload replaces it with a fresh process.
    /// </summary>
    /// <remarks>
    ///     🔴 Concept §6: a member whose lease expired server-side is drained and restarted rather
    ///     than re-used, because its post-lease cleanliness is unproven. That is a state change, not
    ///     a logged shrug — the member is marked draining locally even if the push fails, so this
    ///     controller never hands it another tenant.
    /// </remarks>
    Task DrainMemberAsync(string connectionId, string reason);

    /// <summary>
    ///     Applies a member's release. A release naming a lease the member no longer holds is stale
    ///     and is ignored.
    /// </summary>
    /// <remarks>
    ///     🔴 AB#5864 — a <c>Drained</c> release marks the member draining in the registry (even when
    ///     the release itself is stale: the member's drain flag never resets), so it is granted no
    ///     further lease. A <c>Drained</c> release that never ran its work item (no
    ///     <c>WorkDurationMs</c>) returns the work to the queue at its original position instead of
    ///     failing it, bounded by <c>MaxRefusedLeaseRequeues</c>; one that did run reports that work
    ///     item's own outcome.
    /// </remarks>
    /// <param name="connectionId">The connection the release arrived on.</param>
    /// <param name="result">The member's report.</param>
    /// <param name="connectionTenantId">
    ///     The tenant the connection's token was issued for, if any — checked against the lending
    ///     tenant when the release is not covered by a lease this connection holds (AB#5826).
    /// </param>
    /// <param name="requireConnectionTenant">
    ///     Whether the pool hub gate enforces; then such a release is refused without a matching
    ///     tenant-bound token, otherwise a mismatch is only logged.
    /// </param>
    /// <remarks>
    ///     🔴 AB#5826 — a release this connection does not hold is no longer dropped silently. It is
    ///     attributed to a lease held back after its member disconnected, to the member's previous
    ///     connection, or — after a controller restart — to the persisted execution it names, when that
    ///     execution is still running and was leased to this member of this pool. Otherwise it is
    ///     logged and ignored.
    /// </remarks>
    Task ReleaseLeaseAsync(string connectionId, LeaseResultDto result, string? connectionTenantId = null,
        bool requireConnectionTenant = false);

    /// <summary>
    ///     Handles a member that vanished. When it held a lease the work item is interrupted: concept
    ///     §6 makes this at-least-once, so the contract with pipeline authors — idempotency — is
    ///     unchanged from today's adapter disconnect path.
    /// </summary>
    /// <remarks>
    ///     🔴 AB#5826 — with a reconnect grace configured
    ///     (<c>CommunicationController:LeaseMemberReconnectGraceSeconds</c>) the lease is held back
    ///     instead of re-queued at once: the member usually keeps running and comes back within
    ///     seconds, and re-queuing ran the work twice. See <see cref="SweepLostLeasesAsync" />,
    ///     <see cref="ResumeLeaseAsync" /> and <see cref="ReleaseLostLeasesOfMemberAsync" /> for the
    ///     three ways a held-back lease ends.
    /// </remarks>
    Task HandleMemberDisconnectedAsync(PoolMemberConnection member);

    /// <summary>
    ///     Interrupts and re-queues every held-back lease whose reconnect grace has passed (AB#5826).
    ///     Called on every scheduling round.
    /// </summary>
    /// <returns>How many leases were interrupted.</returns>
    Task<int> SweepLostLeasesAsync(DateTime nowUtc);

    /// <summary>
    ///     A member registered again <b>without</b> a lease: every lease held back for it (except
    ///     <paramref name="exceptLeaseId" />) is interrupted and re-queued at once (AB#5826).
    /// </summary>
    /// <remarks>
    ///     The member reports outcomes it could not deliver before it registers, so whatever is still
    ///     held back at this point is work it does not have any more — a process that restarted in
    ///     place under the same member id, for instance. Waiting out the grace would only delay the
    ///     retry.
    /// </remarks>
    Task<int> ReleaseLostLeasesOfMemberAsync(string memberId, string adapterPoolTenantId, string adapterPoolRtId,
        string? exceptLeaseId = null);

    /// <summary>
    ///     Decides what a member that registers while still running a lease is recorded as (AB#5826).
    /// </summary>
    /// <param name="memberId">The registering member.</param>
    /// <param name="adapterPoolTenantId">Its pool's (lending) tenant.</param>
    /// <param name="adapterPoolRtId">Its pool.</param>
    /// <param name="activeLease">What the member says it is running.</param>
    /// <param name="transferredLease">
    ///     The lease the registry still held under the member's previous connection, when the
    ///     registration superseded one.
    /// </param>
    /// <returns>
    ///     The lease to record the member as busy with, and whether it was adopted — i.e. whether the
    ///     member's release will complete the borrower's execution.
    /// </returns>
    Task<LeaseResumption> ResumeLeaseAsync(string memberId, string adapterPoolTenantId, string adapterPoolRtId,
        PoolMemberActiveLeaseDto activeLease, LeaseDto? transferredLease = null);
}

/// <summary>
///     The outcome of <see cref="ILeaseService.ResumeLeaseAsync" /> (AB#5826).
/// </summary>
/// <param name="Lease">
///     The lease the member is recorded as busy with. When <paramref name="Adopted" /> is false it
///     names no execution, so its release frees the member without touching the borrower's work.
/// </param>
/// <param name="Adopted">Whether the member's release will complete the execution.</param>
/// <param name="StatusMessage">Why the lease was not adopted; null when it was.</param>
public record LeaseResumption(LeaseDto Lease, bool Adopted, string? StatusMessage);
