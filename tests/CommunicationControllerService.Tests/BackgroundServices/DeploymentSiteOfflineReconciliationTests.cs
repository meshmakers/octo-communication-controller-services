using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.BackgroundServices;

/// <summary>
///     AB#6418 — the sweep drives the per-tenant reconcile, stays out of the way during shutdown and
///     survives a failing tenant.
/// </summary>
internal class DeploymentSiteOfflineReconciliationTests
{
    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    private readonly IDeploymentSiteService _deploymentSiteService = Substitute.For<IDeploymentSiteService>();
    private readonly IShutdownState _shutdownState = Substitute.For<IShutdownState>();
    private readonly DeploymentSiteOfflineReconciliationBackgroundService _service;

    public DeploymentSiteOfflineReconciliationTests()
    {
        _adapterCache.GetEnabledTenantIds().Returns(new[] { "t1", "t2" });
        _service = new DeploymentSiteOfflineReconciliationBackgroundService(_adapterCache, _deploymentSiteService,
            _shutdownState, Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()));
    }

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
    public async Task Sweep_FailingTenant_DoesNotStopTheOthers()
    {
        _deploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync("t1").Returns(Task.FromException<int>(new InvalidOperationException("boom")));

        await _service.ReconcileAllTenantsAsync();

        await _deploymentSiteService.Received(1).ReconcileOrphanedOnlineDeploymentSitesAsync("t2");
    }
}
