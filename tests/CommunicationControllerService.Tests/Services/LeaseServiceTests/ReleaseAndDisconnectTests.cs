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
        var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());
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
