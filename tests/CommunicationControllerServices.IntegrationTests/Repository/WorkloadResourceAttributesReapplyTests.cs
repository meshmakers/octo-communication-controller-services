using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Exchange;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Repository;

/// <summary>
///     AB#7426 (Feature AB#7417) — the typed sizing attributes of System.Communication 4.10.0
///     (<c>CpuRequest</c>, <c>CpuLimit</c>, <c>MemoryRequest</c>, <c>MemoryLimit</c>,
///     <c>LastDeploymentWarning</c>) are runtime state on every workload kind, and a blueprint re-apply keeps
///     the value an operator set afterwards. Sits next to the AB#6312 / AB#4706 tests for
///     <c>AdapterConfiguration</c> / <c>ValuesYaml</c> / <c>Values</c>, against real MongoDB.
/// </summary>
/// <remarks>
///     Layer: like <see cref="AdapterConfigurationOwnershipTests" />, the test drives
///     <c>IImportRtModelCommand</c> with <c>Upsert</c>, the command every blueprint apply imports its seed
///     through and the place where the attribute ownership is honoured. The orchestration above it
///     (UpdateBlueprint, Merge mode) belongs to the engine and asset-repository stories. The values are made up.
/// </remarks>
[Collection("CommunicationController")]
public class WorkloadResourceAttributesReapplyTests(CommunicationControllerFixture fixture)
{
    private const string OperatorMemoryRequest = "512Mi";

    public static TheoryData<string> WorkloadKinds => new()
    {
        "System.Communication/Adapter",
        "System.Communication/Application",
        "System.Communication/AdapterPool",
    };

    [Theory]
    [MemberData(nameof(WorkloadKinds))]
    public async Task AllFiveAttributes_AreOptionalRuntimeState_OnEveryWorkloadKind(string ckTypeId)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        var type = await tenantRepository.GetCkTypeGraphAsync(ckTypeId switch
        {
            "System.Communication/Adapter" => SystemCommunicationCkIds.RtCkAdapterTypeId,
            "System.Communication/Application" => SystemCommunicationCkIds.RtCkApplicationTypeId,
            _ => SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
        });

        string[] names =
        [
            nameof(RtDeployableWorkload.CpuRequest), nameof(RtDeployableWorkload.CpuLimit),
            nameof(RtDeployableWorkload.MemoryRequest), nameof(RtDeployableWorkload.MemoryLimit),
            nameof(RtDeployableWorkload.LastDeploymentWarning),
        ];

        foreach (var name in names)
        {
            var attribute = type.AllAttributes.Values
                .Single(a => a.AttributeName.Equals(name, StringComparison.OrdinalIgnoreCase));

            attribute.Ownership.Should().Be(AttributeOwnershipDto.RuntimeState, name);
            attribute.Ownership.IsPreservedOnUpsert().Should().BeTrue(name);
            attribute.IsOptional.Should().BeTrue(name);
        }
    }

    [Theory]
    [MemberData(nameof(WorkloadKinds))]
    public async Task Reapply_SeedOmittingMemoryRequest_KeepsTheOperatorsValue(string ckTypeId)
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart", memoryRequest: null), ImportStrategy.Insert);
        await SetMemoryRequestAsOperatorAsync(ckTypeId, rtId);

        // The blueprint update: same rtId, a seed that never mentions the sizing, and a changed seed-owned value.
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart-v2", memoryRequest: null), ImportStrategy.Upsert);

        var workload = await LoadAsync(ckTypeId, rtId);
        workload.MemoryRequest.Should().Be(OperatorMemoryRequest);
        // Control: the import really replaced the entity.
        workload.ChartName.Should().Be("octo-chart-v2");
    }

    [Theory]
    [MemberData(nameof(WorkloadKinds))]
    public async Task Reapply_SeedWithEmptyMemoryRequest_KeepsTheOperatorsValue(string ckTypeId)
    {
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart", memoryRequest: null), ImportStrategy.Insert);
        await SetMemoryRequestAsOperatorAsync(ckTypeId, rtId);

        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart-v2", memoryRequest: ""), ImportStrategy.Upsert);

        var workload = await LoadAsync(ckTypeId, rtId);
        workload.MemoryRequest.Should().Be(OperatorMemoryRequest);
        workload.ChartName.Should().Be("octo-chart-v2");
    }

    [Theory]
    [MemberData(nameof(WorkloadKinds))]
    public async Task Reapply_SeedWithADifferentMemoryRequest_KeepsTheOperatorsValue(string ckTypeId)
    {
        // The discriminating case: omitted / empty seed values are also guarded for seed-owned attributes
        // (AB#6313), but a seed carrying its OWN non-empty default replaces a seed-owned value. Only
        // runtime state keeps what the operator chose.
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart", memoryRequest: null), ImportStrategy.Insert);
        await SetMemoryRequestAsOperatorAsync(ckTypeId, rtId);

        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart-v2", memoryRequest: "128Mi"), ImportStrategy.Upsert);

        var workload = await LoadAsync(ckTypeId, rtId);
        workload.MemoryRequest.Should().Be(OperatorMemoryRequest);
        workload.ChartName.Should().Be("octo-chart-v2");
    }

    [Theory]
    [MemberData(nameof(WorkloadKinds))]
    public async Task ExistingWorkload_WithoutTheAttributes_HasThemEmpty(string ckTypeId)
    {
        // A workload that was stored before 4.10.0 (or simply never sized) reads all five as empty.
        var rtId = OctoObjectId.GenerateNewId();
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart", memoryRequest: null), ImportStrategy.Insert);

        var workload = await LoadAsync(ckTypeId, rtId);
        workload.CpuRequest.Should().BeNullOrEmpty();
        workload.CpuLimit.Should().BeNullOrEmpty();
        workload.MemoryRequest.Should().BeNullOrEmpty();
        workload.MemoryLimit.Should().BeNullOrEmpty();
        workload.LastDeploymentWarning.Should().BeNullOrEmpty();
    }

    /// <summary>The operator sets the sizing on the live entity, after the blueprint install.</summary>
    private async Task SetMemoryRequestAsOperatorAsync(string ckTypeId, OctoObjectId rtId)
    {
        // Same choke point as an `ImportRt -r` the operator runs: Upsert with the attribute present
        // (a fresh runtime-state value is written, only an EXISTING value is preserved).
        await ImportAsync(WorkloadSeed(ckTypeId, rtId, "octo-chart", OperatorMemoryRequest), ImportStrategy.Upsert);
        (await LoadAsync(ckTypeId, rtId)).MemoryRequest.Should().Be(OperatorMemoryRequest);
    }

    private async Task ImportAsync(string yaml, ImportStrategy strategy)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        await fixture.GetService<IImportRtModelCommand>().ImportTextAsync(tenantRepository, yaml, strategy);
    }

    private async Task<RtDeployableWorkload> LoadAsync(string ckTypeId, OctoObjectId rtId) =>
        (await fixture.GetService<ICommunicationRepository>()
            .GetWorkloadByRtIdAsync(fixture.TestTenantId, rtId))!;

    /// <summary>A workload seed of the given kind. <paramref name="memoryRequest" /> null omits the attribute.</summary>
    private static string WorkloadSeed(string ckTypeId, OctoObjectId rtId, string chartName, string? memoryRequest)
    {
        var memoryYaml = memoryRequest == null
            ? string.Empty
            : $$"""

                  - id: System.Communication/MemoryRequest
                    value: '{{memoryRequest}}'
            """;

        return $$"""
            $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
            dependencies:
              - System.Communication-[4.10,5.0)
            entities:
              - rtId: '{{rtId}}'
                ckTypeId: {{ckTypeId}}
                attributes:
                  - id: System/Name
                    value: sized-workload
                  - id: System.Communication/ChartName
                    value: {{chartName}}
                  - id: System.Communication/DeploymentState
                    value: 0{{memoryYaml}}
            """;
    }
}
