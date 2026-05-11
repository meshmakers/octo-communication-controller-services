using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

internal class DeployPoolAsyncTests : PoolServiceTestsBase
{
    private async Task GivenCloudPool()
    {
        var rtPool = new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = PoolName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { rtPool });
        // GetWorkloadsForPoolAsync — default empty
        CommunicationRepository.GetWorkloadsForPoolAsync(TenantId, PoolRtId)
            .Returns(Array.Empty<RtDeployableWorkload>());
        await Task.CompletedTask;
    }

    private async Task GivenEdgePool()
    {
        var rtPool = new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = PoolName,
            Environment = RtEnvironmentEnum.Edge,
        };
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { rtPool });
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_NotifiesPoolDeployed()
    {
        await GivenCloudPool();

        await PoolService.DeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.Received(1).NotifyPoolDeployedAsync(
            Arg.Is<DeployedPoolDto>(d => d.TenantId == TenantId && d.PoolName == PoolName));
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_NoWorkloads_DoesNotNotifyWorkloads()
    {
        await GivenCloudPool();

        await PoolService.DeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.DidNotReceive()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployPoolAsync_EdgePool_DoesNotEnumerateWorkloads()
    {
        // Edge pools are deployed externally; the operator must not be told to
        // do anything for either the pool or its workloads.
        await GivenEdgePool();

        await PoolService.DeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolDeployedAsync(Arg.Any<DeployedPoolDto>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .GetWorkloadsForPoolAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_SetsDeploymentStateDeployed()
    {
        await GivenCloudPool();

        await PoolService.DeployPoolAsync(TenantId, PoolRtId);

        await CommunicationRepository.Received(1).SetPoolDeploymentStateAsync(TenantId, PoolRtId,
            RtDeploymentStateEnum.Deployed);
    }

    [Test]
    public async Task UndeployPoolAsync_CloudPool_NotifiesWorkloadsBeforePool()
    {
        await GivenCloudPool();
        // Pretend two workloads were tracked from an earlier deploy.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolName = PoolName,
                WorkloadName = "wl-a", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolName = PoolName,
                WorkloadName = "wl-b", WorkloadType = WorkloadTypeDto.Application,
            },
        });

        await PoolService.UndeployPoolAsync(TenantId, PoolRtId);

        // Both workload notifies fired, and the pool notify after them.
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-a"));
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-b"));
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolName);
    }

    [Test]
    public async Task UndeployPoolAsync_CloudPool_OnlyUndeploysWorkloadsOfThisPool()
    {
        await GivenCloudPool();
        // Two workloads, one in a different pool — must not be undeployed here.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolName = PoolName,
                WorkloadName = "wl-here", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolName = "other-pool",
                WorkloadName = "wl-elsewhere", WorkloadType = WorkloadTypeDto.Adapter,
            },
        });

        await PoolService.UndeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-here"));
        await OperatorConnectionManager.DidNotReceive().NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-elsewhere"));
    }
}
