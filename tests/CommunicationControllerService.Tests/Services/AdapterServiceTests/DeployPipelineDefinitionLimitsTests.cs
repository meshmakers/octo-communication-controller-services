using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
///     AB#9667 — the size limits of <see cref="PipelineDefinitionLimits" /> run on every deploy path, whether or not
///     <c>EnablePipelineSchemaValidation</c> is on and whether or not the adapter reported a schema.
/// </summary>
/// <remarks>
///     The gates after the schema validation parse the definition with YamlDotNet, which binds an alias to the shared
///     instance, and walk its nodes recursively. A small document of nested aliases therefore made the walk visit an
///     exponential number of nodes in the default configuration (flag off), where the bounded schema validation never
///     ran. Every test here uses the default flag or a missing schema on purpose.
/// </remarks>
internal class DeployPipelineDefinitionLimitsTests : AdapterServiceTestsBase
{
    /// <summary>
    ///     Ten levels of ten aliases: YamlDotNet parses it into shared lists quickly, a recursive walk visits 10^10
    ///     nodes. The shared converter counts the expansion and refuses it.
    /// </summary>
    private static string NestedAliasDefinition()
    {
        var sb = new StringBuilder();
        sb.AppendLine("l0: &l0 [{type: FromCustomThing@1}]");
        for (var level = 1; level <= 10; level++)
        {
            sb.Append($"l{level}: &l{level} [");
            sb.Append(string.Join(", ",
                Enumerable.Range(0, 10).Select(_ => $"{{type: If@1, transformations: *l{level - 1}}}")));
            sb.AppendLine("]");
        }

        sb.AppendLine("triggers: [{type: FromCustomThing@1}]");
        sb.AppendLine("transformations: *l10");
        return sb.ToString();
    }

    private (RtAdapter Adapter, RtPipeline Pipeline, RtDataFlow DataFlow) ArrangeDedicatedPipeline(
        string? storedDefinition)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline(storedDefinition);

        // No pipeline schema: the adapter reported none.
        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(), null, []));

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId).Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetWorkloadByRtIdAsync(TenantId, rtAdapter.RtId).Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId()).Returns([rtPipeline]);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtDataFlow.RtId).Returns([rtPipeline]);
        CommunicationRepository.GetAdapterByPipelineAsync(TenantId, rtPipeline.ToRtEntityId()).Returns(rtAdapter);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter, rtPipeline, rtDataFlow);
    }

    private async Task AssertRefusedBeforeAnyWrite(AdapterServiceException? ex)
    {
        using var _ = Assert.Multiple();
        await Assert.That(ex).IsNotNull();
        await Assert.That(ex!.Message).Contains(PipelineDefinitionLimits.RefusedCode);
        await CommunicationRepository.DidNotReceive()
            .SetPipelineDefinitionAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>(), Arg.Any<int?>());
        await CommunicationRepository.DidNotReceive()
            .SyncPipelineDataConnectionsAsync(Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<string>());
        await AdapterHubCallbacks.DidNotReceive().AdapterConfigurationUpdatedAsync(Arg.Any<string>(),
            Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    [Timeout(30_000)]
    public async Task DeployPipelineAsync_SchemaValidationOff_NestedAliases_AreRefusedBeforeAnyWrite(
        CancellationToken cancellationToken)
    {
        await Assert.That(ControllerOptions.EnablePipelineSchemaValidation).IsFalse();
        var (adapter, pipeline, _) = ArrangeDedicatedPipeline(storedDefinition: null);

        var ex = await Assert.That(async () => await AdapterService.DeployPipelineAsync(TenantId,
                adapter.ToRtEntityId(), pipeline.ToRtEntityId(), NestedAliasDefinition()))
            .Throws<AdapterServiceException>();

        await AssertRefusedBeforeAnyWrite(ex);
        PipelineSchemaValidator.DidNotReceive().Validate(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    [Timeout(30_000)]
    public async Task DeployPipelineAsync_SchemaValidationOnButNoAdapterSchema_NestedAliases_AreRefused(
        CancellationToken cancellationToken)
    {
        ControllerOptions.EnablePipelineSchemaValidation = true;
        var (adapter, pipeline, _) = ArrangeDedicatedPipeline(storedDefinition: null);

        var ex = await Assert.That(async () => await AdapterService.DeployPipelineAsync(TenantId,
                adapter.ToRtEntityId(), pipeline.ToRtEntityId(), NestedAliasDefinition()))
            .Throws<AdapterServiceException>();

        await AssertRefusedBeforeAnyWrite(ex);
    }

    [Test]
    [Timeout(30_000)]
    public async Task DeployPipelineAsync_WithoutADefinition_StoredNestedAliases_AreRefused(
        CancellationToken cancellationToken)
    {
        var (adapter, pipeline, _) = ArrangeDedicatedPipeline(storedDefinition: NestedAliasDefinition());

        var ex = await Assert.That(async () => await AdapterService.DeployPipelineAsync(TenantId,
                adapter.ToRtEntityId(), pipeline.ToRtEntityId(), null))
            .Throws<AdapterServiceException>();

        await AssertRefusedBeforeAnyWrite(ex);
    }

    [Test]
    [Timeout(30_000)]
    public async Task DeployDataFlowAsync_StoredNestedAliases_AreRefusedBeforeAnyWrite(
        CancellationToken cancellationToken)
    {
        var (_, _, dataFlow) = ArrangeDedicatedPipeline(storedDefinition: NestedAliasDefinition());

        var ex = await Assert.That(async () => await AdapterService.DeployDataFlowAsync(TenantId, dataFlow.RtId))
            .Throws<AdapterServiceException>();

        await AssertRefusedBeforeAnyWrite(ex);
    }

    [Test]
    public async Task DeployPipelineAsync_SchemaValidationOff_MoreNodesThanTheLimit_AreRefused()
    {
        // Valid YAML the adapter could parse; before AB#9667 nothing bounded it with the flag off.
        var nodes = string.Join("\n",
            Enumerable.Range(0, PipelineDefinitionLimits.MaxPipelineNodes + 1).Select(_ => "  - type: Map@1"));
        var definition = $"triggers:\n  - type: FromCustomThing@1\ntransformations:\n{nodes}\n";
        var (adapter, pipeline, _) = ArrangeDedicatedPipeline(storedDefinition: null);

        var ex = await Assert.That(async () => await AdapterService.DeployPipelineAsync(TenantId,
                adapter.ToRtEntityId(), pipeline.ToRtEntityId(), definition))
            .Throws<AdapterServiceException>();

        await AssertRefusedBeforeAnyWrite(ex);
        await Assert.That(ex!.Message).Contains($"more than {PipelineDefinitionLimits.MaxPipelineNodes} nodes");
    }
}
