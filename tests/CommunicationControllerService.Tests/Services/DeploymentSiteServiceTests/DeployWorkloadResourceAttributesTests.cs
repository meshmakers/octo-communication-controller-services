using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#7426 (Feature AB#7417): System.Communication 4.10.0 adds the typed sizing attributes
///     <c>CpuRequest</c>, <c>CpuLimit</c>, <c>MemoryRequest</c>, <c>MemoryLimit</c> and
///     <c>LastDeploymentWarning</c> to <c>DeployableWorkload</c>. This story is schema-only: the controller
///     does not read them yet (a later story of the Feature projects them into the Helm values). What it
///     must guarantee already is that a workload that never sets them renders exactly the Helm values it
///     rendered on 4.9.0.
/// </summary>
internal class DeployWorkloadResourceAttributesTests : PoolServiceTestsBase
{
    private const string OperatorValuesYaml = "replicaCount: 2";
    private const string OperatorOverridePath = "oauth.publicUri";
    private const string OperatorOverrideValue = "https://adapter.example.test";

    [Test]
    public async Task Schema_AllThreeWorkloadKinds_CarryTheFiveOptionalStringAttributes()
    {
        // Compile-time proof that the attributes sit on the abstract base and reach Adapter,
        // Application and AdapterPool; all five are optional, hence nullable strings.
        RtDeployableWorkload[] workloads =
        [
            new RtAdapter(), new RtApplication(), new RtAdapterPool(),
        ];

        foreach (var workload in workloads)
        {
            await Assert.That(workload.CpuRequest).IsNull();
            await Assert.That(workload.CpuLimit).IsNull();
            await Assert.That(workload.MemoryRequest).IsNull();
            await Assert.That(workload.MemoryLimit).IsNull();
            await Assert.That(workload.LastDeploymentWarning).IsNull();

            workload.MemoryRequest = "512Mi";
            await Assert.That(workload.MemoryRequest).IsEqualTo("512Mi");
        }
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithoutTypedResources_RendersTheSameValuesAsBefore()
    {
        var adapter = GivenWorkload(new RtAdapter
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "test-adapter",
            ChartName = "octo-mesh-adapter",
        });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await AssertUnchangedValuesAsync();
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithoutTypedResources_RendersTheSameValuesAsBefore()
    {
        var application = GivenWorkload(new RtApplication
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "test-app",
            ChartName = "test-app",
        });

        await DeploymentSiteService.DeployWorkloadAsync(TenantId, application.RtId);

        await AssertUnchangedValuesAsync();
    }

    private async Task AssertUnchangedValuesAsync()
    {
        // Exactly what the operator set and nothing else: no `resources.*` path appears, the base
        // values.yaml is passed through untouched.
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.ValuesYaml == OperatorValuesYaml
                && d.Values.Count == 1
                && d.Values[0].Path == OperatorOverridePath
                && d.Values[0].Value == OperatorOverrideValue
                && !d.Values[0].IsSecret));
    }

    private T GivenWorkload<T>(T workload) where T : RtDeployableWorkload
    {
        var deploymentSite = new RtDeploymentSite
        {
            RtId = DeploymentSiteRtId,
            CkTypeId = SystemCommunicationCkIds.RtCkDeploymentSiteTypeId,
            Name = DeploymentSiteName,
            Environment = RtEnvironmentEnum.Cloud,
        };
        workload.ChartVersion = "0.1.1";
        workload.ValuesYaml = OperatorValuesYaml;
        workload.Values = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord { Path = OperatorOverridePath, Value = OperatorOverrideValue, IsSecret = false },
        };

        CommunicationRepository.GetDeploymentSitesAsync(TenantId).Returns(new[] { deploymentSite });
        CommunicationRepository.GetWorkloadsForDeploymentSiteAsync(TenantId, DeploymentSiteRtId)
            .Returns(new RtDeployableWorkload[] { workload });
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
}
