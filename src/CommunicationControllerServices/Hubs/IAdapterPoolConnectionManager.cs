using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;

/// <summary>
///     One registered pool member and what it is currently doing (AB#4924).
/// </summary>
/// <param name="ConnectionId">SignalR connection the member is reachable on.</param>
/// <param name="MemberId">
///     Stable identity of the member process across reconnects. Recorded on a borrower's execution as
///     <c>LeasedOnMemberId</c> — it is deliberately <b>not</b> an RtId: members are replicas of one
///     pool workload, not separate entities (implementation plan §12.1/§12.2).
/// </param>
/// <param name="PoolTenantId">The lending tenant that owns the pool.</param>
/// <param name="PoolRtId">RtId of the <c>AdapterPool</c> in <paramref name="PoolTenantId" />.</param>
/// <param name="ActiveLease">The lease the member currently holds, or null when it is idle.</param>
/// <param name="IsDraining">
///     Whether the member was told to drain. A draining member is never handed another lease; it
///     finishes what it holds and exits.
/// </param>
/// <param name="LastSeenUtc">When the member last registered or sent a heartbeat.</param>
public record PoolMemberConnection(
    string ConnectionId,
    string MemberId,
    string PoolTenantId,
    string PoolRtId,
    LeaseDto? ActiveLease,
    bool IsDraining,
    DateTime LastSeenUtc)
{
    /// <summary>Whether this member could take a lease right now.</summary>
    public bool IsAvailable => ActiveLease is null && !IsDraining;
}

/// <summary>
///     In-memory registry of the pool members connected to <b>this</b> controller instance
///     (AB#4924, concept §4).
/// </summary>
/// <remarks>
///     <para>
///         Deliberately in-memory and per-instance, exactly like <see cref="IOperatorConnectionManager" />:
///         a SignalR connection lives on one controller pod, so the pod that holds the connection is
///         the only one that can push a lease down it. A controller restart therefore forgets every
///         member, and the members re-register on reconnect — which is also why a member proposes its
///         own <c>MemberId</c> rather than being assigned one.
///     </para>
///     <para>
///         🔴 <b>The lease is held here, not on the member.</b> Concept §5 puts the queue on the pool
///         rather than on the member because membership is elastic; the same reasoning applies to the
///         lease bookkeeping. When a member disconnects mid-lease the controller still knows which
///         borrower's work was in flight, which is what makes the at-least-once re-queue of concept §6
///         possible at all.
///     </para>
/// </remarks>
public interface IAdapterPoolConnectionManager
{
    /// <summary>
    ///     Records a member as connected and eligible for leases. Replaces any previous registration
    ///     of the same connection.
    /// </summary>
    PoolMemberConnection RegisterMember(string connectionId, string memberId, string poolTenantId,
        string poolRtId);

    /// <summary>
    ///     Drops a connection and returns what it was holding, so the caller can re-queue an
    ///     interrupted work item. Returns null when the connection was never registered.
    /// </summary>
    PoolMemberConnection? RemoveMember(string connectionId);

    /// <summary>The member on this connection, or null when it never registered.</summary>
    PoolMemberConnection? TryGetMember(string connectionId);

    /// <summary>Every member currently registered for one pool.</summary>
    IReadOnlyCollection<PoolMemberConnection> GetMembers(string poolTenantId, string poolRtId);

    /// <summary>
    ///     Every member registered on this controller instance, across all pools. The lease reaper
    ///     (AB#4924 §9.3) needs this: an expired lease is found by walking what is held, not by
    ///     knowing which pool to ask about.
    /// </summary>
    IReadOnlyCollection<PoolMemberConnection> GetAllMembers();

    /// <summary>
    ///     Claims an idle member of the pool for <paramref name="lease" /> and returns it, or null
    ///     when every member is busy, draining or absent.
    /// </summary>
    /// <remarks>
    ///     Atomic against concurrent grants: two lease requests arriving at once must not both be
    ///     handed the same member. There is no queue here — a caller that gets null decides what to
    ///     do, and until increment 7 that decision is "tell the caller the pool is exhausted".
    /// </remarks>
    PoolMemberConnection? TryClaimMember(string poolTenantId, string poolRtId, LeaseDto lease);

    /// <summary>
    ///     Releases a lease previously claimed on this connection. Returns the released lease, or null
    ///     when the connection holds no lease or holds a different one — a stale release must never
    ///     free the lease the member was handed in the meantime.
    /// </summary>
    LeaseDto? ReleaseLease(string connectionId, string leaseId);

    /// <summary>Marks a member as draining; it will not be offered another lease.</summary>
    void MarkDraining(string connectionId);

    /// <summary>Records that a member is still alive.</summary>
    void Heartbeat(string connectionId, DateTime sampledAtUtc);
}
