using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal class UndeployAllCloudPoolsAsyncTests : PoolServiceTestsBase
{
    [Test]
    public async Task UndeployAllCloudPoolsAsync_NoTrackedPools_NoNotifications()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId).Returns([]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_OneTrackedPool_NotifiesOperator()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId).Returns(["pool-one"]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1)
            .NotifyPoolUndeployedAsync(TenantId, "pool-one");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_MultipleTrackedPools_NotifiesEach()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns(["pool-one", "pool-two", "pool-three"]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, "pool-one");
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, "pool-two");
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, "pool-three");
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_DoesNotHitTenantRepository()
    {
        // Regression: the previous implementation called GetPoolsAsync() here,
        // which races with PreUpdatePreDeleteTenantConsumer's cache unload and
        // throws "Failed to get pools" — leaving CRs orphaned in the cluster.
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId).Returns(["pool-one"]);

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await CommunicationRepository.DidNotReceive().GetPoolsAsync(Arg.Any<string>());
    }

    [Test]
    public async Task UndeployAllCloudPoolsAsync_NotifyFails_StillContinuesOtherPools()
    {
        OperatorConnectionManager.GetDeployedPoolsForTenant(TenantId)
            .Returns(["pool-broken", "pool-ok"]);
        OperatorConnectionManager
            .NotifyPoolUndeployedAsync(TenantId, "pool-broken")
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        await PoolService.UndeployAllCloudPoolsAsync(TenantId);

        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, "pool-ok");
    }
}
