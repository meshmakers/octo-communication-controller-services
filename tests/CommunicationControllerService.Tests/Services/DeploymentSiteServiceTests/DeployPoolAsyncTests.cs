using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

internal class DeployPoolAsyncTests : PoolServiceTestsBase
{
    private async Task GivenCloudPool()
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });
        // GetWorkloadsForDeploymentSiteAsync — default empty
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(Array.Empty<RtDeployableWorkload>());
        await Task.CompletedTask;
    }

    private async Task GivenEdgePool()
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Edge,
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });
        await Task.CompletedTask;
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_NotifiesPoolDeployed()
    {
        await GivenCloudPool();

        await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteDeployedAsync(
            Arg.Is<DeployedDeploymentSiteDto>(d => d.TenantId == TenantId && d.DeploymentSiteRtId == DeploymentSiteRtId.ToString()));
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_NoWorkloads_DoesNotNotifyWorkloads()
    {
        await GivenCloudPool();

        await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.DidNotReceive()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployPoolAsync_EdgePool_ThrowsAndLeavesStateUntouched()
    {
        // Edge deploymentSites cannot be Deploy-ed via this controller. The DB state
        // must NOT be flipped to Disabled here — DeploymentState reflects
        // physical operator state (a deploymentSite that was previously Cloud-deployed
        // and then switched to Edge is still physically Deployed until the
        // user runs Undeploy). The reject is precise: "EdgePoolNotDeployable",
        // independent of current DeploymentState.
        await GivenEdgePool();

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId));
        await Assert.That(ex!.Message).Contains("Edge");

        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetDeploymentSiteDeploymentStateAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>(),
                Arg.Any<RtDeploymentStateEnum>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyDeploymentSiteDeployedAsync(Arg.Any<DeployedDeploymentSiteDto>());
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task UndeployPoolAsync_EdgePoolPreviouslyDeployed_CleansUpAndRestsAtDisabled()
    {
        // Cloud→Edge transition without an intermediate Undeploy leaves the
        // deploymentSite physically Deployed in the central cluster. Undeploy must
        // still work — notify the operator to clean up, then set the
        // resting state to Disabled (since Environment is now Edge).
        await GivenEdgePool();
        var deploymentSite = (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single();
        deploymentSite.DeploymentState = RtDeploymentStateEnum.Deployed;

        await DeploymentSiteService.UndeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1)
            .NotifyDeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId.ToString());
        await CommunicationRepository.Received(1).SetDeploymentSiteDeploymentStateAsync(TenantId, DeploymentSiteRtId,
            RtDeploymentStateEnum.Disabled);
    }

    [Test]
    public async Task UndeployPoolAsync_AlreadyUndeployed_ThrowsAlreadyNotDeployed()
    {
        // Nothing to clean up — both Undeployed and Disabled are terminal
        // resting states.
        await GivenCloudPool();
        var deploymentSite = (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single();
        deploymentSite.DeploymentState = RtDeploymentStateEnum.Undeployed;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.UndeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyDeploymentSiteUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UndeployPoolAsync_AlreadyDisabled_ThrowsAlreadyNotDeployed()
    {
        await GivenEdgePool();
        var deploymentSite = (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single();
        deploymentSite.DeploymentState = RtDeploymentStateEnum.Disabled;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.UndeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyDeploymentSiteUndeployedAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterInEdgePool_NotifiesOperatorAndFlipsToPending()
    {
        // Edge deploymentSites are deployable at the workload level — the edge operator
        // receives WorkloadDeployedAsync via RegisterDeploymentSiteForConnection routing
        // and runs the same helm upgrade --install path as the central operator.
        // Only the deploymentSite itself (CR + broker secret) is central-cluster-only.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        var deploymentSite = (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single();
        deploymentSite.Environment = RtEnvironmentEnum.Edge;

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.TenantId == TenantId
                && d.DeploymentSiteRtId == DeploymentSiteRtId.ToString()
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
        var deploymentSite = (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single();
        deploymentSite.Environment = RtEnvironmentEnum.Edge;
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;

        await DeploymentSiteService.UndeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w =>
                w.TenantId == TenantId
                && w.DeploymentSiteRtId == DeploymentSiteRtId.ToString()
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
            async () => await DeploymentSiteService.UndeployWorkloadAsync(TenantId, adapter.RtId));
        await Assert.That(ex!.Message).Contains("nothing to undeploy");

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadUndeployedAsync(Arg.Any<WorkloadUndeployedDto>());
    }

    [Test]
    public async Task UndeployWorkloadAsync_AdapterCloud_RestsAtUndeployed()
    {
        // Normal Cloud-deploymentSite undeploy: notify operator, rest at Undeployed.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.DeploymentState = RtDeploymentStateEnum.Deployed;

        await DeploymentSiteService.UndeployWorkloadAsync(TenantId, adapter.RtId);

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

        await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await CommunicationRepository.Received(1).SetDeploymentSiteDeploymentStateAsync(TenantId, DeploymentSiteRtId,
            RtDeploymentStateEnum.Deployed);
    }

    [Test]
    public async Task UndeployPoolAsync_CloudPool_NotifiesWorkloadsBeforePool()
    {
        await GivenCloudPool();
        // UndeployPool requires the deploymentSite to be in a deployable state (the
        // controller would otherwise throw PoolAlreadyNotDeployed).
        (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single().DeploymentState =
            RtDeploymentStateEnum.Deployed;
        // Pretend two workloads were tracked from an earlier deploy.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, DeploymentSiteRtId = DeploymentSiteRtId.ToString(),
                WorkloadName = "wl-a", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, DeploymentSiteRtId = DeploymentSiteRtId.ToString(),
                WorkloadName = "wl-b", WorkloadType = WorkloadTypeDto.Application,
            },
        });

        await DeploymentSiteService.UndeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        // Both workload notifies fired, and the deploymentSite notify after them.
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-a"));
        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-b"));
        await OperatorConnectionManager.Received(1).NotifyDeploymentSiteUndeployedAsync(TenantId, DeploymentSiteRtId.ToString());
    }

    [Test]
    public async Task DeployPoolAsync_CloudPool_DoesNotFanOutWorkloads()
    {
        // C decoupling: deploymentSite-deploy notifies the operator about the deploymentSite
        // only; workloads are deployed via explicit DeployWorkloadAsync
        // calls. This test pins the fan-out is gone.
        await GivenCloudPoolWithAdapter(receivesClusterSecrets: true);

        await DeploymentSiteService.DeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithReceivesClusterSecretsTrue_PropagatesFlagToDto()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: true);

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithReceivesClusterSecretsFalse_DefaultsDtoFlagToFalse()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => !d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithReceivesClusterSecretsFalse_DefaultsDtoFlagToFalse()
    {
        var (_, application) = await GivenCloudPoolWithApplication(receivesClusterSecrets: false);

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

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

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ReceivesClusterSecrets));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterMissingChartName_ThrowsWithSpecificMessage()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.ChartName = string.Empty;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

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

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

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
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

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
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

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

        await DeploymentSiteService.UndeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w =>
                w.TenantId == TenantId
                && w.DeploymentSiteRtId == DeploymentSiteRtId.ToString()
                && w.WorkloadName == "test-adapter"
                && w.WorkloadType == WorkloadTypeDto.Adapter));
    }

    private async Task<(RtDeploymentSite DeploymentSite, RtAdapter Adapter)> GivenCloudPoolWithAdapter(bool receivesClusterSecrets)
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });

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
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { adapter });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId)
            .Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(rtDeploymentSite);

        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });
        await Task.CompletedTask;
        return (rtDeploymentSite, adapter);
    }

    private async Task<(RtDeploymentSite DeploymentSite, RtApplication Application)> GivenCloudPoolWithApplication(
        bool receivesClusterSecrets = false)
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });

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
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { application });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, application.RtId)
            .Returns(application);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, application.RtId)
            .Returns(rtDeploymentSite);

        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, application.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });
        await Task.CompletedTask;
        return (rtDeploymentSite, application);
    }

    [Test]
    public async Task UndeployPoolAsync_CloudPool_OnlyUndeploysWorkloadsOfThisPool()
    {
        await GivenCloudPool();
        (await CommunicationRepository.GetDeploymentSitesAsync(TenantId)).Single().DeploymentState =
            RtDeploymentStateEnum.Deployed;
        // Two workloads, one in a different deploymentSite — must not be undeployed here.
        OperatorConnectionManager.GetDeployedWorkloadsForTenant(TenantId).Returns(new[]
        {
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, DeploymentSiteRtId = DeploymentSiteRtId.ToString(),
                WorkloadName = "wl-here", WorkloadType = WorkloadTypeDto.Adapter,
            },
            new WorkloadUndeployedDto
            {
                TenantId = TenantId, DeploymentSiteRtId = "65d5c447b420da3fb12381cc",
                WorkloadName = "wl-elsewhere", WorkloadType = WorkloadTypeDto.Adapter,
            },
        });

        await DeploymentSiteService.UndeployDeploymentSiteAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-here"));
        await OperatorConnectionManager.DidNotReceive().NotifyWorkloadUndeployedAsync(
            Arg.Is<WorkloadUndeployedDto>(w => w.WorkloadName == "wl-elsewhere"));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterIngressEnabledWithHostname_PropagatesFieldsToDto()
    {
        // Public-ingress opt-in is a typed attribute on DeployableWorkload (so
        // both Adapter and Application carry it). The controller copies it
        // straight onto the DTO; the operator then projects ingress.enabled +
        // publicUri into the workload's Helm values.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = true;
        adapter.Hostname = "adapter.staging.octo-mesh.com";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.IngressEnabled
                && d.Hostname == "adapter.staging.octo-mesh.com"));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterIngressDisabled_HostnameKeptOnDtoButDisabledFlagWins()
    {
        // IngressEnabled=false → operator emits no ingress.enabled overlay
        // regardless of Hostname. The DTO still carries the hostname for
        // diagnostics / future re-enable; the operator just ignores it.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = false;
        adapter.Hostname = "adapter.staging.octo-mesh.com";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                !d.IngressEnabled
                && d.Hostname == "adapter.staging.octo-mesh.com"));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterIngressEnabledNoHostname_ThrowsBeforeNotify()
    {
        // The chart's templates/ingress.yaml would render an Ingress with an
        // empty host rule (k8s admission rejects it). Fail fast at Deploy time
        // with an actionable message instead of letting the helm release fail
        // mid-rollout.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = true;
        adapter.Hostname = string.Empty;

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("Hostname");
        await Assert.That(ex!.Message).Contains("Ingress Enabled");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationIngressEnabledWithHostname_PropagatesFieldsToDto()
    {
        // Same contract on Application — the attributes were moved up onto
        // DeployableWorkload, so this is the no-regression test for the
        // Application path.
        var (_, application) = await GivenCloudPoolWithApplication(receivesClusterSecrets: false);
        application.IngressEnabled = true;
        application.Hostname = "energy.prod-1.octo-mesh.com";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.IngressEnabled
                && d.Hostname == "energy.prod-1.octo-mesh.com"));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterIngressDisabledNoHostname_DtoHostnameIsNull()
    {
        // Default values: IngressEnabled=false, Hostname is blank. Controller
        // normalises blank to null on the DTO so the operator sees "absent",
        // not an empty string. Pins the contract for the operator-side
        // string.IsNullOrWhiteSpace check.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = false;
        adapter.Hostname = string.Empty;

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => !d.IngressEnabled && d.Hostname == null));
    }

    [Test]
    public async Task DeployWorkloadAsync_HostnameWithKnownDomainTemplate_ResolvesBeforeNotify()
    {
        // Late-binding contract: the Hostname attribute carries a template like
        // "adapter.{{domain.default}}"; the controller resolves it against its
        // configured named domains at deploy time and sends the concrete host
        // to the operator. The workload entity itself stays cluster-portable.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = true;
        adapter.Hostname = "adapter.{{domain.default}}";

        TemplateResolver
            .TryResolve("adapter.{{domain.default}}", Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = "adapter.staging.octo-mesh.com";
                ci[3] = null;
                return true;
            });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.IngressEnabled
                && d.Hostname == "adapter.staging.octo-mesh.com"));
    }

    [Test]
    public async Task DeployWorkloadAsync_HostnameWithUnknownDomainTemplate_ThrowsBeforeNotify()
    {
        // Unknown domain name in the template fails fast with an actionable
        // message that names the offending key — same shape as the
        // IngressEnabled+empty-hostname guard above. The operator must NOT be
        // notified (no helm release attempt with a literal '{{domain.X}}' host).
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.IngressEnabled = true;
        adapter.Hostname = "adapter.{{domain.does-not-exist}}";

        TemplateResolver
            .TryResolve("adapter.{{domain.does-not-exist}}", Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = null;
                ci[3] = "domain.does-not-exist";
                return false;
            });

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("does-not-exist");
        await Assert.That(ex!.Message).Contains("Hostname");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_NonSecretValueOverrideWithTemplate_ResolvesBeforeNotify()
    {
        // Per-tenant URL constructed at deploy time from {{context.tenantId}}
        // and the cluster's Identity-Service authority. Pins the resolver
        // wiring on the ValueOverride path.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord
            {
                Path = "oauth.callbackUrl",
                Value = "{{service.authority}}/{{context.tenantId}}/callback",
                IsSecret = false,
            },
        };
        TemplateResolver
            .TryResolve("{{service.authority}}/{{context.tenantId}}/callback",
                Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = "https://identity.staging.octo-mesh.com/tenantId/callback";
                ci[3] = null;
                return true;
            });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.Values.Count == 1
                && d.Values[0].Path == "oauth.callbackUrl"
                && d.Values[0].Value == "https://identity.staging.octo-mesh.com/tenantId/callback"
                && !d.Values[0].IsSecret));
    }

    [Test]
    public async Task DeployWorkloadAsync_SecretValueOverride_NotSubstituted()
    {
        // Regression guard: the encryption/sentinel layer owns secret values.
        // The resolver must NOT see them — running TryResolve over decrypted
        // secret material would mix two contracts and could leak placeholder
        // text into the chart secret. The DTO carries the Decrypt() result
        // verbatim.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord
            {
                Path = "oauth.clientSecret",
                Value = "enc:v1:cipher",
                IsSecret = true,
            },
        };
        EncryptionService.Decrypt("enc:v1:cipher").Returns("plain-{{context.tenantId}}");

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        // The Decrypt output reaches the DTO with the placeholder text intact —
        // proves the resolver did NOT run over secret material. Running templating
        // over decrypted secret material would mix the encryption-sentinel layer
        // with the templating contract.
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.Values.Count == 1
                && d.Values[0].IsSecret
                && d.Values[0].Value == "plain-{{context.tenantId}}"));
    }

    [Test]
    public async Task DeployWorkloadAsync_ValuesYamlWithTemplate_ResolvesBeforeNotify()
    {
        // ValuesYaml is treated as one opaque string and resolved as a whole
        // before being passed to the operator. The operator writes it to a
        // -f file unchanged after that point.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.ValuesYaml = "oauth.authority: {{service.authority}}";
        // Predicate-based match so we don't have to thread an exact-string mock
        // through two consecutive resolver calls (EnsureWorkloadIsHelmDeployableAsync
        // + BuildWorkloadDeployedDtoAsync).
        TemplateResolver
            .TryResolve(Arg.Is<string?>(s => s != null && s.Contains("{{service.authority}}")),
                Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = "oauth.authority: https://identity.staging.octo-mesh.com";
                ci[3] = null;
                return true;
            });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.ValuesYaml == "oauth.authority: https://identity.staging.octo-mesh.com"));
    }

    [Test]
    public async Task DeployWorkloadAsync_ValueOverrideUnknownPlaceholder_ThrowsBeforeNotify()
    {
        // Unknown placeholder inside a ValueOverride fails fast with a
        // message that names the offending field path. Operator must NOT
        // be notified.
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord
            {
                Path = "oauth.callbackUrl",
                Value = "{{service.nope}}",
                IsSecret = false,
            },
        };
        TemplateResolver
            .TryResolve("{{service.nope}}", Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = null;
                ci[3] = "service.nope";
                return false;
            });

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("service.nope");
        await Assert.That(ex!.Message).Contains("ValueOverride[oauth.callbackUrl]");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_ValuesYamlUnknownPlaceholder_ThrowsBeforeNotify()
    {
        var (_, adapter) = await GivenCloudPoolWithAdapter(receivesClusterSecrets: false);
        adapter.ValuesYaml = "x: {{domain.missing}}";
        TemplateResolver
            .TryResolve("x: {{domain.missing}}", Arg.Any<WorkloadTemplateContext>(),
                out Arg.Any<string?>(), out Arg.Any<string?>())
            .Returns(ci =>
            {
                ci[2] = null;
                ci[3] = "domain.missing";
                return false;
            });

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await Assert.That(ex!.Message).Contains("domain.missing");
        await Assert.That(ex!.Message).Contains("ValuesYaml");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }
}
