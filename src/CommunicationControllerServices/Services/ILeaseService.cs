using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     What a caller asks for when it wants a pool member (AB#4924).
/// </summary>
/// <param name="BorrowerTenantId">The tenant whose work is to be executed.</param>
/// <param name="BorrowerAdapterRtId">
///     RtId of the borrowing tenant's <c>Adapter</c> — the one whose <c>LifecycleMode</c> is
///     <c>Leased</c>. Its <c>LentFromTenantId</c> / <c>LentFromPoolRtId</c> pair is what makes the
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
public record LeaseRequest(
    string BorrowerTenantId,
    OctoObjectId BorrowerAdapterRtId,
    string? ExecutionId = null,
    TimeSpan? Ttl = null);

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
public record LeaseGrantResult(bool Granted, string? LeaseId, string? MemberId, string? StatusMessage)
{
    /// <summary>A refusal with a named reason. Never a bare false — a caller has to be able to act.</summary>
    public static LeaseGrantResult Refused(string reason) => new(false, null, null, reason);
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
    Task<LeaseGrantResult> GrantLeaseAsync(string lenderTenantId, OctoObjectId poolRtId,
        LeaseRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Applies a member's release. A release naming a lease the member no longer holds is stale
    ///     and is ignored.
    /// </summary>
    Task ReleaseLeaseAsync(string connectionId, LeaseResultDto result);

    /// <summary>
    ///     Handles a member that vanished. When it held a lease the work item is interrupted: concept
    ///     §6 makes this at-least-once, so the contract with pipeline authors — idempotency — is
    ///     unchanged from today's adapter disconnect path.
    /// </summary>
    Task HandleMemberDisconnectedAsync(PoolMemberConnection member);
}
