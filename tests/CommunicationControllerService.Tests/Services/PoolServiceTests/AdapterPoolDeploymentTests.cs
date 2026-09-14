using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
///     AB#4924 §7 — the controller half of adapter-pool deployment: deploy, undeploy and scale.
///
///     A pool is <b>one workload with a replica range</b> (§13.2), so it rides the existing
///     1:1 workload ↔ helm release path and the AB#4917 scale verb. What is new is that the
///     operator has to be able to tell it apart from a tenant workload — it lands in a different
///     namespace, it gets an owner reference, and it must not be handed the cluster's shared
///     data-store credentials. That discriminator, and the replica/sizing values every member is
///     rendered from, are what this suite pins.
/// </summary>
internal class AdapterPoolDeploymentTests : PoolServiceTestsBase
{
    private RtDeploymentSite ArrangeCloudPool() => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
        Name = "cloud-pool",
        Environment = RtEnvironmentEnum.Cloud,
    };

    private RtAdapterPool ArrangeAdapterPool(RtDeploymentSite site, int minReplicas = 1, int maxReplicas = 3,
        RtDeploymentStateEnum deploymentState = RtDeploymentStateEnum.Undeployed)
    {
        var pool = new RtAdapterPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "meshtest-pool",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "1.0.0",
            DeploymentState = deploymentState,
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas,
            SharingMode = RtAdapterSharingModeEnum.Descendants,
            ScaleUpPolicy = RtPoolScaleUpPolicyEnum.QueueDepthOrWaitSeconds,
            ScaleUpQueueDepthThreshold = 5,
            ScaleUpQueueWaitSeconds = 30,
        };

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, pool.RtId).Returns(pool);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, pool.RtId).Returns(site);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, pool.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://charts.example.com",
            });
        return pool;
    }

    private async Task<WorkloadDeployedDto> DeployAndCaptureAsync(RtDeployableWorkload workload)
    {
        await PoolService.DeployWorkloadAsync(TenantId, workload.RtId);

        return (WorkloadDeployedDto)OperatorConnectionManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IOperatorConnectionManager.NotifyWorkloadDeployedAsync))
            .GetArguments()[0]!;
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_TellsTheOperatorItIsAPool()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(pool);

        await Assert.That(dto.WorkloadType).IsEqualTo(WorkloadTypeDto.AdapterPool);
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_StartsTheReleaseAtMinReplicas()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 2, maxReplicas: 5);

        var dto = await DeployAndCaptureAsync(pool);

        var replicaCount = dto.Values.SingleOrDefault(v => v.Path == "replicaCount");
        await Assert.That(replicaCount).IsNotNull();
        await Assert.That(replicaCount!.Value).IsEqualTo("2");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_ProjectsThePerMemberSizingOntoTheChartResources()
    {
        // Q15 — one sizing per pool, applied identically to every member. A chart value is exactly
        // that: it renders into each replica's pod spec unchanged.
        var pool = ArrangeAdapterPool(ArrangeCloudPool());
        pool.PoolMemberCpuRequest = "250m";
        pool.PoolMemberCpuLimit = "1";
        pool.PoolMemberMemoryRequest = "512Mi";
        pool.PoolMemberMemoryLimit = "1Gi";

        var dto = await DeployAndCaptureAsync(pool);

        using var _ = Assert.Multiple();
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.cpu").Value).IsEqualTo("250m");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.limits.cpu").Value).IsEqualTo("1");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.memory").Value).IsEqualTo("512Mi");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.limits.memory").Value).IsEqualTo("1Gi");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPoolWithoutSizing_SendsNoResourceValues()
    {
        // An unset sizing attribute means "whatever the chart defaults to". Rendering it as an
        // empty string produces an invalid pod spec and the release fails on admission.
        var pool = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(pool);

        await Assert.That(dto.Values.Any(v => v.Path.StartsWith("resources.", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_DoesNotOverruleAPinnedReplicaCount()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 2);
        pool.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord { Path = "replicaCount", Value = "4", IsSecret = false },
        };

        var dto = await DeployAndCaptureAsync(pool);

        await Assert.That(dto.Values.Single(v => v.Path == "replicaCount").Value).IsEqualTo("4");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_NeverReceivesTheClusterDataStoreCredentials()
    {
        // 🔴 The flag hands over the cluster's SHARED Mongo / CrateDB credentials. A pool member
        // runs work for tenants other than the one that owns it, and the lease is what gives it one
        // tenant at a time — a standing credential to all of them makes that lease decorative. The
        // operator refuses the same thing independently.
        var pool = ArrangeAdapterPool(ArrangeCloudPool());
        pool.ReceivesClusterSecrets = true;

        var dto = await DeployAndCaptureAsync(pool);

        await Assert.That(dto.ReceivesClusterSecrets).IsFalse();
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_MovesItToPending()
    {
        // Without a DeploymentState writer for the pool type this silently did nothing: the pool
        // stayed at its default state, Studio never showed it deployed, and Undeploy refused it as
        // "already not deployed".
        var pool = ArrangeAdapterPool(ArrangeCloudPool());

        await PoolService.DeployWorkloadAsync(TenantId, pool.RtId);

        await CommunicationRepository.Received(1).SetAdapterPoolDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == pool.RtId
                                     && id.CkTypeId == SystemCommunicationCkIds.RtCkAdapterPoolTypeId),
            RtDeploymentStateEnum.Pending,
            Arg.Any<string?>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterPool_TellsTheOperatorItIsAPool()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), deploymentState: RtDeploymentStateEnum.Deployed);

        await PoolService.UndeployWorkloadAsync(TenantId, pool.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(d => d.WorkloadType == WorkloadTypeDto.AdapterPool));
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_WithinTheRange_RequestsExactlyThatManyMembers()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 1, maxReplicas: 3,
            deploymentState: RtDeploymentStateEnum.Deployed);

        var effective = await PoolService.ScaleAdapterPoolAsync(TenantId, pool.RtId, 3);

        await Assert.That(effective).IsEqualTo(3);
        await WorkloadLifecycleService.Received(1).RequestScaleAsync(TenantId, pool, 3);
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_BelowMinReplicas_ReportsTheFloorAsTheEffectiveCount()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 1, maxReplicas: 3,
            deploymentState: RtDeploymentStateEnum.Deployed);

        var effective = await PoolService.ScaleAdapterPoolAsync(TenantId, pool.RtId, 0);

        await Assert.That(effective).IsEqualTo(1);
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_UndeployedPool_IsRefused()
    {
        var pool = ArrangeAdapterPool(ArrangeCloudPool(), deploymentState: RtDeploymentStateEnum.Undeployed);

        await Assert.ThrowsAsync<PoolServiceException>(
            () => PoolService.ScaleAdapterPoolAsync(TenantId, pool.RtId, 2));

        await WorkloadLifecycleService.DidNotReceiveWithAnyArgs().RequestScaleAsync(
            Arg.Any<string>(), Arg.Any<RtDeployableWorkload>(), Arg.Any<int>());
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_OnAnOrdinaryAdapter_IsRefused()
    {
        // An Adapter's replica count is owned by the on-demand lifecycle (hibernate / wake). Only a
        // pool has a range to move within, so scaling one through this path would silently fight
        // the lifecycle state machine.
        var site = ArrangeCloudPool();
        var adapter = Helper.RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, adapter.RtId).Returns(site);

        await Assert.ThrowsAsync<PoolServiceException>(
            () => PoolService.ScaleAdapterPoolAsync(TenantId, adapter.RtId, 2));
    }
}
