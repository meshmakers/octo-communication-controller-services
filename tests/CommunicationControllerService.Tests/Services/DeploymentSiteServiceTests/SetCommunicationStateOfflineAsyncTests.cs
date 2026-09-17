using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

internal class SetCommunicationStateOfflineAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task SetCommunicationStateOfflineAsync_PoolFoundByRtIdAndConnectionMatches_WritesOffline()
    {
        // Happy path for the OperatorHub.OnDisconnectedAsync fix: when the
        // disconnecting connection still owns the cached deploymentSite, the service
        // must write Offline. This is the only path that flips state when
        // the operator's SignalR connection drops without a graceful
        // UnregisterDeploymentSiteOperatorAsync.
        GivenTenantInCache();
        AddDeploymentSiteToTenant();

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId, ConnectionId);

        await CommunicationRepository.Received(1)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, DeploymentSiteRtId, RtCommunicationStateEnum.Offline);
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_StaleDisconnectFromReplacedConnection_NoOp()
    {
        // Regression: a controller restart drops the operator's previous
        // SignalR connection. The operator auto-reconnects with a NEW
        // connection id and re-registers its deploymentSites (cache.ConnectionId =
        // new id). Some time later the previous connection's
        // OnDisconnectedAsync finally fires on the controller. Without the
        // stale-disconnect guard this overwrites the freshly-written
        // Online state and the UI shows the deploymentSite offline even though the
        // operator is connected.
        GivenTenantInCache();
        AddDeploymentSiteToTenant(connectionId: "new-connection-id");

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId,
            "stale-old-connection-id");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_TenantNotInCache_NoOp()
    {
        GivenTenantNotInCache();

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId, ConnectionId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_PoolRtIdNotInCache_NoOp()
    {
        GivenTenantInCache();
        // Don't add the deploymentSite — DeploymentSitesById lookup must miss and the call must no-op.

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId, ConnectionId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_OtherConnectionStillClaimsPool_NoOp()
    {
        // Regression for the multi-claimer bug: when two operator connections
        // claim the same deploymentSite (e.g. central operator with 2 replicas, or a
        // rolling restart with brief overlap), the disconnect of ONE claimer
        // must not flip the deploymentSite Offline as long as the other connection is
        // still hosting it. The DeploymentSiteDescription cache only carries the LAST
        // claim's ConnectionId — without this guard the OperatorHub's
        // OnDisconnectedAsync orphan-flip would mark the deploymentSite Offline even
        // though the surviving operator is still connected. By the time we
        // get here OperatorConnectionManager.RemoveOperator has already
        // removed the disconnecting connection's tracking entry, so any
        // results from GetConnectionsForDeploymentSite are surviving operators.
        GivenTenantInCache();
        AddDeploymentSiteToTenant();
        OperatorConnectionManager
            .GetConnectionsForDeploymentSite(TenantId, DeploymentSiteRtId.ToString())
            .Returns(new[] { "surviving-connection-id" });

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId,
            "disconnecting-connection-id");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task SetCommunicationStateOfflineAsync_OtherConnectionStillClaimsPool_RewiresCachedConnectionId()
    {
        // Same scenario as the previous test, plus pinning the side effect:
        // when the disconnecting connection was the one in the cache, the
        // cache must be rewired to a surviving connection so the stale-
        // disconnect guard catches it when that surviving connection eventually
        // disconnects (rather than the cached id being a dead connection
        // and the guard then refusing to write Offline forever).
        GivenTenantInCache();
        var deploymentSite = AddDeploymentSiteToTenant(connectionId: "disconnecting-connection-id");
        OperatorConnectionManager
            .GetConnectionsForDeploymentSite(TenantId, DeploymentSiteRtId.ToString())
            .Returns(new[] { "surviving-connection-id" });

        await DeploymentSiteService.SetCommunicationStateOfflineAsync(TenantId, DeploymentSiteRtId,
            "disconnecting-connection-id");

        await Assert.That(deploymentSite.ConnectionId).IsEqualTo("surviving-connection-id");
    }
}
