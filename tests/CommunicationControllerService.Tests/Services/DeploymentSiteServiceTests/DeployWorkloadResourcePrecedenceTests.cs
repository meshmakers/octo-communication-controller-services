using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DeploymentSiteServiceTests;

/// <summary>
///     AB#7428 (Feature AB#7417): the typed CPU/memory attributes reach the Helm release with the precedence
///     chart default &lt; typed attributes &lt; explicit <c>ValuesYaml</c> / <c>Values[]</c>. The merge is built in
///     the controller (no operator or DTO contract change); an explicit value that overrides a typed one is
///     reported in <c>LastDeploymentWarning</c>, in the log and to the caller.
/// </summary>
internal class DeployWorkloadResourcePrecedenceTests : PoolServiceTestsBase
{
    private const string OperatorOverridePath = "oauth.publicUri";
    private const string OperatorOverrideValue = "https://adapter.example.test";

    private T GivenWorkload<T>(T workload, string valuesYaml = "", params (string Path, string Value)[] values)
        where T : RtDeployableWorkload
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
        workload.ValuesYaml = valuesYaml;
        var list = new AttributeRecordValueList<RtValueOverrideRecord>
        {
            new RtValueOverrideRecord { Path = OperatorOverridePath, Value = OperatorOverrideValue, IsSecret = false },
        };
        foreach (var (path, value) in values)
        {
            list.Add(new RtValueOverrideRecord { Path = path, Value = value, IsSecret = false });
        }

        workload.Values = list;

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

    private async Task<WorkloadDeployedDto> DeployAndCaptureAsync(RtDeployableWorkload workload)
    {
        await DeploymentSiteService.DeployWorkloadAsync(TenantId, workload.RtId);

        var call = OperatorConnectionManager.ReceivedCalls()
            .Single(c => c.GetMethodInfo().Name == nameof(OperatorConnectionManager.NotifyWorkloadDeployedAsync));
        return (WorkloadDeployedDto)call.GetArguments()[0]!;
    }

    private static string[] ResourcePaths(WorkloadDeployedDto dto) => dto.Values
        .Where(v => v.Path.StartsWith("resources.", StringComparison.OrdinalIgnoreCase))
        .Select(v => v.Path).ToArray();

    [Test]
    public async Task DeployWorkloadAsync_AdapterWithOnlyMemoryRequest_SendsOnlyThatResourcePath()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "512Mi";

        var dto = await DeployAndCaptureAsync(adapter);

        using var _ = Assert.Multiple();
        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory"]);
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.memory").Value).IsEqualTo("512Mi");
        await Assert.That(dto.Values.Single(v => v.Path == OperatorOverridePath).Value).IsEqualTo(OperatorOverrideValue);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetWorkloadDeploymentWarningAsync(default!, default!, default);
    }

    [Test]
    public async Task DeployWorkloadAsync_ApplicationWithOnlyMemoryRequest_SendsOnlyThatResourcePath()
    {
        var application = GivenWorkload(NewApplication());
        application.ChartName = "octo-mesh-demo-app";
        application.MemoryRequest = "512Mi";

        var dto = await DeployAndCaptureAsync(application);

        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory"]);
    }

    [Test]
    public async Task DeployWorkloadAsync_AllFourTypedValues_AreProjectedToTheFourPaths()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.CpuRequest = "100m";
        adapter.CpuLimit = " 1 ";
        adapter.MemoryRequest = "512Mi";
        adapter.MemoryLimit = "1Gi";

        var dto = await DeployAndCaptureAsync(adapter);

        using var _ = Assert.Multiple();
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.cpu").Value).IsEqualTo("100m");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.limits.cpu").Value).IsEqualTo("1");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.memory").Value).IsEqualTo("512Mi");
        await Assert.That(dto.Values.Single(v => v.Path == "resources.limits.memory").Value).IsEqualTo("1Gi");
        await Assert.That(dto.Values.Where(v => v.Path.StartsWith("resources.")).All(v => !v.IsSecret)).IsTrue();
    }

    [Test]
    public async Task DeployWorkloadAsync_NoTypedValues_DtoIsUnchangedAndNoWarning()
    {
        const string yaml = "replicaCount: 2";
        var adapter = GivenWorkload(NewAdapter(), yaml);

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(warning).IsNull();
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ValuesYaml == yaml && d.Values.Count == 1
                && d.Values[0].Path == OperatorOverridePath && d.Values[0].Value == OperatorOverrideValue));
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetWorkloadDeploymentWarningAsync(default!, default!, default);
    }

    [Test]
    public async Task DeployWorkloadAsync_ExplicitValuesYaml_WinsAndWarns()
    {
        const string yaml = "resources: {requests: {memory: 1Gi}}";
        var adapter = GivenWorkload(NewAdapter(), yaml);
        adapter.MemoryRequest = "512Mi";

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        const string expected =
            "MemoryRequest 512Mi is overridden by an explicit Helm value (ValuesYaml resources.requests.memory)";
        using var _ = Assert.Multiple();
        await Assert.That(warning).IsEqualTo(expected);
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.ValuesYaml == yaml
                && !d.Values.Any(v => v.Path.StartsWith("resources."))));
        await CommunicationRepository.Received(1)
            .SetWorkloadDeploymentWarningAsync(TenantId, adapter, expected);
    }

    [Test]
    public async Task DeployWorkloadAsync_ExplicitValuesEntry_WinsAndWarnsCaseInsensitive()
    {
        var adapter = GivenWorkload(NewAdapter(), string.Empty, ("Resources.Requests.Memory", "2Gi"));
        adapter.MemoryRequest = "512Mi";

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(warning).IsEqualTo(
            "MemoryRequest 512Mi is overridden by an explicit Helm value (Values[] resources.requests.memory)");
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d =>
                d.Values.Count(v => v.Path.Equals("resources.requests.memory", StringComparison.OrdinalIgnoreCase)) == 1
                && d.Values.Single(v => v.Path == "Resources.Requests.Memory").Value == "2Gi"));
    }

    [Test]
    public async Task DeployWorkloadAsync_OnlyTheConflictingValueIsDropped_OthersStillProjected()
    {
        const string yaml = "resources:\n  limits:\n    memory: 4Gi\n";
        var adapter = GivenWorkload(NewAdapter(), yaml);
        adapter.MemoryRequest = "512Mi";
        adapter.MemoryLimit = "2Gi";
        adapter.CpuRequest = "250m";

        var dto = await DeployAndCaptureAsync(adapter);

        using var _ = Assert.Multiple();
        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory", "resources.requests.cpu"]);
        await CommunicationRepository.Received(1).SetWorkloadDeploymentWarningAsync(TenantId, adapter,
            "MemoryLimit 2Gi is overridden by an explicit Helm value (ValuesYaml resources.limits.memory)");
    }

    [Test]
    public async Task DeployWorkloadAsync_TwoConflicts_AreReportedInOneWarning()
    {
        const string yaml = "resources: {requests: {memory: 1Gi}}";
        var adapter = GivenWorkload(NewAdapter(), yaml, ("resources.limits.cpu", "2"));
        adapter.MemoryRequest = "512Mi";
        adapter.CpuLimit = "1";

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await Assert.That(warning).IsEqualTo(
            "CpuLimit 1 is overridden by an explicit Helm value (Values[] resources.limits.cpu); " +
            "MemoryRequest 512Mi is overridden by an explicit Helm value (ValuesYaml resources.requests.memory)");
    }

    [Test]
    public async Task DeployWorkloadAsync_ExplicitValueEqualToTypedOne_IsNotAConflict()
    {
        var adapter = GivenWorkload(NewAdapter(), "resources: {requests: {memory: 1024Mi}}");
        adapter.MemoryRequest = "1Gi";

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await Assert.That(warning).IsNull();
    }

    [Test]
    public async Task DeployWorkloadAsync_EmptyChartResourcesInValuesYaml_DoNotCountAsExplicit()
    {
        var adapter = GivenWorkload(NewAdapter(), "resources: {}\nreplicaCount: 2");
        adapter.MemoryRequest = "512Mi";

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory"]);
    }

    [Test]
    public async Task DeployWorkloadAsync_InvalidValuesYaml_DoesNotCountAsExplicit()
    {
        var adapter = GivenWorkload(NewAdapter(), "resources: [unclosed");
        adapter.MemoryRequest = "512Mi";

        var dto = await DeployAndCaptureAsync(adapter);

        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory"]);
    }

    [Test]
    public async Task DeployWorkloadAsync_ConflictFreeDeployAfterAWarning_ClearsLastDeploymentWarning()
    {
        var adapter = GivenWorkload(NewAdapter());
        adapter.MemoryRequest = "512Mi";
        adapter.LastDeploymentWarning = "MemoryRequest 512Mi is overridden by an explicit Helm value (ValuesYaml resources.requests.memory)";

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await Assert.That(warning).IsNull();
        await CommunicationRepository.Received(1).SetWorkloadDeploymentWarningAsync(TenantId, adapter, null);
    }

    [Test]
    public async Task DeployWorkloadAsync_SameWarningAlreadyStored_IsNotWrittenAgain()
    {
        const string expected =
            "MemoryRequest 512Mi is overridden by an explicit Helm value (ValuesYaml resources.requests.memory)";
        var adapter = GivenWorkload(NewAdapter(), "resources: {requests: {memory: 1Gi}}");
        adapter.MemoryRequest = "512Mi";
        adapter.LastDeploymentWarning = expected;

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await Assert.That(warning).IsEqualTo(expected);
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .SetWorkloadDeploymentWarningAsync(default!, default!, default);
    }

    [Test]
    public async Task DeployWorkloadAsync_WarningCannotBeStored_StillDeploys()
    {
        var adapter = GivenWorkload(NewAdapter(), "resources: {requests: {memory: 1Gi}}");
        adapter.MemoryRequest = "512Mi";
        CommunicationRepository
            .SetWorkloadDeploymentWarningAsync(TenantId, Arg.Any<RtDeployableWorkload>(), Arg.Any<string?>())
            .ThrowsAsync(new InvalidOperationException("db down"));

        var warning = await DeploymentSiteService.DeployWorkloadAsync(TenantId, adapter.RtId);

        await Assert.That(warning).IsNotNull();
        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(Arg.Any<WorkloadDeployedDto>());
    }

    [Test]
    public async Task ReconcilePendingWorkloadsAsync_ResendsTheTypedValues_WithIsReconciliation()
    {
        // A pool re-registration / controller restart re-dispatches a Pending workload through the
        // reconciliation entry point; the typed settings must be sent again, not only on a manual deploy.
        var adapter = GivenWorkload(NewAdapter());
        adapter.DeploymentState = RtDeploymentStateEnum.Pending;
        adapter.MemoryRequest = "512Mi";
        adapter.CpuLimit = "1";

        await DeploymentSiteService.ReconcilePendingWorkloadsAsync(TenantId, DeploymentSiteRtId);

        await OperatorConnectionManager.Received(1).NotifyWorkloadDeployedAsync(
            Arg.Is<WorkloadDeployedDto>(d => d.IsReconciliation
                && d.Values.Any(v => v.Path == "resources.requests.memory" && v.Value == "512Mi")
                && d.Values.Any(v => v.Path == "resources.limits.cpu" && v.Value == "1")));
    }

    [Test]
    public async Task DeployWorkloadAsync_AdapterPool_KeepsItsPoolMemberSizingPath()
    {
        var pool = GivenWorkload(new RtAdapterPool
        {
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "test-pool",
            MinReplicas = 1,
            MaxReplicas = 2,
            PoolMemberMemoryRequest = "512Mi",
        });

        var dto = await DeployAndCaptureAsync(pool);

        await Assert.That(ResourcePaths(dto)).IsEquivalentTo(["resources.requests.memory"]);
        await Assert.That(dto.Values.Single(v => v.Path == "resources.requests.memory").Value).IsEqualTo("512Mi");
    }
}
