using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

internal class UndeployAllCloudPoolsAsyncTests : PoolServiceTestsBase
{
    private const string PoolOneRtId = "65d5c447b420da3fb12381c1";
    private const string PoolTwoRtId = "65d5c447b420da3fb12381c2";
    private const string PoolThreeRtId = "65d5c447b420da3fb12381c3";
    private const string PoolBrokenRtId = "65d5c447b420da3fb12381cb";
    private const string WorkloadRtId1 = "65d5c447b420da3fb12382aa";

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NoTrackedPools_NoNotifications()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId).Returns([]);

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyDeploymentSiteUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_OneTrackedPool_NotifiesOperator()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId)
            .Returns([PoolOneRtId]);

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.Received(1)
            .NotifyDeploymentSiteUndeployedAsync(TenantId, PoolOneRtId);
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_MultipleTrackedPools_NotifiesEach()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId)
            .Returns([PoolOneRtId, PoolTwoRtId, PoolThreeRtId]);

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, PoolOneRtId);
        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, PoolTwoRtId);
        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, PoolThreeRtId);
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_DoesNotHitTenantRepository()
    {
        // Regression: the previous implementation called GetDeploymentSitesAsync() here,
        // which races with PreUpdatePreDeleteTenantConsumer's cache unload and
        // throws "Failed to get deploymentSites" — leaving CRs orphaned in the cluster.
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId)
            .Returns([PoolOneRtId]);

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await CommunicationRepository.DidNotReceive().GetDeploymentSitesAsync(Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NotifyFails_StillContinuesOtherPools()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId)
            .Returns([PoolBrokenRtId, PoolOneRtId]);
        OperatorConnectionManager
            .NotifyDeploymentSiteUndeployedAsync(TenantId, PoolBrokenRtId)
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, PoolOneRtId);
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_TrackedWorkloads_AlsoNotifiedBeforePools()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId)
            .Returns([PoolOneRtId]);
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId,
                DeploymentSiteRtId = PoolOneRtId,
                WorkloadRtId = WorkloadRtId1,
                WorkloadName = "wl-1",
                WorkloadType = WorkloadTypeDto.Adapter,
            },
        });

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-1"));
        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, PoolOneRtId);
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NeitherPoolsNorWorkloads_NoOp()
    {
        OperatorConnectionManager.GetDeployedDeploymentSitesForTenant(TenantId).Returns([]);
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId)
            .Returns(Array.Empty<WorkloadUndeployedDto>());

        await DeploymentSiteService.UndeployAllCloudDeploymentSitesAsync(TenantId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyDeploymentSiteUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadUndeployedAsync(Arg.Any<WorkloadUndeployedDto>());
    }
}
