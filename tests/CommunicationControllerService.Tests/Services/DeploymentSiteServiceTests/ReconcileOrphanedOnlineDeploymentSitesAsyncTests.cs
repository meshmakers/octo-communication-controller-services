using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#6418 — a deployment site persisted Online that no operator connection owns is set Offline.
/// </summary>
internal class ReconcileOrphanedOnlineDeploymentSitesAsyncTests : PoolServiceTestsBase
{
    private static RtDeploymentSite Site(RtCommunicationStateEnum state)
    {
        var site = RtEntityCreator.CreateDeploymentSite();
        site.CommunicationState = state;
        return site;
    }

    private void ReturnSites(params RtDeploymentSite[] sites)
    {
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(sites);
    }

    [Test]
    public async Task OnlineSiteWithoutOwner_IsSetOffline()
    {
        // The test-2 case: Online in the database, the controller restarted, nobody claimed it.
        var site = Site(RtCommunicationStateEnum.Online);
        ReturnSites(site);

        var count = await DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);

        await Assert.That(count).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, site.RtId, RtCommunicationStateEnum.Offline);
        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task OnlineSiteWithOwningConnection_IsLeftAlone()
    {
        var site = Site(RtCommunicationStateEnum.Online);
        ReturnSites(site);
        OperatorConnectionManager.GetConnectionsForDeploymentSite(TenantId, site.RtId.ToString())
            .Returns(new[] { ConnectionId });

        var count = await DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);

        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    [Arguments(RtCommunicationStateEnum.Offline)]
    [Arguments(RtCommunicationStateEnum.Unregistered)]
    public async Task NonOnlineSite_IsNotTouched(RtCommunicationStateEnum state)
    {
        ReturnSites(Site(state));

        var count = await DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);

        await Assert.That(count).IsEqualTo(0);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task MixedFleet_OnlyTheUnownedOnlineSiteGoesOffline()
    {
        var owned = Site(RtCommunicationStateEnum.Online);
        var orphan = Site(RtCommunicationStateEnum.Online);
        var offline = Site(RtCommunicationStateEnum.Offline);
        ReturnSites(owned, orphan, offline);
        OperatorConnectionManager.GetConnectionsForDeploymentSite(TenantId, owned.RtId.ToString())
            .Returns(new[] { ConnectionId });

        var count = await DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);

        await Assert.That(count).IsEqualTo(1);
        await CommunicationRepository.Received(1)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, orphan.RtId, RtCommunicationStateEnum.Offline);
        await CommunicationRepository.DidNotReceive()
            .SetDeploymentSiteCommunicationStateAsync(TenantId, owned.RtId, Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task CloudSiteReclaimedByCentralOperatorAfterRestart_StaysOnline_AndOrphanGoesOffline()
    {
        // Real connection manager, no mocks of the ownership: the controller restarted, the central
        // operator re-registered and claimed site A again (what RegisterDeploymentSiteAsync does);
        // site B (an edge site whose operator never came back) is still Online in the database.
        var manager = new OperatorConnectionManager(Substitute.For<IHubContext<OperatorHub>>());
        var service = new DeploymentSiteService(CommunicationRepository, DeploymentSiteCache,
            CommunicationEventService, manager, EncryptionService, TemplateResolver, OnDemandCapabilityService,
            ServiceAccountProvisioningService, ServiceAccountResolver, LendingScopeResolver,
            WorkloadLifecycleService, AdapterPoolMirrorProvisioningService);

        var reclaimed = Site(RtCommunicationStateEnum.Online);
        var orphan = Site(RtCommunicationStateEnum.Online);
        ReturnSites(reclaimed, orphan);

        manager.AddOperator("central-conn");
        manager.RegisterDeploymentSiteForConnection("central-conn", TenantId, reclaimed.RtId.ToString());

        // Two sweeps in a row must not flap the reclaimed site.
        await service.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);
        await service.ReconcileOrphanedOnlineDeploymentSitesAsync(TenantId);

        await CommunicationRepository.DidNotReceive()
            .SetDeploymentSiteCommunicationStateAsync(TenantId, reclaimed.RtId, Arg.Any<RtCommunicationStateEnum>());
        await CommunicationRepository.Received(2)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, orphan.RtId, RtCommunicationStateEnum.Offline);
    }
}
