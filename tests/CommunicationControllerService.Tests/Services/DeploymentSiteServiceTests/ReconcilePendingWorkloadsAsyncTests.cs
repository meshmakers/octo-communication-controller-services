using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#4894: a workload deploy notification sent while the operator pod was being replaced is
///     lost silently (fire-and-forget SendAsync to a dying connection), stranding the entity in
///     DeploymentState=Pending forever. On every deploymentSite (re-)registration the controller therefore
///     re-dispatches whatever is still Pending — best effort, never failing the registration.
/// </summary>
internal class ReconcilePendingWorkloadsAsyncTests : PoolServiceTestsBase
{
    private RtDeploymentSite _rtPool = null!;

    private RtAdapter GivenPoolWithAdapterInState(RtDeploymentStateEnum state)
    {
        _rtPool = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        var rtDeploymentSite = _rtPool;
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });

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
        return adapter;
    }

    [Test]
    public async Task PendingWorkload_IsRedispatched()
    {
        // Arrange
        var adapter = GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);

        // Act
        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

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
        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        // Assert
        await OperatorConnectionManager.DidNotReceive()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task WorkloadLookupFails_DoesNotThrow()
    {
        // Arrange — the lookup can race a concurrent tenant-update cache unload; the
        // registration path must survive that.
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .ThrowsAsync(new InvalidOperationException("cache unloaded"));

        // Act & Assert — no throw.
        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

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
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { broken, second });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, broken.RtId)
            .Returns((RtDeployableWorkload?)null);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, second.RtId)
            .Returns(second);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, second.RtId)
            .Returns(_rtPool);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, second.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });

        // Act
        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        // Assert
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.WorkloadRtId == second.RtId.ToString()));
    }

    [Test]
    public async Task UnpinnedWorkload_IsStillRedispatchedButWarnsAboutTheVersion()
    {
        // AB#4955: an empty ChartVersion resolves to "newest in the repository" at helm
        // upgrade time. Because this dispatch is triggered by a deploymentSite re-registration —
        // an operator restart, a blueprint re-apply, a CK-model update — the workload can
        // come back on a version nobody chose. Recovery still has to happen (that is what
        // AB#4894 is for), so the dispatch stays; it must not stay silent though.
        var adapter = GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);
        adapter.ChartVersion = string.Empty;

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.WorkloadRtId == adapter.RtId.ToString()));
        await CommunicationEventService.Received(1).StoreWarningEventAsync(
            TenantId,
            Arg.Is<string>(m => m.Contains("without a pinned chart version")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task PinnedWorkload_IsRedispatchedWithoutAVersionWarning()
    {
        // A pinned workload comes back on exactly the version it was running, so the
        // re-dispatch is unremarkable and stays an information event.
        GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        await CommunicationEventService.DidNotReceive().StoreWarningEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
        // DeployWorkloadAsync writes its own "deploy requested" event, so assert on the
        // reconcile message rather than on the call count.
        await CommunicationEventService.Received(1).StoreInformationEventAsync(
            TenantId,
            Arg.Is<string>(m => m.Contains("still Pending when its deploymentSite re-registered")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task Redispatch_MarksTheDeployAsAReconciliation()
    {
        // AB#4955: the flag is what lets the operator tell "restore what was running" apart from
        // "give me the newest chart", which is the whole reason an unpinned workload could drift.
        GivenPoolWithAdapterInState(RtDeploymentStateEnum.Pending);

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => w.IsReconciliation));
    }

    [Test]
    public async Task UserTriggeredDeploy_IsNotMarkedAsAReconciliation()
    {
        // The counterpart: an explicit Deploy is a release decision, so an empty ChartVersion
        // must keep meaning "newest in the repository".
        var adapter = GivenPoolWithAdapterInState(RtDeploymentStateEnum.Deployed);

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(w => !w.IsReconciliation));
    }
}
