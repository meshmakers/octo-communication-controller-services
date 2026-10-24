using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#7427 (Feature AB#7417): invalid typed CPU/memory settings are rejected at DeployWorkload with an
///     error that names the attribute, nothing reaches the operator, and DeploymentState/LastDeploymentError
///     carry the reason. Workload entities have no write hook, so the deploy is the enforcement net.
/// </summary>
internal class DeployWorkloadResourceValidationTests : PoolServiceTestsBase
{
    private T GivenWorkload<T>(T workload) where T : RtDeployableWorkload
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        workload.RtId = OctoObjectId.GenerateNewId();
        workload.ChartName = "octo-mesh-adapter";
        workload.ChartVersion = "0.1.1";

        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, workload.RtId).Returns(workload);
        CommunicationRepository.GetDeploymentSiteForWorkloadAsync(TenantId, workload.RtId).Returns(deploymentSite);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, workload.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://example.test/charts",
            });
        return workload;
    }

    private static RtAdapter NewAdapter() => new()
    {
        CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
        Name = "test-adapter",
    };

    private static RtApplication NewApplication() => new()
    {
        CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
        Name = "test-app",
    };

    private async Task AssertRejectedAsync(RtDeployableWorkload workload, params string[] expectedFragments)
    {
        var ex = await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, workload.RtId));

        using var _ = Assert.Multiple();
        foreach (var fragment in expectedFragments)
        {
            await Assert.That(ex!.Message).Contains(fragment);
        }

        await OperatorConnectionManager.DidNotReceiveWithAnyArgs()
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithMalformedMemoryRequest_NamesAttributeAndValueAndSendsNothing()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "512 MB";

        await AssertRejectedAsync(adapter, "MemoryRequest '512 MB' is not a valid Kubernetes quantity");
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithMalformedCpuLimit_IsRejected()
    {
        var application = GivenWorkload(NewApplication());
        application.CpuLimit = "two cores";

        await AssertRejectedAsync(application, "CpuLimit 'two cores' is not a valid Kubernetes quantity");
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterRejected_RecordsErrorStateWithTheReason()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "512 MB";

        await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId));

        await CommunicationRepository.Received(1).SetAdapterDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId), RtDeploymentStateEnum.Error,
            Arg.Is<string?>(m => m != null && m.Contains("MemoryRequest '512 MB'")));
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationRejected_RecordsErrorStateWithTheReason()
    {
        var application = GivenWorkload(NewApplication());
        application.MemoryLimit = "lots";

        await Assert.ThrowsAsync<Exception>(
            async () => await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId));

        await CommunicationRepository.Received(1).SetApplicationDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == application.RtId), RtDeploymentStateEnum.Error,
            Arg.Is<string?>(m => m != null && m.Contains("MemoryLimit 'lots'")));
    }

    [Test]
    public async Task DeployWorkloadAsync_FailingToRecordTheRejection_StillThrowsTheValidationError()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "512 MB";
        CommunicationRepository
            .SetAdapterDeploymentStateAsync(TenantId, Arg.Any<RtEntityId>(), RtDeploymentStateEnum.Error,
                Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("db down"));

        await AssertRejectedAsync(adapter, "MemoryRequest '512 MB'");
    }

    [Test]
    public async Task DeployWorkloadAsync_MemoryRequestAboveLimit_IsRejected()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "2Gi";
        adapter.MemoryLimit = "1Gi";

        await AssertRejectedAsync(adapter, "MemoryRequest 2Gi exceeds MemoryLimit 1Gi");
    }

    [Test]
    public async Task DeployWorkloadAsync_CpuRequestAboveLimit_IsRejected()
    {
        var application = GivenWorkload(NewApplication());
        application.CpuRequest = "500m";
        application.CpuLimit = "250m";

        await AssertRejectedAsync(application, "CpuRequest 500m exceeds CpuLimit 250m");
    }

    [Test]
    public async Task DeployWorkloadAsync_RequestAndLimitComparedOnParsedQuantities_1024MiEqualsOneGi()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "1024Mi";
        adapter.MemoryLimit = "1Gi";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_DecimalCpuAndMixedSuffixes_AreComparedNumerically()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.CpuRequest = "0.5";
        adapter.CpuLimit = "1";
        adapter.MemoryRequest = "512Ki";
        adapter.MemoryLimit = "1G";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_PlainNumberMemory_IsValid()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "536870912";
        adapter.MemoryLimit = "1Gi";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_ValidValues_PassValidation()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.CpuRequest = "100m";
        adapter.MemoryRequest = "512Mi";
        adapter.MemoryLimit = "1Gi";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_AllValuesEmptyOrWhitespace_PassValidation()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.CpuLimit = "  ";
        adapter.MemoryRequest = string.Empty;

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task DeployWorkloadAsync_RequestWithoutLimit_IsValid()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "8Gi";

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    [Arguments("CpuRequest")]
    [Arguments("CpuLimit")]
    [Arguments("MemoryRequest")]
    [Arguments("MemoryLimit")]
    public async Task DeployWorkloadAsync_AdapterPoolWithInheritedSizing_PointsToPoolMemberAttributes(
        string attribute)
    {
        var pool = GivenWorkload(new RtAdapterPool
        {
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "test-pool",
            MinReplicas = 1,
            MaxReplicas = 2,
        });
        switch (attribute)
        {
            case "CpuRequest": pool.CpuRequest = "100m"; break;
            case "CpuLimit": pool.CpuLimit = "1"; break;
            case "MemoryRequest": pool.MemoryRequest = "512Mi"; break;
            default: pool.MemoryLimit = "1Gi"; break;
        }

        await AssertRejectedAsync(pool, attribute, "PoolMemberCpuRequest", "PoolMemberCpuLimit",
            "PoolMemberMemoryRequest", "PoolMemberMemoryLimit");
        await CommunicationRepository.Received(1).SetAdapterPoolDeploymentStateAsync(TenantId,
            Arg.Is<RtEntityId>(id => id.RtId == pool.RtId), RtDeploymentStateEnum.Error,
            Arg.Is<string?>(m => m != null && m.Contains("PoolMember")));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPoolWithPoolMemberSizingOnly_Deploys()
    {
        var pool = GivenWorkload(new RtAdapterPool
        {
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "test-pool",
            MinReplicas = 1,
            MaxReplicas = 2,
            PoolMemberMemoryRequest = "512Mi",
            PoolMemberMemoryLimit = "1Gi",
        });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, pool.RtId);

        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }
}
