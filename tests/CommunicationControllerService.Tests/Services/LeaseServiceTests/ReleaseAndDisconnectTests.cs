using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 increment 6 — what the controller does when a lease ends, cleanly or otherwise.
/// </summary>
internal class ReleaseAndDisconnectTests : LeaseServiceTestsBase
{
    private async Task<string> GrantAsync()
    {
        ArrangeGrantableLease();
        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, AdapterPoolRtId, ARequest());
        return result.LeaseId!;
    }

    [Test]
    public async Task ACleanRelease_FreesTheMember()
    {
        var leaseId = await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            ReleasedAtUtc = DateTime.UtcNow
        });

        using var _ = Assert.Multiple();
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
        await EventService.DidNotReceive().StoreErrorEventAsync(BorrowerTenantId, Arg.Any<string>());
    }

    /// <summary>
    ///     🔴 AB#4924 §9.6 — the member is free again, and the next grant must not wait for the
    ///     scheduler's tick. Without this the interval WAS the cadence: grants came 5.11–5.14 s apart
    ///     for runs of 0.43–1.36 s, so a member idled 75–92 % of the time and could not exceed roughly
    ///     twelve executions a minute however small the work was.
    /// </summary>
    [Test]
    public async Task ACleanRelease_PullsTheNextSchedulingRoundForward()
    {
        var leaseId = await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true,
            ReleasedAtUtc = DateTime.UtcNow
        });

        WakeSignal.Received(1).RequestRound(Arg.Any<string>());
    }

    /// <summary>
    ///     A failed work item frees the member just as a successful one does — the failure belongs to
    ///     the pipeline, not to the member — so the queue must move on at once rather than paying a
    ///     tick for somebody else's broken node.
    /// </summary>
    [Test]
    public async Task AFailedWorkItem_StillPullsTheNextSchedulingRoundForward()
    {
        var leaseId = await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Failed,
            Success = false,
            StatusMessage = "node 3 threw"
        });

        WakeSignal.Received(1).RequestRound(Arg.Any<string>());
    }

    /// <summary>
    ///     🔴 A DRAINED member is the one release that must not wake anybody. It takes no further
    ///     work — concept §6 drains and restarts a member whose post-lease cleanliness is unproven —
    ///     so a round on its account would read every borrower's queue and grant nothing.
    /// </summary>
    [Test]
    public async Task ADrainedRelease_DoesNotPullTheNextRoundForward()
    {
        var leaseId = await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Drained,
            Success = false,
            StatusMessage = "a participant failed to leave"
        });

        WakeSignal.DidNotReceive().RequestRound(Arg.Any<string>());
    }

    /// <summary>
    ///     A stale or unknown lease id frees nothing, so there is nothing to schedule for — and a
    ///     reconnecting member replaying its last release must not be able to drive rounds.
    /// </summary>
    [Test]
    public async Task AnUnknownLease_PullsNoRoundForward()
    {
        await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = "not-a-lease-this-member-holds",
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true
        });

        WakeSignal.DidNotReceive().RequestRound(Arg.Any<string>());
    }

    /// <summary>
    ///     A failed work item is still a clean release — the member unwound the lease itself, so the
    ///     failure belongs to the pipeline, not to the isolation invariant. It is recorded in the
    ///     <b>borrower's</b> event log, because it is the borrower's pipeline that failed.
    /// </summary>
    [Test]
    public async Task AFailedWorkItem_IsRecordedInTheBorrowersEventLog()
    {
        var leaseId = await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = leaseId,
            Reason = LeaseReleaseReasonDto.Failed,
            Success = false,
            StatusMessage = "node 3 threw"
        });

        using var _ = Assert.Multiple();
        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains("node 3 threw")));
        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsTrue();
    }

    /// <summary>
    ///     🔴 A stale release — one naming a lease the member no longer holds — must not free the
    ///     lease it holds now. That is the one way a second borrower could be let onto a process while
    ///     the first one is still running.
    /// </summary>
    [Test]
    public async Task AStaleRelease_DoesNotFreeTheCurrentLease()
    {
        await GrantAsync();

        await LeaseService.ReleaseLeaseAsync(ConnectionId, new LeaseResultDto
        {
            LeaseId = "a-lease-from-a-previous-life",
            Reason = LeaseReleaseReasonDto.Completed,
            Success = true
        });

        await Assert.That(ConnectionManager.TryGetMember(ConnectionId)!.IsAvailable).IsFalse();
    }

    /// <summary>
    ///     Concept §6: a member that vanished mid-lease leaves an interrupted work item. At-least-once
    ///     is the contract, so the borrower has to be able to see that its execution stopped — in its
    ///     own event log, not only in a controller pod's stdout.
    /// </summary>
    [Test]
    public async Task ADisconnectMidLease_IsReportedToTheBorrower()
    {
        await GrantAsync();
        var member = ConnectionManager.RemoveMember(ConnectionId)!;

        await LeaseService.HandleMemberDisconnectedAsync(member);

        await EventService.Received(1).StoreErrorEventAsync(BorrowerTenantId,
            Arg.Is<string>(m => m.Contains(MemberId) && m.Contains("interrupted")));
    }

    [Test]
    public async Task ADisconnectWithoutALease_ReportsNothing()
    {
        ArrangeConnectedMember();
        var member = ConnectionManager.RemoveMember(ConnectionId)!;

        await LeaseService.HandleMemberDisconnectedAsync(member);

        await EventService.DidNotReceive().StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }
}
