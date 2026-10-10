using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.BackgroundServices;

/// <summary>
///     AB#4699 / AB#6418 — the adapter sweep judges only after the adapters had their quiet period
///     (<c>ReconciliationGrace</c>). On test-2 the cloud Mesh Adapters reconnected 72 seconds after the old
///     fixed 5 minute grace and were written Offline shortly before they registered again.
/// </summary>
internal class AdapterOfflineReconciliationTests
{
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly IAdapterConnectionTracker _tracker = Substitute.For<IAdapterConnectionTracker>();
    private readonly FixedTimeProvider _clock = new(new DateTime(2026, 10, 10, 15, 59, 49, DateTimeKind.Utc));
    private readonly AdapterOfflineReconciliationBackgroundService _service;

    public AdapterOfflineReconciliationTests()
    {
        _adapterCache.GetEnabledTenantIds().Returns(new[] { "t1", "t2" });
        _service = new AdapterOfflineReconciliationBackgroundService(_adapterCache, _adapterService, _tracker, _clock,
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()));
    }

    [After(Test)]
    public void DisposeService() => _service.Dispose();

    private void AdapterConnectedAfter(TimeSpan sinceStart) =>
        _tracker.LastConnectedAt.Returns(new DateTimeOffset(_clock.UtcNow + sinceStart, TimeSpan.Zero));

    [Test]
    public async Task Tick_NoAdapterConnectedYetAfterOneInterval_DoesNotJudge()
    {
        _clock.UtcNow += TimeSpan.FromMinutes(5);

        await _service.SweepIfSettledAsync();

        await _adapterService.DidNotReceiveWithAnyArgs().ReconcileOrphanedOnlineAdaptersAsync(default!);
    }

    [Test]
    public async Task Tick_AdaptersReconnectedJustAfterTheOldGrace_StillWaits()
    {
        // test-2: start 15:59:49, old grace over 16:04:49, cloud adapters register 16:06:01-02.
        AdapterConnectedAfter(TimeSpan.FromSeconds(372));
        _clock.UtcNow += TimeSpan.FromMinutes(10);

        await _service.SweepIfSettledAsync();

        await _adapterService.DidNotReceiveWithAnyArgs().ReconcileOrphanedOnlineAdaptersAsync(default!);
    }

    [Test]
    public async Task Tick_OneIntervalAfterTheLastAdapterConnection_Judges()
    {
        AdapterConnectedAfter(TimeSpan.FromSeconds(372));
        _clock.UtcNow += TimeSpan.FromMinutes(15);

        await _service.SweepIfSettledAsync();

        await _adapterService.Received(1).ReconcileOrphanedOnlineAdaptersAsync("t1");
        await _adapterService.Received(1).ReconcileOrphanedOnlineAdaptersAsync("t2");
    }

    [Test]
    public async Task Tick_NoAdapterEverConnects_JudgesAfterThreeIntervals()
    {
        _clock.UtcNow += TimeSpan.FromMinutes(15);

        await _service.SweepIfSettledAsync();

        await _adapterService.Received(1).ReconcileOrphanedOnlineAdaptersAsync("t1");
    }

    [Test]
    public async Task Sweep_FailingTenant_DoesNotStopTheOthers()
    {
        _adapterService.ReconcileOrphanedOnlineAdaptersAsync("t1").Returns(Task.FromException<int>(new InvalidOperationException("boom")));
        _clock.UtcNow += TimeSpan.FromMinutes(15);

        await _service.SweepIfSettledAsync();

        await _adapterService.Received(1).ReconcileOrphanedOnlineAdaptersAsync("t2");
    }
}
