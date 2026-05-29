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
            Arg.Is<DeployedPoolDto>(d => d.TenantId == TenantId && d.PoolRtId == PoolRtId.ToString()));
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
    public async Task DeployPoolAsync_EdgePool_ThrowsAndLeavesStateUntouched()
    {
        // Edge pools cannot be Deploy-ed via this controller. The DB state
        // must NOT be flipped to Disabled here — DeploymentState reflects
        // physical operator state (a pool that was previously Cloud-deployed
        // and then switched to Edge is still physically Deployed until the
        // user runs Undeploy). The reject is precise: "EdgePoolNotDeployable",
        // independent of current DeploymentState.
        await GivenEdgePool();

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.DeployPoolAsync(TenantId, PoolRtId));
        await Assert.That(ex!.Message).Contains("Edge");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetPoolDeploymentStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtDeploymentStateEnum>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolDeployedAsync(Arg.Any<DeployedPoolDto>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task UndeployPoolAsync_EdgePoolPreviouslyDeployed_CleansUpAndRestsAtDisabled()
    {
        // Cloud→Edge transition without an intermediate Undeploy leaves the
        // pool physically Deployed in the central cluster. Undeploy must
        // still work — notify the operator to clean up, then set the
        // resting state to Disabled (since Environment is now Edge).
        await GivenEdgePool();
        var pool = (await CommunicationRepository.GetPoolsAsync(TenantId)).Single();
        pool.DeploymentState = RtDeploymentStateEnum.Deployed;

        await PoolService.UndeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.Received(1)
            .NotifyPoolUndeployedAsync(TenantId, PoolRtId.ToString());
        await CommunicationRepository.Received(1).SetPoolDeploymentStateAsync(TenantId, PoolRtId,
            RtDeploymentStateEnum.Disabled);
    }

    [Test]
    public async Task UndeployPoolAsync_AlreadyUndeployed_ThrowsAlreadyNotDeployed()
    {
        // Nothing to clean up — both Undeployed and Disabled are terminal
        // resting states.
        await GivenCloudPool();
        var pool = (await CommunicationRepository.GetPoolsAsync(TenantId)).Single();
        pool.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.UndeployPoolAsync(TenantId, PoolRtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UndeployPoolAsync_AlreadyDisabled_ThrowsAlreadyNotDeployed()
    {
        await GivenEdgePool();
        var pool = (await CommunicationRepository.GetPoolsAsync(TenantId)).Single();
        pool.DeploymentState = RtDeploymentStateEnum.Disabled;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.UndeployPoolAsync(TenantId, PoolRtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyPoolUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterInEdgePool_NotifiesOperatorAndFlipsToPending()
    {
        // Edge pools are deployable at the workload level — the edge operator
        // receives WorkloadDeployedAsync via RegisterPoolForConnection routing
        // and runs the same helm upgrade --install path as the central operator.
        // Only the pool itself (CR + broker secret) is central-cluster-only.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        var pool = (await CommunicationRepository.GetPoolsAsync(TenantId)).Single();
        pool.Environment = RtEnvironmentEnum.Edge;

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.TenantId == TenantId
                && d.PoolRtId == PoolRtId.ToString()
                && d.WorkloadName == "test-adapter"));
        await CommunicationRepository.Received(1).SetAdapterDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId),
            RtDeploymentStateEnum.Pending, Arg.Any<string?>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterPreviouslyDeployedInEdgePool_CleansUpAndRestsAtUndeployed()
    {
        // Edge alone is no longer a disabling rule for workloads — the edge
        // operator deploys workloads via the same helm path as central, so
        // a re-deploy is possible. Resting state is Undeployed (the workload
        // still has its Helm fields), not Disabled.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        var pool = (await CommunicationRepository.GetPoolsAsync(TenantId)).Single();
        pool.Environment = RtEnvironmentEnum.Edge;
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;

        await PoolService.UndeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w =>
                w.TenantId == TenantId
                && w.PoolRtId == PoolRtId.ToString()
                && w.WorkloadName == "test-adapter"));
        await CommunicationRepository.Received(1).SetAdapterDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId),
            RtDeploymentStateEnum.Undeployed, Arg.Any<string?>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterAlreadyUndeployed_Throws()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.UndeployWorkloadAsync(TenantId, adapter.RtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadUndeployedAsync(Arg.Any<WorkloadUndeployedDto>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterCloud_RestsAtUndeployed()
    {
        // Normal Cloud-pool undeploy: notify operator, rest at Undeployed.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;

        await PoolService.UndeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Any<WorkloadUndeployedDto>());
        await CommunicationRepository.Received(1).SetAdapterDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId),
            RtDeploymentStateEnum.Undeployed, Arg.Any<string?>());
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
        // UndeployPool requires the pool to be in a deployable state (the
        // controller would otherwise throw PoolAlreadyNotDeployed).
        (await CommunicationRepository.GetPoolsAsync(TenantId)).Single().DeploymentState =
            RtDeploymentStateEnum.Deployed;
        // Pretend two workloads were tracked from an earlier deploy.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolRtId = PoolRtId.ToString(),
                WorkloadName = "wl-a", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolRtId = PoolRtId.ToString(),
                WorkloadName = "wl-b", WorkloadType = WorkloadTypeDto.Application,
            },
        });

        await PoolService.UndeployPoolAsync(TenantId, PoolRtId);

        // Both workload notifies fired, and the pool notify after them.
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-a"));
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-b"));
        await OperatorConnectionManager.Received(1).NotifyPoolUndeployedAsync(TenantId, PoolRtId.ToString());
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_DoesNotFanOutWorkloads()
    {
        // C decoupling: pool-deploy notifies the operator about the pool
        // only; workloads are deployed via explicit DeployWorkloadAsync
        // calls. This test pins the fan-out is gone.
        await GivenCloudPoolWithAdapter(receivesClusterSecrets: true);

        await PoolService.DeployPoolAsync(TenantId, PoolRtId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithReceivesClusterSecretsTrue_PropagatesFlagToDto()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: true);

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithReceivesClusterSecretsFalse_DefaultsDtoFlagToFalse()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => !d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithReceivesClusterSecretsFalse_DefaultsDtoFlagToFalse()
    {
        var (_, application) = await GivenCloudPoolWithApplication(receivesClusterSecrets: false);

        await PoolService.DeployWorkloadAsync(TenantId, application.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => !d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithReceivesClusterSecretsTrue_PropagatesFlagToDto()
    {
        // Applications with a backend (e.g. energy-community, voest-app) opt
        // in to cluster-credential injection via the same flag as adapters —
        // the attribute lives on DeployableWorkload so both types carry it.
        var (_, application) = await GivenCloudPoolWithApplication(receivesClusterSecrets: true);

        await PoolService.DeployWorkloadAsync(TenantId, application.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterMissingChartName_ThrowsWithSpecificMessage()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.ChartName = string.Empty;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("Chart Name");
        await Assert.That(ex!.Message).Contains("test-adapter");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterEmptyChartVersion_DeploysAsLatest()
    {
        // Empty ChartVersion is the explicit "use latest from configured repo"
        // signal — see EnsureWorkloadIsHelmDeployableAsync. Deploy continues
        // through to NotifyWorkloadDeployedAsync; the operator's HelmRunner
        // omits --version downstream and helm picks the newest chart.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.ChartVersion = string.Empty;

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.ChartVersion == string.Empty));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithoutLinkedHelmRepository_ThrowsWithSpecificMessage()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns((RtHelmRepositoryConfiguration?)null);

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("Helm repository");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterHelmRepositoryUrlEmpty_ThrowsWithSpecificMessage()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = string.Empty,
            });

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("Repository URL");
        await Assert.That(ex!.Message).Contains("test-adapter");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_NotifiesOperator()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;

        await PoolService.UndeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w =>
                w.TenantId == TenantId
                && w.PoolRtId == PoolRtId.ToString()
                && w.WorkloadName == "test-adapter"
                && w.WorkloadType == WorkloadTypeDto.Adapter));
    }

    private async Task<(RtPool Pool, RtAdapter Adapter)> GivenCloudPoolWithAdapter(bool receivesClusterSecrets)
    {
        var rtPool = new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = PoolName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { rtPool });

        var adapter = new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "test-adapter",
            ChartName = "octo-mesh-adapter",
            ChartVersion = "0.1.1",
            ValuesYaml = string.Empty,
            ReceivesClusterSecrets = receivesClusterSecrets,
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
        await Task.CompletedTask;
        return (rtPool, adapter);
    }

    private async Task<(RtPool Pool, RtApplication Application)> GivenCloudPoolWithApplication(
        bool receivesClusterSecrets = false)
    {
        var rtPool = new RtPool
        {
            RtId = PoolRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = PoolName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetPoolsAsync(TenantId).Returns(new[] { rtPool });

        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "test-app",
            ChartName = "test-app",
            ChartVersion = "0.0.1",
            ValuesYaml = string.Empty,
            ReceivesClusterSecrets = receivesClusterSecrets,
        };
        CommunicationRepository.GetWorkloadsForPoolAsync(TenantId, PoolRtId)
            .Returns(new RtDeployableWorkload[] { application });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, application.RtId)
            .Returns(application);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, application.RtId)
            .Returns(rtPool);

        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, application.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });
        await Task.CompletedTask;
        return (rtPool, application);
    }

    [Test]
    public async Task UndeployPoolAsync_CloudPool_OnlyUndeploysWorkloadsOfThisPool()
    {
        await GivenCloudPool();
        (await CommunicationRepository.GetPoolsAsync(TenantId)).Single().DeploymentState =
            RtDeploymentStateEnum.Deployed;
        // Two workloads, one in a different pool — must not be undeployed here.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolRtId = PoolRtId.ToString(),
                WorkloadName = "wl-here", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, PoolRtId = "65d5c447b420da3fb12381cc",
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
