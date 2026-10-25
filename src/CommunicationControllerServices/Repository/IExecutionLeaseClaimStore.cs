using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>Outcome of <see cref="IExecutionLeaseClaimStore.TryClaimAsync" />.</summary>
internal enum LeaseClaimOutcome
{
    /// <summary>This caller holds the single-use claim now.</summary>
    Claimed,

    /// <summary>The claim exists already: it was taken before, by this or by another controller instance.</summary>
    AlreadyClaimed
}

/// <param name="Outcome">Whether the claim was taken.</param>
/// <param name="ClaimedAtUtc">When the existing claim was taken (only for <see cref="LeaseClaimOutcome.AlreadyClaimed" />).</param>
internal readonly record struct LeaseClaimResult(LeaseClaimOutcome Outcome, DateTime? ClaimedAtUtc = null);

/// <summary>
///     AB#7476 — the controller-owned record that a sealed queued execution was leased. It lives OUTSIDE the
///     <c>PipelineExecution</c> entity and outside every tenant runtime collection (a controller collection in the
///     system database), so no generic write path (asset-repo GraphQL, MCP generic tools, CK runtime API) can reset or
///     delete it.
/// </summary>
/// <remarks>
///     The identity of a claim is (tenant, claim id), where the claim id is a random value that lives inside the
///     authenticated payload of the sealed caller (<see cref="QueuedCaller" />). The claim is an atomic
///     insert-if-absent on the unique primary key, so any number of controller instances agree on one winner without a
///     read-then-write window.
/// </remarks>
internal interface IExecutionLeaseClaimStore
{
    /// <summary>
    ///     Takes the single-use claim <paramref name="claimId" /> of <paramref name="tenantId" />, atomically.
    ///     Throws when the store cannot be reached: the caller must then NOT grant the lease (fail closed).
    /// </summary>
    /// <param name="tenantId">The borrower tenant the execution belongs to.</param>
    /// <param name="claimId">The single-use identity from the sealed payload.</param>
    /// <param name="executionRtId">The execution the seal was written for (audit only).</param>
    /// <param name="nowUtc">The claim time.</param>
    /// <param name="retention">How long the record is kept (see the retention rule in the lease concept).</param>
    /// <param name="cancellationToken">Cancellation.</param>
    Task<LeaseClaimResult> TryClaimAsync(string tenantId, string claimId, OctoObjectId executionRtId,
        DateTime nowUtc, TimeSpan retention, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gives a claim back. Only for a claim whose lease was provably NOT delivered or started (the admission gate
    ///     declined or threw while the execution is still queued); never after the execution started running.
    /// </summary>
    Task ReleaseAsync(string tenantId, string claimId, CancellationToken cancellationToken = default);
}
