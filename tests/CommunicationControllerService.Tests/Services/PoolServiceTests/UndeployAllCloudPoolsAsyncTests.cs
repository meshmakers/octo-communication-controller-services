using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

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
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId).Returns([]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolUndeployedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_OneTrackedPool_NotifiesOperator()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns([(PoolOneRtId, "pool-one")]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1)
            .NotifyPoolUndeployedAsync(TenantId, PoolOneRtId, "pool-one");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_MultipleTrackedPools_NotifiesEach()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns([(PoolOneRtId, "pool-one"), (PoolTwoRtId, "pool-two"), (PoolThreeRtId, "pool-three")]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolOneRtId, "pool-one");
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolTwoRtId, "pool-two");
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolThreeRtId, "pool-three");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_DoesNotHitTenantRepository()
    {
        // Regression: the previous implementation called GetPoolsAsync() here,
        // which races with PreUpdatePreDeleteTenantConsumer's cache unload and
        // throws "Failed to get pools" — leaving CRs orphaned in the cluster.
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns([(PoolOneRtId, "pool-one")]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await CommunicationRepository.DidNotReceive().GetPoolsAsync(Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NotifyFails_StillContinuesOtherPools()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns([(PoolBrokenRtId, "pool-broken"), (PoolOneRtId, "pool-ok")]);
        OperatorConnectionManager
            .NotifyPoolUndeployedAsync(TenantId, PoolBrokenRtId, "pool-broken")
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolOneRtId, "pool-ok");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_TrackedWorkloads_AlsoNotifiedBeforePools()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns([(PoolOneRtId, "pool-one")]);
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId,
                PoolRtId = PoolOneRtId,
                PoolName = "pool-one",
                WorkloadRtId = WorkloadRtId1,
                WorkloadName = "wl-1",
                WorkloadType = WorkloadTypeDto.Adapter,
            },
        });

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-1"));
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolOneRtId, "pool-one");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NeitherPoolsNorWorkloads_NoOp()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId).Returns([]);
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId)
            .Returns(Array.Empty<WorkloadUndeployedDto>());

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolUndeployedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadUndeployedAsync(Arg.Any<WorkloadUndeployedDto>());
    }
}
