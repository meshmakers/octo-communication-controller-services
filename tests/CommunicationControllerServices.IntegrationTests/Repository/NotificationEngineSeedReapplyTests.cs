using System.Runtime.CompilerServices;
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
///     AB#2585 — re-applying the <c>System.Communication.Notifications</c> seed (a blueprint version bump)
///     keeps the engine pipeline's <c>DeploymentState</c>, against real MongoDB. Drives the real seed file
///     through <c>IImportRtModelCommand</c> with <c>Upsert</c>, the command every blueprint apply uses.
/// </summary>
[Collection("CommunicationController")]
public class NotificationEngineSeedReapplyTests(CommunicationControllerFixture fixture)
{
    private const string DataFlowRtId = "670000000000000000002580";
    private const string PipelineRtId = "670000000000000000002581";
    private const string AdapterRtId = "670000000000000000000002";

    [Fact]
    public async Task DeploymentState_IsPreservedOnUpsert_InTheCompiledModel()
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        var pipelineType = await tenantRepository.GetCkTypeGraphAsync(SystemCommunicationCkIds.RtCkPipelineTypeId);

        var deploymentState = pipelineType.AllAttributes.Values
            .Single(a => a.AttributeName.Equals(nameof(RtPipeline.DeploymentState), StringComparison.OrdinalIgnoreCase));

        deploymentState.Ownership.Should().Be(AttributeOwnershipDto.RuntimeState);
        deploymentState.Ownership.IsPreservedOnUpsert().Should().BeTrue();
    }

    [Fact]
    public async Task ReapplyingTheSeed_KeepsADeployedEngineDeployed()
    {
        var (seed, pipelineId) = await InstallSeedAsync();
        (await LoadAsync(pipelineId)).DeploymentState.Should().Be(RtDeploymentStateEnum.Undeployed);

        await fixture.GetService<ICommunicationRepository>().SetPipelineDeploymentStateAsync(fixture.TestTenantId,
            pipelineId, RtDeploymentStateEnum.Deployed, null);
        await ImportAsync(seed.Replace("value: Notification Engine Pipeline", "value: Notification Engine Pipeline v2"),
            ImportStrategy.Upsert);

        var pipeline = await LoadAsync(pipelineId);
        pipeline.DeploymentState.Should().Be(RtDeploymentStateEnum.Deployed);
        pipeline.Name.Should().Be("Notification Engine Pipeline v2", "the re-apply really replaced the entity");
    }

    [Fact]
    public async Task ReapplyingTheSeed_LeavesAnUndeployedEngineUndeployed()
    {
        var (seed, pipelineId) = await InstallSeedAsync();

        await ImportAsync(seed, ImportStrategy.Upsert);

        (await LoadAsync(pipelineId)).DeploymentState.Should().Be(RtDeploymentStateEnum.Undeployed);
    }

    /// <summary>
    ///     Installs the real seed under fresh rtIds (the fixture's tenant is shared), on a fresh adapter for
    ///     its <c>Executes</c> edge.
    /// </summary>
    private async Task<(string Seed, RtEntityId PipelineId)> InstallSeedAsync()
    {
        var adapterRtId = OctoObjectId.GenerateNewId().ToString();
        var pipelineRtId = OctoObjectId.GenerateNewId().ToString();
        var seed = (await File.ReadAllTextAsync(SeedPath()))
            .Replace(DataFlowRtId, OctoObjectId.GenerateNewId().ToString())
            .Replace(PipelineRtId, pipelineRtId)
            .Replace(AdapterRtId, adapterRtId);

        await ImportAsync(AdapterYaml(adapterRtId), ImportStrategy.Insert);
        await ImportAsync(seed, ImportStrategy.Upsert);
        return (seed, new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, new OctoObjectId(pipelineRtId)));
    }

    private async Task ImportAsync(string yaml, ImportStrategy strategy)
    {
        var tenantRepository = await fixture.GetSystemContext().FindTenantRepositoryAsync(fixture.TestTenantId);
        await fixture.GetService<IImportRtModelCommand>().ImportTextAsync(tenantRepository, yaml, strategy);
    }

    private async Task<RtPipeline> LoadAsync(RtEntityId pipelineId) =>
        (await fixture.GetService<ICommunicationRepository>().GetPipelineAsync(fixture.TestTenantId, pipelineId))!;

    private static string SeedPath([CallerFilePath] string sourceFile = "") =>
        Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", "..", "..", "src", "SystemCommunicationCkModel",
            "Blueprints", "System.Communication.Notifications", "seed-data", "entities.yaml");

    private static string AdapterYaml(string rtId) =>
        $$"""
        $schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
        dependencies:
          - System.Communication-[4.5,5.0)
        entities:
          - rtId: '{{rtId}}'
            ckTypeId: System.Communication/Adapter
            attributes:
              - id: System/Name
                value: Mesh Adapter
              - id: System.Communication/DeploymentState
                value: 0
              - id: System.Communication/CommunicationState
                value: 0
              - id: System.Communication/ConfigurationState
                value: 0
              - id: System.Communication/LastSyncedSequenceNumber
                value: 0
        """;
}
