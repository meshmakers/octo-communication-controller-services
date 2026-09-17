using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#4924 §7 — the controller half of adapter-deploymentSite deployment: deploy, undeploy and scale.
///
///     A deploymentSite is <b>one workload with a replica range</b> (§13.2), so it rides the existing
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
        Name = "cloud-deploymentSite",
        Environment = RtEnvironmentEnum.Cloud,
    };

    private RtAdapterPool ArrangeAdapterPool(RtDeploymentSite site, int minReplicas = 1, int maxReplicas = 3,
        RtDeploymentStateEnum deploymentState = RtDeploymentStateEnum.Undeployed)
    {
        var deploymentSite = new RtAdapterPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "meshtest-deploymentSite",
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

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, deploymentSite.RtId).Returns(deploymentSite);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, deploymentSite.RtId).Returns(site);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, deploymentSite.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://charts.example.com",
            });
        return deploymentSite;
    }

    private RtAdapter ArrangePlainAdapter(RtDeploymentSite site)
    {
        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "plain-adapter",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "1.0.0",
            ValuesYaml = string.Empty,
            DeploymentState = RtDeploymentStateEnum.Undeployed,
        };

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId).Returns(site);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://charts.example.com",
            });
        return adapter;
    }

    private async Task<WorkloadDeployedDto> DeployAndCaptureAsync(RtDeployableWorkload workload)
    {
        await DeploymentSiteService.DeployWorkloadAsync(TenantId, workload.RtId);

        return (WorkloadDeployedDto)OperatorConnectionManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(IOperatorConnectionManager.NotifyWorkloadDeployedAsync))
            .GetArguments()[0]!;
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_TellsTheOperatorItIsAPool()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.WorkloadType).IsEqualTo(WorkloadTypeDto.AdapterPool);
    }

    /// <summary>
    ///     🔴 AB#4924 §9.4 — without these two values the pod is not a deploymentSite member at all. The SDK
    ///     composes one only when BOTH ids are present; with neither, the process starts, binds
    ///     <c>AdapterPoolMemberOptions</c> to its defaults, finds <c>IsEnabled</c> false, logs
    ///     "started without a configured deploymentSite … Doing nothing" and stays healthy for ever. Sizing and
    ///     replica count alone — all this method used to write — produce exactly that pod, which is
    ///     why every other test in this file passed while no deploymentSite member could run in a cluster.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_SendsThePoolIdentityTheMemberNeedsToRegister()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.Values.Single(v => v.Path == "adapterPool.adapterPoolTenantId").Value)
            .IsEqualTo(TenantId);
        await Assert.That(dto.Values.Single(v => v.Path == "adapterPool.poolRtId").Value)
            .IsEqualTo(deploymentSite.RtId.ToString());
    }

    /// <summary>
    ///     The tenant is the <b>lender</b> — the member's own connection tenant, never the tenant of
    ///     any work it executes, which arrives per lease. Pinned separately from the value above
    ///     because "it happens to be the deploying tenant" is the kind of coincidence a refactor
    ///     replaces with a borrower id without anything failing.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_NamesTheLendingTenantAsThePoolTenant()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.Values.Single(v => v.Path == "adapterPool.adapterPoolTenantId").Value)
            .IsEqualTo(TenantId);
    }

    /// <summary>
    ///     🔴 No member id is sent. <c>AdapterPoolMemberOptions.EffectiveMemberId</c> falls back to the
    ///     machine name, which is the pod name in Kubernetes — so every replica identifies itself
    ///     correctly and stays correct when one is rescheduled. A value written here would give every
    ///     replica of the deployment the same member id, and the controller's own registry keys
    ///     members by it.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_SendsNoMemberId()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.Values.Any(v => v.Path.Contains("memberId", StringComparison.OrdinalIgnoreCase)))
            .IsFalse();
    }

    /// <summary>
    ///     An ordinary adapter is not a deploymentSite member, and must not be handed a deploymentSite identity — the chart
    ///     would then render it as one and it would stop being reachable on its own tenant route.
    /// </summary>
    [Test]
    public async Task DeployWorkloadAsync_PlainAdapter_SendsNoPoolIdentity()
    {
        var adapter = ArrangePlainAdapter(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(dto.Values.Any(v => v.Path.StartsWith("adapterPool.", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_StartsTheReleaseAtMinReplicas()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 2, maxReplicas: 5);

        var dto = await DeployAndCaptureAsync(deploymentSite);

        var replicaCount = dto.Values.SingleOrDefault(v => v.Path == "replicaCount");
        await Assert.That(replicaCount).IsNotNull();
        await Assert.That(replicaCount!.Value).IsEqualTo("2");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_ProjectsThePerMemberSizingOntoTheChartResources()
    {
        // Q15 — one sizing per deploymentSite, applied identically to every member. A chart value is exactly
        // that: it renders into each replica's pod spec unchanged.
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());
        deploymentSite.PoolMemberCpuRequest = "250m";
        deploymentSite.PoolMemberCpuLimit = "1";
        deploymentSite.PoolMemberMemoryRequest = "512Mi";
        deploymentSite.PoolMemberMemoryLimit = "1Gi";

        var dto = await DeployAndCaptureAsync(deploymentSite);

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
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.Values.Any(v => v.Path.StartsWith("resources.", StringComparison.Ordinal)))
            .IsFalse();
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_DoesNotOverruleAPinnedReplicaCount()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 2);
        deploymentSite.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord { Path = "replicaCount", Value = "4", IsSecret = false },
        };

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.Values.Single(v => v.Path == "replicaCount").Value).IsEqualTo("4");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_NeverReceivesTheClusterDataStoreCredentials()
    {
        // 🔴 The flag hands over the cluster's SHARED Mongo / CrateDB credentials. A deploymentSite member
        // runs work for tenants other than the one that owns it, and the lease is what gives it one
        // tenant at a time — a standing credential to all of them makes that lease decorative. The
        // operator refuses the same thing independently.
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());
        deploymentSite.ReceivesClusterSecrets = true;

        var dto = await DeployAndCaptureAsync(deploymentSite);

        await Assert.That(dto.ReceivesClusterSecrets).IsFalse();
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_MovesItToPending()
    {
        // Without a DeploymentState writer for the deploymentSite type this silently did nothing: the deploymentSite
        // stayed at its default state, Studio never showed it deployed, and Undeploy refused it as
        // "already not deployed".
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool());

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, deploymentSite.RtId);

        await CommunicationRepository.Received(1).SetAdapterPoolDeploymentStateAsync(
            TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == deploymentSite.RtId
                                     && id.CkTypeId == SystemCommunicationCkIds.RtCkAdapterPoolTypeId),
            RtDeploymentStateEnum.Pending,
            Arg.Any<string?>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterPool_TellsTheOperatorItIsAPool()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), deploymentState: RtDeploymentStateEnum.Deployed);

        await DeploymentSiteService.UndeployWorkloadAsync(TenantId, deploymentSite.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(d => d.WorkloadType == WorkloadTypeDto.AdapterPool));
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_WithinTheRange_RequestsExactlyThatManyMembers()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 1, maxReplicas: 3,
            deploymentState: RtDeploymentStateEnum.Deployed);

        var effective = await DeploymentSiteService.ScaleAdapterPoolAsync(TenantId, deploymentSite.RtId, 3);

        await Assert.That(effective).IsEqualTo(3);
        await WorkloadLifecycleService.Received(1).RequestScaleAsync(TenantId, deploymentSite, 3);
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_BelowMinReplicas_ReportsTheFloorAsTheEffectiveCount()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), minReplicas: 1, maxReplicas: 3,
            deploymentState: RtDeploymentStateEnum.Deployed);

        var effective = await DeploymentSiteService.ScaleAdapterPoolAsync(TenantId, deploymentSite.RtId, 0);

        await Assert.That(effective).IsEqualTo(1);
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_UndeployedPool_IsRefused()
    {
        var deploymentSite = ArrangeAdapterPool(ArrangeCloudPool(), deploymentState: RtDeploymentStateEnum.Undeployed);

        await Assert.ThrowsAsync<DeploymentSiteServiceException>(
            () => DeploymentSiteService.ScaleAdapterPoolAsync(TenantId, deploymentSite.RtId, 2));

        await WorkloadLifecycleService.DidNotReceiveWithAnyArgs().RequestScaleAsync(
            Arg.Any<string>(), Arg.Any<RtDeployableWorkload>(), Arg.Any<int>());
    }

    [Test]
    public async Task ScaleAdapterPoolAsync_OnAnOrdinaryAdapter_IsRefused()
    {
        // An Adapter's replica count is owned by the on-demand lifecycle (hibernate / wake). Only a
        // deploymentSite has a range to move within, so scaling one through this path would silently fight
        // the lifecycle state machine.
        var site = ArrangeCloudPool();
        var adapter = Helper.RtEntityCreator.CreateAdapter();
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId).Returns(site);

        await Assert.ThrowsAsync<DeploymentSiteServiceException>(
            () => DeploymentSiteService.ScaleAdapterPoolAsync(TenantId, adapter.RtId, 2));
    }
}
