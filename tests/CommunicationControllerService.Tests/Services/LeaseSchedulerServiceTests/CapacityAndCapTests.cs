using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#4924 §9.1/§9.4 — what bounds a borrower, and what happens when the pool runs out.
/// </summary>
internal class CapacityAndCapTests : LeaseSchedulerServiceTestsBase
{
    /// <summary>
    ///     <c>LendingMaxConcurrentLeasesPerTenant</c> caps CONCURRENCY, not queue depth: the excess
    ///     stays queued and visible rather than being rejected (concept §6, "A borrowed tenant floods
    ///     the queue").
    /// </summary>
    [Test]
    public async Task ThePerTenantCap_BlocksTheCappedTenantAndServesTheOthers()
    {
        ArrangePool(maxConcurrentLeasesPerTenant: 1);

        ArrangeQueue(TenantA, Entry("a-0", 600), Entry("a-1", 599));
        ArrangeQueue(TenantB, Entry("b-0", 5));

        // Tenant A already holds its one allowed lease.
        ArrangeBusyMember(TenantA);
        ArrangeMembers(2);

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(1);
        await Assert.That(Claimed.Select(c => c.TenantId).ToList()).IsEquivalentTo(new List<string> { TenantB });
        // A's work is untouched, not dropped.
        await Assert.That(Claimed.Any(c => c.TenantId == TenantA)).IsFalse();
    }

    /// <summary>
    ///     A pool that declares no cap is bounded by the rotation and its replica range alone
    ///     (concept §8 Q11 left it deliberately unset).
    /// </summary>
    [Test]
    public async Task WithoutACap_ATenantThatAlreadyHoldsALeaseStillGetsAnother()
    {
        ArrangePool();

        ArrangeQueue(TenantA, Entry("a-0", 600), Entry("a-1", 599));

        ArrangeBusyMember(TenantA);
        ArrangeMembers(1);

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(1);
        await Assert.That(Claimed[0].TenantId).IsEqualTo(TenantA);
    }

    /// <summary>
    ///     Concept §6, "Pool exhausted": the queue grows, the work is neither dropped nor rejected.
    /// </summary>
    [Test]
    public async Task AnExhaustedPool_GrowsTheQueueInsteadOfDroppingWork()
    {
        ArrangePool();

        ArrangeQueue(TenantA, Entry("a-0", 600), Entry("a-1", 599), Entry("a-2", 598));
        ArrangeQueue(TenantB, Entry("b-0", 5), Entry("b-1", 4));

        // Every member is busy for somebody else; none is available.
        ArrangeBusyMember(TenantC);

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(0);
        // Nothing left the queue: no execution was claimed, so every entry is still Queued.
        await Assert.That(Claimed).IsEmpty();
        await CommunicationRepository.DidNotReceive()
            .TryClaimQueuedExecutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LeaseClaim>());
    }

    /// <summary>
    ///     A pool with no connected members at all behaves identically — this is the ordinary state
    ///     of a pool whose members are connected to a different controller pod.
    /// </summary>
    [Test]
    public async Task NoMembersOnThisInstance_GrantsNothingAndKeepsTheQueue()
    {
        ArrangePool();
        ArrangeQueue(TenantA, Entry("a-0", 600));

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(0);
        await Assert.That(Claimed).IsEmpty();
    }

    /// <summary>
    ///     A work item another controller instance claimed first must not be dispatched here. The
    ///     admission gate is what makes that possible; this pins that the scheduler honours its answer
    ///     instead of counting the grant anyway.
    /// </summary>
    [Test]
    public async Task AWorkItemClaimedElsewhere_IsNotGrantedAndDoesNotConsumeTheRound()
    {
        ArrangePool();
        ArrangeQueue(TenantA, Entry("a-0", 600));
        ArrangeMembers(1);

        CommunicationRepository
            .TryClaimQueuedExecutionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<LeaseClaim>())
            .Returns(false);

        var granted = await Scheduler.RunSchedulingRoundAsync();

        // Undoing the member reservation is LeaseService's job and is pinned there
        // (GrantLeaseAsyncTests.ARefusedAdmissionGate_PutsTheMemberBack); what this test owns is
        // that the scheduler does not count a refused grant as one.
        await Assert.That(granted).IsEqualTo(0);
    }
}
