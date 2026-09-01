using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PoolServiceTests;

/// <summary>
/// AB#5027 phase 2: deploying an Adapter workload is the closest this service has to "an adapter was
/// created" — adapters are RtEntities written through the asset repository, so there is no create
/// hook here, but nothing runs pipelines before its workload is deployed. Provisioning here closes
/// the window between an operator adding an adapter and the next tenant load.
/// </summary>
internal class DeployWorkloadServiceAccountProvisioningTests : PoolServiceTestsBase
{
    private RtPool ArrangeCloudPool()
    {
        var pool = new RtPool
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = "cloud-pool",
            Environment = RtEnvironmentEnum.Cloud
        };
        return pool;
    }

    private void ArrangeDeployableWorkload(RtPool pool, RtDeployableWorkload workload)
    {
        workload.ChartName = "octo-mesh-adapter";
        workload.ChartVersion = "1.0.0";

        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, workload.RtId).Returns(workload);
        CommunicationRepository.GetPoolForWorkloadAsync(TenantId, workload.RtId).Returns(pool);
        CommunicationRepository.GetHelmRepositoryForWorkloadAsync(TenantId, workload.RtId)
            .Returns(new RtHelmRepositoryConfiguration
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkHelmRepositoryConfigurationTypeId,
                RepositoryUrl = "https://charts.example.com"
            });
    }

    [Test]
    public async Task DeployWorkloadAsync_Adapter_ProvisionsItsPipelineServiceAccount()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "mesh-adapter";
        ArrangeDeployableWorkload(pool, adapter);

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await ServiceAccountProvisioningService.Received(1).EnsureAdapterProvisionedAsync(TenantId, adapter);
    }

    [Test]
    public async Task DeployWorkloadAsync_Application_DoesNotProvision()
    {
        var pool = ArrangeCloudPool();
        var application = new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "some-app"
        };
        ArrangeDeployableWorkload(pool, application);

        await PoolService.DeployWorkloadAsync(TenantId, application.RtId);

        // An Application executes no pipelines and therefore has no pipeline identity.
        await ServiceAccountProvisioningService.DidNotReceiveWithAnyArgs()
            .EnsureAdapterProvisionedAsync(Arg.Any<string>(), Arg.Any<RtAdapter>());
    }

    [Test]
    public async Task DeployWorkloadAsync_ProvisioningThrows_DoesNotFailTheDeploy()
    {
        var pool = ArrangeCloudPool();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangeDeployableWorkload(pool, adapter);
        // Defence in depth: EnsureAdapterProvisionedAsync is contractually non-throwing, but a
        // helm rollout must not depend on that contract holding.
        ServiceAccountProvisioningService
            .EnsureAdapterProvisionedAsync(TenantId, adapter)
            .ThrowsAsync(new InvalidOperationException("identity unreachable"));

        await PoolService.DeployWorkloadAsync(TenantId, adapter.RtId);

        // The deploy notification goes out before provisioning is attempted, and the state write
        // after it — so neither is skipped by a broken identity service.
        using var _ = Assert.Multiple();
        await OperatorConnectionManager.Received(1)
            .NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
        await CommunicationRepository.Received(1)
            .SetAdapterDeploymentStateAsync(TenantId, adapter.ToRtEntityId(),
                RtDeploymentStateEnum.Pending, Arg.Any<string?>());
    }
}
