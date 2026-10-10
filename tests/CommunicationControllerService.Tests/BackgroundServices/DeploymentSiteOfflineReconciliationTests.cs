using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.BackgroundServices;

/// <summary>
///     AB#6418 — the sweep drives the per-tenant reconcile, stays out of the way during shutdown and
///     survives a failing tenant. It judges ownership only after the operators had their quiet period
///     (<c>ReconciliationGrace</c>): on test-2 the operators reconnected more than a minute after the
///     old fixed 5 minute grace and every Cloud site was written Offline shortly before it was claimed.
/// </summary>
internal class DeploymentSiteOfflineReconciliationTests
{
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly IDeploymentSiteService _deploymentSiteService = Substitute.For<IDeploymentSiteService>();
    private readonly IShutdownState _shutdownState = Substitute.For<IShutdownState>();
    private readonly IOperatorConnectionManager _operatorConnectionManager = Substitute.For<IOperatorConnectionManager>();
    private readonly FixedTimeProvider _clock = new(new DateTime(2026, 10, 10, 15, 59, 49, DateTimeKind.Utc));
    private readonly DeploymentSiteOfflineReconciliationBackgroundService _service;

    public DeploymentSiteOfflineReconciliationTests()
    {
        _adapterCache.GetEnabledTenantIds().Returns(new[] { "t1", "t2" });
        // Default interval / grace: 5 minutes.
        _service = new DeploymentSiteOfflineReconciliationBackgroundService(_adapterCache, _deploymentSiteService,
            _shutdownState, _operatorConnectionManager, _clock,
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()));
    }

    private void OperatorRegisteredAfter(TimeSpan sinceStart) =>
        _operatorConnectionManager.LastOperatorRegisteredAt.Returns(new DateTimeOffset(_clock.UtcNow + sinceStart, TimeSpan.Zero));

    [After(Test)]
    public void DisposeService() => _service.Dispose();

    [Test]
    public async Task Sweep_ReconcilesEveryEnabledTenant()
    {
        await _service.ReconcileAllTenantsAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t1");
        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t2");
    }

    [Test]
    public async Task Sweep_WhileShuttingDown_DoesNothing()
    {
        _shutdownState.IsShuttingDown.Returns(true);

        await _service.ReconcileAllTenantsAsync();

        await _deploymentSiteService.DidNotReceiveWithAnyArgs().ReconcileOrphanedOnlineDeploymentSitesAsync(default!);
    }

    [Test]
    public async Task Sweep_ShutdownBeginsAfterFirstTenant_SkipsTheRest()
    {
        _shutdownState.IsShuttingDown.Returns(false, false, true);

        await _service.ReconcileAllTenantsAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t1");
        await _deploymentSiteService.DidNotReceive().ReconcileOrphanedOnlineDeploymentSitesAsync("t2");
    }

    [Test]
    public async Task Sweep_FailingTenant_DoesNotStopTheOthers()
    {
        _deploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync("t1").Returns(Task.FromException<int>(new InvalidOperationException("boom")));

        await _service.ReconcileAllTenantsAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t2");
    }

    [Test]
    public async Task Tick_NoOperatorRegisteredYetAfterOneInterval_DoesNotJudge()
    {
        // The old behaviour: 5 minutes after the start the sweep wrote Offline although no operator
        // had reconnected yet (test-2: they came 6 minutes after the start).
        _clock.UtcNow += TimeSpan.FromMinutes(5);

        await _service.SweepIfSettledAsync();

        await _deploymentSiteService.DidNotReceiveWithAnyArgs().ReconcileOrphanedOnlineDeploymentSitesAsync(default!);
    }

    [Test]
    public async Task Tick_OperatorRegisteredJustAfterTheOldGrace_StillWaitsForItsClaims()
    {
        // test-2 timeline: start 15:59:49, old grace over 16:04:49, operators register 16:06:01 and claim
        // until 16:07:11. A tick at 16:09:49 is less than one interval after the registration.
        OperatorRegisteredAfter(TimeSpan.FromSeconds(372));
        _clock.UtcNow += TimeSpan.FromMinutes(10);

        await _service.SweepIfSettledAsync();

        await _deploymentSiteService.DidNotReceiveWithAnyArgs().ReconcileOrphanedOnlineDeploymentSitesAsync(default!);
    }

    [Test]
    public async Task Tick_OneIntervalAfterTheLastOperatorRegistration_Judges()
    {
        OperatorRegisteredAfter(TimeSpan.FromSeconds(372));
        _clock.UtcNow += TimeSpan.FromMinutes(15);

        await _service.SweepIfSettledAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t1");
        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t2");
    }

    [Test]
    public async Task Tick_NobodyEverRegisters_JudgesAfterThreeIntervals()
    {
        // Original AB#6418 goal: with no operator at all the sites end up Offline.
        _clock.UtcNow += TimeSpan.FromMinutes(15);

        await _service.SweepIfSettledAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t1");
    }

    [Test]
    public async Task Tick_OperatorKeepsReconnecting_CapStillLetsTheSweepJudge()
    {
        // A flapping edge operator registered one minute ago must not hold the sweep back forever.
        OperatorRegisteredAfter(TimeSpan.FromMinutes(19));
        _clock.UtcNow += TimeSpan.FromMinutes(20);

        await _service.SweepIfSettledAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t1");
    }
}
