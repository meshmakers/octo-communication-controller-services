using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
/// Pins the AB#4984 deploy-time lifecycle validation in
/// <c>DeploymentSiteService.EnsureWorkloadIsHelmDeployableAsync</c>: OnDemand deploys are rejected for
/// non-capable workloads (process-bound triggers) and for Application workloads, and the
/// reserved LifecycleMode 'Auto' is rejected until implemented.
/// </summary>
internal class DeployWorkloadLifecycleValidationTests : PoolServiceTestsBase
{
    private (RtDeploymentSite DeploymentSite, RtAdapter Adapter) GivenEdgePoolWithAdapter(RtLifecycleModeEnum lifecycleMode)
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            // Edge routing keeps the arrange minimal — validation runs before any
            // operator-connection routing either way.
            Environment = RtEnvironmentEnum.Edge,
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
            LifecycleMode = lifecycleMode,
        };
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, adapter.RtId).Returns(adapter);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, adapter.RtId).Returns(rtDeploymentSite);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, adapter.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });

        return (rtDeploymentSite, adapter);
    }

    [Test]
    public async Task DeployWorkloadAsync_OnDemandButNotCapable_IsRejectedWithReasons()
    {
        var (_, adapter) = GivenEdgePoolWithAdapter(RtLifecycleModeEnum.OnDemand);
        OnDemandCapabilityService
            .EvaluateAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(false,
                ["Pipeline 'sync' uses process-bound trigger 'FromPolling@1'"]));

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("FromPolling@1");
        await Assert.That(ex!.Message).Contains("OnDemand");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_OnDemandAndCapable_Deploys()
    {
        var (_, adapter) = GivenEdgePoolWithAdapter(RtLifecycleModeEnum.OnDemand);

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.TenantId == TenantId && d.WorkloadName == "test-adapter"));
    }

    [Test]
    public async Task DeployWorkloadAsync_LifecycleModeAuto_IsRejected()
    {
        var (_, adapter) = GivenEdgePoolWithAdapter(RtLifecycleModeEnum.Auto);

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("Auto");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_OnDemandApplication_IsRejected()
    {
        var rtDeploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Edge,
        };
        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { rtDeploymentSite });

        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "test-app",
            ChartName = "custom-app",
            ValuesYaml = string.Empty,
            LifecycleMode = RtLifecycleModeEnum.OnDemand,
        };
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, application.RtId).Returns(application);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, application.RtId).Returns(rtDeploymentSite);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, application.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });

        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId));

        using var _ = Assert.Multiple();
        await Assert.That(ex!.Message).Contains("adapter workloads only");
        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }
}
