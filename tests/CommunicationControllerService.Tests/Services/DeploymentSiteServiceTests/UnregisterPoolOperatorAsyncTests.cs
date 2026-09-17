using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

internal class UnregisterPoolOperatorAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task UnregisterPoolOperatorAsync_TenantNotInCache_NoOp()
    {
        GivenTenantNotInCache();

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteDeploymentStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtDeploymentStateEnum>());
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolNotInTenant_NoOp()
    {
        GivenTenantInCache();
        // Don't add the deploymentSite

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteCommunicationStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtCommunicationStateEnum>());
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_WritesUnregisteredBeforeRemovingFromCache()
    {
        // This is the regression for the bug observed in production: the order of writes
        // matters. SetDeploymentSiteCommunicationStateAsync must run while the deploymentSite is still in the
        // _deploymentSiteCache, otherwise the subsequent OnDisconnectedAsync (which is the only
        // other path that flips the state) can no longer locate the deploymentSite either, and the
        // UI keeps showing the deploymentSite as Online forever.
        GivenTenantInCache();
        AddDeploymentSiteToTenant();

        var receivedStateAtRepoCall = (Online: false, Offline: false, Unregistered: false);

        // Capture whether the deploymentSite was still in the cache at the moment the repo write happened.
        await CommunicationRepository.SetDeploymentSiteCommunicationStateAsync(TenantId, DeploymentSiteRtId,
            Arg.Do<RtCommunicationStateEnum>(_ =>
            {
                receivedStateAtRepoCall.Unregistered = DeploymentSiteTenant.DeploymentSitesById.ContainsKey(DeploymentSiteRtId);
            }));

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        using var _ = Assert.Multiple();
        await CommunicationRepository.Received(1)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, DeploymentSiteRtId, RtCommunicationStateEnum.Unregistered);
        await Assert.That(receivedStateAtRepoCall.Unregistered).IsTrue();
        await Assert.That(DeploymentSiteTenant.DeploymentSitesById.ContainsKey(DeploymentSiteRtId)).IsFalse();
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_SetsDeploymentStatePending()
    {
        GivenTenantInCache();
        AddDeploymentSiteToTenant();

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.Received(1)
            .SetDeploymentSiteDeploymentStateAsync(TenantId, DeploymentSiteRtId, RtDeploymentStateEnum.Pending);
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_DeployedCloudPool_FlipsToPending()
    {
        GivenTenantInCache();
        AddDeploymentSiteToTenant();
        GivenPersistedPool(RtDeploymentStateEnum.Deployed, RtEnvironmentEnum.Cloud);

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.Received(1)
            .SetDeploymentSiteDeploymentStateAsync(TenantId, DeploymentSiteRtId, RtDeploymentStateEnum.Pending);
    }

    [Test]
    [Arguments(RtDeploymentStateEnum.Undeployed, RtEnvironmentEnum.Cloud)]
    [Arguments(RtDeploymentStateEnum.Disabled, RtEnvironmentEnum.Edge)]
    public async Task UnregisterPoolOperatorAsync_RestingPool_KeepsItsDeploymentState(
        RtDeploymentStateEnum restingState, RtEnvironmentEnum environment)
    {
        // AB#4255 regression: UndeployDeploymentSiteAsync writes the resting state before it notifies the
        // operator, whose release used to overwrite it with Pending - so a gracefully undeployed
        // Cloud deploymentSite never reached a state the Communication disable guard accepts.
        GivenTenantInCache();
        AddDeploymentSiteToTenant();
        GivenPersistedPool(restingState, environment);

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteDeploymentStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtDeploymentStateEnum>());
        await CommunicationRepository.Received(1)
            .SetDeploymentSiteCommunicationStateAsync(TenantId, DeploymentSiteRtId, RtCommunicationStateEnum.Unregistered);
    }

    private void GivenPersistedPool(RtDeploymentStateEnum deploymentState, RtEnvironmentEnum environment)
    {
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[]
        {
            new RtDeploymentSite
            {
                RtId = DeploymentSiteRtId,
                CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
                Name = DeploymentSiteName,
                DeploymentState = deploymentState,
                Environment = environment
            }
        });
    }

    [Test]
    public async Task UnregisterPoolOperatorAsync_PoolInCache_StoresInformationEvent()
    {
        GivenTenantInCache();
        AddDeploymentSiteToTenant();

        await DeploymentSiteService.UnregisterDeploymentSiteOperatorAsync(TenantId, DeploymentSiteRtId);

        await CommunicationEventService.Received(1)
            .StoreInformationEventAsync(TenantId,
                Arg.Is<string>(s => s.Contains(DeploymentSiteName) && s.Contains("unregistered")),
                Arg.Any<RtEntityId>());
    }
}
