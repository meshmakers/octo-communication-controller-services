using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#5826 — the reconnect grace of a disconnected member's lease ends on the scheduling cadence.
/// </summary>
/// <remarks>
///     The grace is seconds; the cleanup service runs every few minutes, so sweeping there would turn
///     a 90-second grace into up to five minutes of a borrower's work standing still.
/// </remarks>
internal class LostLeaseSweepTests : LeaseSchedulerServiceTestsBase
{
    [Test]
    public async Task EveryRound_SweepsTheHeldBackLeasesOfDisconnectedMembers()
    {
        await Scheduler.RunSchedulingRoundAsync();

        await LeaseService.Received(1).SweepLostLeasesAsync(Arg.Any<DateTime>());
    }

    /// <summary>A sweep that throws must not cost every pool its scheduling round.</summary>
    [Test]
    public async Task AFailingSweep_DoesNotStopTheRound()
    {
        LeaseService.SweepLostLeasesAsync(Arg.Any<DateTime>()).ThrowsAsync(new InvalidOperationException("boom"));

        var granted = await Scheduler.RunSchedulingRoundAsync();

        await Assert.That(granted).IsEqualTo(0);
    }
}
