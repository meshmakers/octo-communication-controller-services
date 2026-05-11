using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Hubs;

internal class OperatorConnectionManagerTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";
    private const string PoolX = "pool-x";
    private const string PoolY = "pool-y";

    private static OperatorConnectionManager CreateSut() =>
        new(Substitute.For<IHubContext<OperatorHub>>());

    [Test]
    public async Task GetDeployedPoolsForTenant_NeverNotified_ReturnsEmpty()
    {
        var sut = CreateSut();

        var pools = sut.GetDeployedPoolsForTenant(TenantA);

        await Assert.That(pools).IsNotNull();
        await Assert.That(pools.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotifyPoolDeployedAsync_TracksPool()
    {
        var sut = CreateSut();

        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });

        var pools = sut.GetDeployedPoolsForTenant(TenantA);
        await Assert.That(pools.Count).IsEqualTo(1);
        await Assert.That(pools).Contains(PoolX);
    }

    [Test]
    public async Task NotifyPoolDeployedAsync_DuplicatePool_TrackedOnce()
    {
        var sut = CreateSut();

        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_RemovesTrackedPool()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolX);

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(0);
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_UnknownPool_NoOp()
    {
        var sut = CreateSut();

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolX);

        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA).Count).IsEqualTo(0);
    }

    [Test]
    public async Task GetDeployedPoolsForTenant_IsolatesTenants()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantB, PoolName = PoolY });

        var poolsA = sut.GetDeployedPoolsForTenant(TenantA);
        var poolsB = sut.GetDeployedPoolsForTenant(TenantB);

        await Assert.That(poolsA).Contains(PoolX);
        await Assert.That(poolsA).DoesNotContain(PoolY);
        await Assert.That(poolsB).Contains(PoolY);
        await Assert.That(poolsB).DoesNotContain(PoolX);
    }

    [Test]
    public async Task GetDeployedPools_AcrossTenants_ReturnsAll()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantB, PoolName = PoolY });

        var all = sut.GetDeployedPools().ToArray();

        await Assert.That(all.Length).IsEqualTo(2);
        await Assert.That(all.Any(p => p.TenantId == TenantA && p.PoolName == PoolX)).IsTrue();
        await Assert.That(all.Any(p => p.TenantId == TenantB && p.PoolName == PoolY)).IsTrue();
    }

    [Test]
    public async Task NotifyPoolUndeployedAsync_LastPoolForTenant_RemovesTenantBucket()
    {
        var sut = CreateSut();
        await sut.NotifyPoolDeployedAsync(new DeployedPoolDto { TenantId = TenantA, PoolName = PoolX });

        await sut.NotifyPoolUndeployedAsync(TenantA, PoolX);

        await Assert.That(sut.GetDeployedPools()).IsEmpty();
        await Assert.That(sut.GetDeployedPoolsForTenant(TenantA)).IsEmpty();
    }

    // ---- Workload tracking ----

    private static WorkloadDeployedDto WorkloadDeploy(string tenantId, string poolName,
        string workloadName, WorkloadTypeDto type = WorkloadTypeDto.Adapter) =>
        new()
        {
            TenantId = tenantId,
            PoolName = poolName,
            WorkloadName = workloadName,
            WorkloadType = type,
            ChartName = "test-chart",
            ChartVersion = "1.0.0",
        };

    [Test]
    public async Task NotifyWorkloadDeployedAsync_TracksWorkload()
    {
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));

        var tracked = sut.GetDeployedWorkloadsForTenant(TenantA);
        await Assert.That(tracked.Count).IsEqualTo(1);
        await Assert.That(tracked.Any(w => w.PoolName == PoolX && w.WorkloadName == "wl-1")).IsTrue();
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_PreservesWorkloadType()
    {
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "app-1", WorkloadTypeDto.Application));

        var tracked = sut.GetDeployedWorkloadsForTenant(TenantA);
        await Assert.That(tracked.Single().WorkloadType).IsEqualTo(WorkloadTypeDto.Application);
    }

    [Test]
    public async Task NotifyWorkloadDeployedAsync_SameWorkloadTwice_TrackedOnce()
    {
        // Same pool + workload name should overwrite (idempotent re-deploy).
        var sut = CreateSut();

        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA).Count).IsEqualTo(1);
    }

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_RemovesTrackedWorkload()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));

        await sut.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = TenantA,
            PoolName = PoolX,
            WorkloadName = "wl-1",
            WorkloadType = WorkloadTypeDto.Adapter,
        });

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }

    [Test]
    public async Task GetDeployedWorkloadsForTenant_IsolatesTenants()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantB, PoolY, "wl-2"));

        var trackedA = sut.GetDeployedWorkloadsForTenant(TenantA);
        var trackedB = sut.GetDeployedWorkloadsForTenant(TenantB);

        await Assert.That(trackedA.Any(w => w.WorkloadName == "wl-1")).IsTrue();
        await Assert.That(trackedA.Any(w => w.WorkloadName == "wl-2")).IsFalse();
        await Assert.That(trackedB.Any(w => w.WorkloadName == "wl-2")).IsTrue();
        await Assert.That(trackedB.Any(w => w.WorkloadName == "wl-1")).IsFalse();
    }

    [Test]
    public async Task NotifyWorkloadUndeployedAsync_LastWorkloadForTenant_RemovesTenantBucket()
    {
        var sut = CreateSut();
        await sut.NotifyWorkloadDeployedAsync(WorkloadDeploy(TenantA, PoolX, "wl-1"));

        await sut.NotifyWorkloadUndeployedAsync(new WorkloadUndeployedDto
        {
            TenantId = TenantA,
            PoolName = PoolX,
            WorkloadName = "wl-1",
            WorkloadType = WorkloadTypeDto.Adapter,
        });

        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }

    [Test]
    public async Task GetDeployedWorkloadsForTenant_NoneTracked_ReturnsEmpty()
    {
        var sut = CreateSut();
        await Assert.That(sut.GetDeployedWorkloadsForTenant(TenantA)).IsEmpty();
    }
}
