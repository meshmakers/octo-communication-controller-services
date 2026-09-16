using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     AB#4924 §9.6 — the signal that lets a lease release or a work enqueue pull the next scheduling
///     round forward instead of waiting for the tick.
/// </summary>
/// <remarks>
///     What is worth pinning here is not "a wake wakes": it is the two properties that decide whether
///     this helps or hurts. A request must survive until somebody waits (otherwise a request racing a
///     round in progress is lost and the work sits until the tick anyway), and a burst must collapse
///     into ONE round (otherwise six executions enqueued in 320 ms — the shape the round-robin walk
///     produced — run six rounds, each reading the queue of every borrower of the pool).
/// </remarks>
internal class LeaseSchedulerWakeSignalTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Test]
    public async Task AWaiterWithNoRequestPendingTimesOut()
    {
        using var signal = new LeaseSchedulerWakeSignal();

        var requested = await signal.WaitForRoundAsync(NoWait, CancellationToken.None);

        await Assert.That(requested).IsFalse();
    }

    /// <summary>
    ///     🔴 The request is a pending permit, not an event. A release that lands while a round is
    ///     still running has nobody waiting at that instant; if it were dropped, the member it just
    ///     freed would stay idle until the tick — exactly the latency this feature removes.
    /// </summary>
    [Test]
    public async Task ARequestMadeBeforeAnyoneWaitsIsStillHonoured()
    {
        using var signal = new LeaseSchedulerWakeSignal();

        signal.RequestRound("a lease was released while the previous round was still running");
        var requested = await signal.WaitForRoundAsync(NoWait, CancellationToken.None);

        await Assert.That(requested).IsTrue();
    }

    /// <summary>
    ///     🔴 The coalescing property, stated as the burst that produced it: six executions enqueued
    ///     inside a third of a second must cost one extra round, not six.
    /// </summary>
    [Test]
    public async Task ABurstOfRequestsCollapsesIntoASingleRound()
    {
        using var signal = new LeaseSchedulerWakeSignal();

        for (var i = 0; i < 6; i++)
        {
            signal.RequestRound($"execution {i} enqueued");
        }

        var first = await signal.WaitForRoundAsync(NoWait, CancellationToken.None);
        var second = await signal.WaitForRoundAsync(NoWait, CancellationToken.None);

        await Assert.That(first).IsTrue();
        await Assert.That(second).IsFalse();
    }

    /// <summary>
    ///     After a round has been served, the next request must wake again — otherwise the signal
    ///     would work exactly once per process and the regression would look like "leasing is slow
    ///     again" weeks later.
    /// </summary>
    [Test]
    public async Task AFurtherRequestAfterAServedRoundWakesAgain()
    {
        using var signal = new LeaseSchedulerWakeSignal();

        signal.RequestRound("first");
        await signal.WaitForRoundAsync(NoWait, CancellationToken.None);

        signal.RequestRound("second");
        var requested = await signal.WaitForRoundAsync(NoWait, CancellationToken.None);

        await Assert.That(requested).IsTrue();
    }

    /// <summary>
    ///     Requesting a round on a disposed signal happens during shutdown — a member releasing its
    ///     lease while the host tears the scheduler down. It must not throw into a hub callback.
    /// </summary>
    [Test]
    public async Task RequestingARoundAfterDisposalIsIgnored()
    {
        var signal = new LeaseSchedulerWakeSignal();
        signal.Dispose();

        await Assert.That(() => signal.RequestRound("a lease released during shutdown")).ThrowsNothing();
    }
}
