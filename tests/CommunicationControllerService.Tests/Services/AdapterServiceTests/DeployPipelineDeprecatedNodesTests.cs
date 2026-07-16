using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

/// <summary>
/// Tests for the deprecated-node warning events written during pipeline deploy.
/// Deprecation is reported by the adapter via <see cref="NodeDescriptorDto.IsDeprecated"/>.
/// </summary>
internal class DeployPipelineDeprecatedNodesTests : AdapterServiceTestsBase
{
    private const string DefinitionWithDeprecatedNode =
        """
        triggers:
          - type: FromHttpRequest@1
        transformations:
          - type: OldNode@1
            path: $
        """;

    private static NodeDescriptorDto CreateDescriptor(string nodeName, bool isDeprecated,
        string? deprecationMessage = null)
    {
        return new NodeDescriptorDto(nodeName, 1, "Transform", false, false, "{}",
            isDeprecated, deprecationMessage);
    }

    private (RtEntityId AdapterRtEntityId, RtEntityId PipelineRtEntityId) ArrangeDeployablePipeline(
        string pipelineDefinition, params NodeDescriptorDto[] nodeDescriptors)
    {
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        rtPipeline.PipelineDefinition = pipelineDefinition;

        var adapter = AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto(
            rtAdapter.ToRtEntityId(),
            null,
            []
        ));
        adapter.SetNodeDescriptors(nodeDescriptors);

        CommunicationRepository.GetPipelineAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(rtPipeline);
        CommunicationRepository.GetDataFlowByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(rtDataFlow);
        CommunicationRepository.GetAdapterAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns(rtAdapter);
        CommunicationRepository.GetPipelinesAsync(TenantId, rtAdapter.ToRtEntityId())
            .Returns([rtPipeline]);
        CommunicationRepository.GetConfigurationsByPipelineAsync(TenantId, rtPipeline.RtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>([]));

        return (rtAdapter.ToRtEntityId(), rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task DeployPipelineAsync_PipelineUsesDeprecatedNode_StoresWarningEvent()
    {
        // Arrange
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(DefinitionWithDeprecatedNode,
            CreateDescriptor("OldNode", isDeprecated: true, "Use NewNode@1 instead"),
            CreateDescriptor("FromHttpRequest", isDeprecated: false));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // Assert
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(msg => msg.Contains("OldNode@1") && msg.Contains("Use NewNode@1 instead")),
            pipelineRtEntityId);
    }

    [Test]
    public async Task DeployPipelineAsync_DeprecatedNodeWithoutMessage_StoresWarningEvent()
    {
        // Arrange
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(DefinitionWithDeprecatedNode,
            CreateDescriptor("OldNode", isDeprecated: true));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // Assert
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(msg => msg.Contains("deprecated node 'OldNode@1'")),
            pipelineRtEntityId);
    }

    [Test]
    public async Task DeployPipelineAsync_NoDeprecatedNodesUsed_StoresNoWarningEvent()
    {
        // Arrange - a deprecated descriptor exists, but the pipeline does not use that node type
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(
            """
            triggers:
              - type: FromHttpRequest@1
            transformations:
              - type: NewNode@1
                path: $
            """,
            CreateDescriptor("OldNode", isDeprecated: true, "Use NewNode@1 instead"),
            CreateDescriptor("NewNode", isDeprecated: false));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // Assert
        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task DeployPipelineAsync_DuplicateDeprecatedDescriptors_StoresSingleWarningEvent()
    {
        // Arrange - defensive: duplicate NodeName@Version entries (incl. casing variant) must not throw
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(DefinitionWithDeprecatedNode,
            CreateDescriptor("OldNode", isDeprecated: true, "Use NewNode@1 instead"),
            CreateDescriptor("OldNode", isDeprecated: true),
            CreateDescriptor("oldnode", isDeprecated: true));

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // Assert
        await CommunicationEventService.Received(1).StoreWarningEventAsync(TenantId,
            Arg.Is<string>(msg => msg.Contains("OldNode@1")),
            pipelineRtEntityId);
    }

    [Test]
    public async Task DeployPipelineAsync_EventStoreThrows_DeployStillSucceeds()
    {
        // Arrange - best-effort contract: a failing event store must never fail the deploy
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(DefinitionWithDeprecatedNode,
            CreateDescriptor("OldNode", isDeprecated: true));
        CommunicationEventService.StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>())
            .Returns<Task>(_ => throw new InvalidOperationException("event store down"));

        // Act & Assert - no exception
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        await AdapterHubCallbacks.Received(1).AdapterConfigurationUpdatedAsync(TenantId,
            Arg.Any<AdapterConfigurationDto>());
    }

    [Test]
    public async Task DeployPipelineAsync_AdapterWithoutNodeDescriptors_StoresNoWarningEvent()
    {
        // Arrange - older adapter version that did not report node descriptors
        var (adapterRtEntityId, pipelineRtEntityId) = ArrangeDeployablePipeline(DefinitionWithDeprecatedNode);
        AdapterTenant.AdapterById[adapterRtEntityId].SetNodeDescriptors(null);

        // Act
        await AdapterService.DeployPipelineAsync(TenantId, adapterRtEntityId, pipelineRtEntityId);

        // Assert
        await CommunicationEventService.DidNotReceiveWithAnyArgs()
            .StoreWarningEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }
}
