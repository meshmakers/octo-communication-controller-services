using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#4924 §9.2 — the scheduler is round-robin across tenants, never global FIFO, and priority
///     lives strictly inside one tenant's turn.
/// </summary>
/// <remarks>
///     🔴 <b>Every test here is written so that it FAILS under a global-FIFO scheduler.</b> That is
///     the point: a fairness test that a global FIFO would also pass proves nothing, and a silent
///     regression to global FIFO is by far the most likely way this scheduler stops being fair. The
///     arrangement is therefore always the same shape — the starving tenant's work is <b>older</b>
///     than everybody else's, so arrival order and fair order disagree.
/// </remarks>
internal class FairnessTests : LeaseSchedulerServiceTestsBase
{
    /// <summary>
    ///     The displacement problem the whole design exists to remove: one tenant with 200 queued
    ///     jobs must not starve the others.
    /// </summary>
    [Test]
    public async Task OneTenantWith200QueuedItems_DoesNotStarveTheOthers()
    {
        ArrangePool();

        // Tenant A's 200 items are ALL older than anything the other two queued. Under global FIFO
        // the three available members would all go to A and neither B nor C would run today.
        var a = Enumerable.Range(0, 200)
            .Select(i => Entry($"a-{i:D3}", minutesAgo: 600 - i))
            .ToArray();
        ArrangeQueue(TenantA, a);
        ArrangeQueue(TenantB, Entry("b-0", minutesAgo: 1));
        ArrangeQueue(TenantC, Entry("c-0", minutesAgo: 1));

        ArrangeMembers(3);

        var granted = await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(granted).IsEqualTo(3);
        await Assert.That(Claimed.Select(c => c.TenantId).Order().ToList())
            .IsEquivalentTo(new List<string> { TenantA, TenantB, TenantC });
        await Assert.That(Claimed.Count(c => c.TenantId == TenantA)).IsEqualTo(1);
    }

    /// <summary>
    ///     With one member the rotation has to actually rotate: A, then B, then C, then A again.
    ///     A scheduler that always restarts at the first tenant serves A forever.
    /// </summary>
    [Test]
    public async Task ConsecutiveRounds_TakeTurnsInsteadOfServingTheSameTenantAgain()
    {
        ArrangePool();

        ArrangeQueue(TenantA, Entry("a-0", 600), Entry("a-1", 599), Entry("a-2", 598));
        ArrangeQueue(TenantB, Entry("b-0", 5), Entry("b-1", 4));
        ArrangeQueue(TenantC, Entry("c-0", 3));

        // One member, released again between rounds — the shape of a pool at MinReplicas = 1.
        ArrangeMembers(1);
        var connectionId = "conn-0";

        var served = new List<string>();
        for (var round = 0; round < 4; round++)
        {
            await Scheduler.RunSchedulingRoundAsync();
            served.Add(Claimed[^1].TenantId);

            var member = ConnectionManager.TryGetMember(connectionId)!;
            ConnectionManager.ReleaseLease(connectionId, member.ActiveLease!.LeaseId);
        }

        await Assert.That(served).IsEquivalentTo(new List<string> { TenantA, TenantB, TenantC, TenantA });
    }

    /// <summary>
    ///     Within one tenant's turn, Interactive goes first even though the Batch item is older.
    /// </summary>
    [Test]
    public async Task WithinOneTenantsTurn_InteractiveIsServedBeforeBatch()
    {
        ArrangePool();

        // The nightly batch queued at 02:00, the "generate billing" click at 09:15.
        ArrangeQueue(TenantA,
            Entry("nightly-batch", minutesAgo: 400, executionClass: QueuedExecution.BatchClass),
            Entry("billing-click", minutesAgo: 1, executionClass: QueuedExecution.InteractiveClass));

        ArrangeMembers(1);

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(Claimed).Count().IsEqualTo(1);
        await Assert.That(Claimed[0].ExecutionId).IsEqualTo("billing-click");
    }

    /// <summary>
    ///     🔴 And <b>not</b> across turns. This is the test that separates "priority inside a turn"
    ///     from "priority", and it is the one a class-first global sort fails.
    /// </summary>
    /// <remarks>
    ///     Tenant A is first in the rotation and has only Batch work; tenant B has an Interactive job.
    ///     The single member must go to A. If class ever reached across tenants, B would overtake A —
    ///     and a tenant could then starve every other simply by declaring its work interactive, which
    ///     is precisely the starvation round-robin exists to prevent.
    /// </remarks>
    [Test]
    public async Task AcrossTenants_InteractiveDoesNotOvertakeAnotherTenantsBatch()
    {
        ArrangePool();

        ArrangeQueue(TenantA, Entry("a-batch", minutesAgo: 10, executionClass: QueuedExecution.BatchClass));
        ArrangeQueue(TenantB,
            Entry("b-interactive", minutesAgo: 1, executionClass: QueuedExecution.InteractiveClass));

        ArrangeMembers(1);

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(Claimed).Count().IsEqualTo(1);
        await Assert.That(Claimed[0].TenantId).IsEqualTo(TenantA);
        await Assert.That(Claimed[0].ExecutionId).IsEqualTo("a-batch");
    }

    /// <summary>
    ///     A second pass of the same round hands out the members that are still free — again one per
    ///     tenant per pass, so the second member of a two-member pool does not simply go back to the
    ///     tenant that got the first.
    /// </summary>
    [Test]
    public async Task ASecondPassInTheSameRound_AlsoGoesRoundTheRotation()
    {
        ArrangePool();

        ArrangeQueue(TenantA, Entry("a-0", 600), Entry("a-1", 599), Entry("a-2", 598), Entry("a-3", 597));
        ArrangeQueue(TenantB, Entry("b-0", 5), Entry("b-1", 4));

        ArrangeMembers(4);

        await Scheduler.RunSchedulingRoundAsync();

        using var _ = Assert.Multiple();
        await Assert.That(Claimed).Count().IsEqualTo(4);
        // Two each, not four for A.
        await Assert.That(Claimed.Count(c => c.TenantId == TenantA)).IsEqualTo(2);
        await Assert.That(Claimed.Count(c => c.TenantId == TenantB)).IsEqualTo(2);
        // And inside each tenant, arrival order is preserved.
        await Assert.That(Claimed.Where(c => c.TenantId == TenantA).Select(c => c.ExecutionId).ToList())
            .IsEquivalentTo(new List<string> { "a-0", "a-1" });
    }
}
