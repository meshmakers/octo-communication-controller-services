using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
///     AB#4894: a workload deploy notification sent while the operator pod was being replaced is
///     lost silently (fire-and-forget SendAsync to a dying connection), stranding the entity in
///     DeploymentState=Pending forever. On every pool (re-)registration the controller therefore
///     re-dispatches whatever is still Pending — best effort, never failing the registration.
/// </summary>
internal class ReconcilePendingWorkloadsAsyncTests : PoolServiceTestsBase
{
    private RtPool _rtPool = null!;

    private RtAdapter GivenPoolWithAdapterInState(RtDeploymentStateEnum state)
    {
        _rtPool = new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = PoolName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        var rtPool = _rtPool;
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { rtPool });

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "test-adapter",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "0.1.1",
            ValuesYaml = string.Empty,
            DeploymentState = state,
        };
        CommunicationRepository.GetWorkloadsForPoolAsync(TenantId, PoolRtId)
            .Returns(new RtDeployableWorkload[] { adapter });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId)
            .Returns(adapter);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(rtPool);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });
        return adapter;
    }

    [Test]
    public async Task PendingWorkload_IsRedispatched()
    {
        // Arrange
        var adapter = GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);

        // Act
        await PoolService.ReconcilePendingWorkloadsAsync(TenantId, PoolRtId);

        // Assert — the full deploy path ran: notify + Pending state write.
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.WorkloadRtId == adapter.RtId.ToString()));
    }

    [Test]
    public async Task DeployedWorkload_IsNotTouched()
    {
        // Arrange — a healthy Deployed workload must not be re-deployed on every registration.
        GivenPoolWithAdapterInState(RtDeploymentStateEnum.Deployed);

        // Act
        await PoolService.ReconcilePendingWorkloadsAsync(TenantId, PoolRtId);

        // Assert
        await OperatorConnectionManager.DidNotReceive()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task WorkloadLookupFails_DoesNotThrow()
    {
        // Arrange — the lookup can race a concurrent tenant-update cache unload; the
        // registration path must survive that.
        CommunicationRepository.GetWorkloadsForPoolAsync(TenantId, PoolRtId)
            .ThrowsAsync(new InvalidOperationException("cache unloaded"));

        // Act & Assert — no throw.
        await PoolService.ReconcilePendingWorkloadsAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.DidNotReceive()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task RedispatchFailure_ContinuesWithNextPendingWorkload()
    {
        // Arrange — two Pending workloads; the first one's deploy fails (entity vanished
        // between listing and dispatch). The second must still be re-dispatched.
        var broken = GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);

        var second = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "second-adapter",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "0.1.1",
            ValuesYaml = string.Empty,
            DeploymentState = RtDeploymentStateEnum.Pending,
        };
        CommunicationRepository.GetWorkloadsForPoolAsync(TenantId, PoolRtId)
            .Returns(new RtDeployableWorkload[] { broken, second });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, broken.RtId)
            .Returns((RtDeployableWorkload?)null);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, second.RtId)
            .Returns(second);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, second.RtId)
            .Returns(_rtPool);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, second.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });

        // Act
        await PoolService.ReconcilePendingWorkloadsAsync(TenantId, PoolRtId);

        // Assert
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.WorkloadRtId == second.RtId.ToString()));
    }
}
