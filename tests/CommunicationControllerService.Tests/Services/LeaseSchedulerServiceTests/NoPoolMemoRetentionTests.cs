using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseSchedulerServiceTests;

/// <summary>
///     AB#5329 — the "names no usable pool" memo is swept on the cadence that WRITES it.
/// </summary>
/// <remarks>
///     <para>
///         The warning about a leased adapter that names no pool is produced by
///         <c>GetTopologyAsync</c>, which rebuilds on <c>LeaseTopologyRefreshSeconds</c> (60 s by
///         default). Its memo was first swept against <c>LeaseSchedulerIntervalSeconds</c> (5 s) like
///         the refusal memo beside it, so it had always expired by the time the topology looked
///         again: every observation read as a transition and warned, and the rate limit did nothing.
///     </para>
///     <para>
///         🔴 Measured, not reasoned: three consecutive scheduler rounds against a broken borrower
///         produced three WARNs on the local cluster, exactly as before the change. It is invisible
///         in any single round and in any test that runs two rounds back to back — both cadences
///         cover that — which is why this pins the derivation itself rather than an observed
///         interval.
///     </para>
/// </remarks>
internal class NoPoolMemoRetentionTests : LeaseSchedulerServiceTestsBase
{
    private LeaseSchedulerService Service => (LeaseSchedulerService)Scheduler;

    [Test]
    public async Task TheMemoOutlivesTheCadenceThatRefreshesIt()
    {
        Options.LeaseTopologyRefreshSeconds = 60;
        Options.LeaseSchedulerIntervalSeconds = 5;

        // The whole point: longer than one topology refresh, so an adapter that is still broken
        // refreshes its own memo before it can expire.
        await Assert.That(Service.NoPoolMemoRetentionSeconds())
            .IsGreaterThan(Options.LeaseTopologyRefreshSeconds);
    }

    /// <summary>
    ///     The defaults differ by a factor of twelve, so reading the scheduling tick instead of the
    ///     topology cadence is exactly the mistake that was made.
    /// </summary>
    [Test]
    public async Task ItIsDerivedFromTheTopologyCadenceAndNotTheSchedulingTick()
    {
        Options.LeaseTopologyRefreshSeconds = 600;
        Options.LeaseSchedulerIntervalSeconds = 5;
        var withSlowTopology = Service.NoPoolMemoRetentionSeconds();

        Options.LeaseTopologyRefreshSeconds = 60;
        var withFastTopology = Service.NoPoolMemoRetentionSeconds();

        using var _ = Assert.Multiple();
        await Assert.That(withSlowTopology).IsGreaterThan(withFastTopology);
        await Assert.That(withSlowTopology).IsGreaterThan(600);
    }

    /// <summary>
    ///     A deployment that rebuilds the topology more often than it schedules must not push the
    ///     window back below the scheduling tick's own.
    /// </summary>
    [Test]
    public async Task AFastTopologyNeverShortensItBelowTheSchedulingWindow()
    {
        Options.LeaseTopologyRefreshSeconds = 5;
        Options.LeaseSchedulerIntervalSeconds = 30;

        await Assert.That(Service.NoPoolMemoRetentionSeconds()).IsGreaterThan(30);
    }
}
