using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     One entry of an adapter pool's queue as the three surfaces see it (AB#4924 §10).
/// </summary>
/// <remarks>
///     🔴 <b>There is no global rank here, and there cannot be one.</b> Round-robin serves tenants in
///     turns, so "you are 7th" is not a fact about this queue. What is a fact, and what the surfaces
///     must show instead, is a pair: where the item sits inside <i>its own</i> tenant's queue, and how
///     many other tenants take a turn before that tenant's next one (concept §5, "Fairness").
/// </remarks>
/// <param name="ExecutionId">The borrower's execution id.</param>
/// <param name="BorrowerTenantId">Tenant whose work this is.</param>
/// <param name="PipelineRtId">Pipeline to run, when the edge resolves.</param>
/// <param name="PipelineName">Display name of that pipeline.</param>
/// <param name="ExecutionClass">0 Interactive, 1 Batch — see <see cref="Repository.QueuedExecution" />.</param>
/// <param name="QueuedAtUtc">When the item entered the queue.</param>
/// <param name="PositionInTenant">1-based position inside this tenant's queue; 0 for an item already leased.</param>
/// <param name="TenantsAheadInRotation">
///     How many other tenants with queued work are served before this tenant's next turn. 0 for an
///     item already leased.
/// </param>
/// <param name="LeasedOnMemberId">
///     The member holding this item, when it is leased; null while it waits. A member id may name a
///     process that no longer exists — nothing enforces referential integrity on it (implementation
///     plan §13.1) — so a surface must render it as text, never resolve it.
/// </param>
/// <param name="LeaseExpiresAtUtc">When the holding lease expires; null while the item waits.</param>
public sealed record AdapterPoolQueueEntry(
    string ExecutionId,
    string BorrowerTenantId,
    string? PipelineRtId,
    string? PipelineName,
    int ExecutionClass,
    DateTime QueuedAtUtc,
    int PositionInTenant,
    int TenantsAheadInRotation,
    string? LeasedOnMemberId,
    DateTime? LeaseExpiresAtUtc);

/// <summary>
///     Outcome of cancelling one queue entry (AB#4924 §9.5).
/// </summary>
public enum QueueCancellationResult
{
    /// <summary>The entry was waiting and is now <c>Cancelled</c>.</summary>
    Cancelled,

    /// <summary>No such execution in any tenant borrowing from this pool.</summary>
    NotFound,

    /// <summary>
    ///     The execution exists but already holds a lease. Interrupting a running pipeline is a
    ///     different operation and follows the existing cancellation path — the caller is told which
    ///     of the two it is looking at rather than having the distinction collapsed into one verb.
    /// </summary>
    AlreadyLeased
}

/// <summary>
///     The pool's queue and the scheduler that drains it (AB#4924, increment 7, concept §5).
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>The queue belongs to the pool, not to the member.</b> Pool membership is elastic; a
///         queue held by a member would strand its work the moment that member is drained. The
///         per-adapter view — "what is this member holding, what waits behind it" — is a projection of
///         the pool queue (<c>LeasedOnMemberId</c> carries the member), never a collection of its own.
///     </para>
///     <para>
///         🔴 <b>Manual adapters have no queue.</b> They execute immediately, exactly as today.
///         <c>Queued</c> is a pool-only state and the asymmetry between the two kinds of adapter is
///         intended — it reflects a real difference, not an inconsistency to paper over.
///     </para>
///     <para>
///         <b>Fairness is round-robin across tenants, never global FIFO.</b> Global FIFO lets one
///         tenant with 200 queued jobs starve every other, which is the displacement problem this
///         design exists to remove. Within one tenant's turn, work is ordered
///         <c>Interactive</c> before <c>Batch</c>; that ordering never reaches across tenants, because
///         it would reintroduce exactly the starvation the rotation prevents.
///     </para>
/// </remarks>
public interface ILeaseSchedulerService
{
    /// <summary>
    ///     Runs one scheduling round over every adapter pool that has borrowers with queued work.
    /// </summary>
    /// <returns>Number of leases granted in this round.</returns>
    Task<int> RunSchedulingRoundAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Reclaims leases whose TTL has expired: the attempt becomes <c>Interrupted</c> with its
    ///     lease span closed, a fresh attempt is enqueued, and the member is <b>drained and
    ///     restarted, not re-used</b> — concept §6, because its post-lease cleanliness is unproven.
    /// </summary>
    /// <returns>Number of leases reclaimed.</returns>
    Task<int> ReapExpiredLeasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     The queue of one pool: everything waiting, plus everything this controller instance
    ///     currently has leased out of it.
    /// </summary>
    Task<IReadOnlyList<AdapterPoolQueueEntry>> GetQueueAsync(string lenderTenantId, OctoObjectId poolRtId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cancels one waiting entry of this pool's queue.
    /// </summary>
    Task<QueueCancellationResult> CancelQueuedExecutionAsync(string lenderTenantId, OctoObjectId poolRtId,
        string executionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Drops the cached borrower topology of one pool, so the next round sees an adapter that was
    ///     just re-pointed, created or deleted.
    /// </summary>
    void InvalidateTopology();
}
