using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.AdapterServiceTests;

internal class RegisterAdapterTests : AdapterServiceTestsBase
{
    [Test]
    public async Task RegisterAdapterToUnknownTenant()
    {
        await Assert.That(async () =>
                await AdapterService.RegisterAdapterAsync("unknown", new RtEntityId(""), ""))
            .Throws<AdapterServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task RegisterAdapter_Empty_Cache()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);

        var pipeline = configuration.Pipelines.First();
        await Assert.That(pipeline.DataFlowRtId).IsEqualTo(rtDataFlow.RtId);
        await Assert.That(pipeline.PipelineRtEntityId).IsEqualTo(rtPipeline.ToRtEntityId());
        await Assert.That(pipeline.NodeConfiguration).IsEqualTo(rtPipeline.PipelineDefinition);

        // Note: Online state is set in OnConnectedAsync, not in RegisterAdapterAsync
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateAsync(TenantId,
            rtAdapter.ToRtEntityId(),
            RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task RegisterAdapter_Unchanged_Cache()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto
        (
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);

        var pipeline = configuration.Pipelines.First();
        await Assert.That(pipeline.DataFlowRtId).IsEqualTo(rtDataFlow.RtId);
        await Assert.That(pipeline.PipelineRtEntityId).IsEqualTo(rtPipeline.ToRtEntityId());
        await Assert.That(pipeline.NodeConfiguration).IsEqualTo(rtPipeline.PipelineDefinition);

        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateAsync(TenantId,
            rtAdapter.ToRtEntityId(),
            RtCommunicationStateEnum.Online);
    }
    
    [Test]
    public async Task RegisterAdapter_Reconnect_UnchangedConfig_RefreshesConnectionId()
    {
        // Regression for AB#4594: an adapter that reconnects on a NEW SignalR
        // connection while its configuration is unchanged must have its cached
        // ConnectionId refreshed to the live connection. Otherwise
        // AdapterConfigurationUpdatedAsync keeps routing config deploys to the dead
        // old connection (Clients.Client(adapter.ConnectionId)) and every deploy
        // silently times out after 120s while the adapter stays Online.

        // Arrange — adapter already cached on an OLD connection, config unchanged
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        const string oldConnectionId = "old-connection";
        const string newConnectionId = "new-connection";

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), oldConnectionId, new AdapterConfigurationDto
        (
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));

        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);

        // Act — re-register on the NEW connection with the same configuration
        await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), newConnectionId);

        // Assert — the cache now points at the live connection
        using var _ = Assert.Multiple();

        await Assert.That(AdapterTenant.AdapterById[rtAdapter.ToRtEntityId()].ConnectionId)
            .IsEqualTo(newConnectionId);
        await Assert.That(AdapterTenant.AdapterByConnectionId.ContainsKey(newConnectionId)).IsTrue();
        await Assert.That(AdapterTenant.AdapterByConnectionId.ContainsKey(oldConnectionId)).IsFalse();
    }

    [Test]
    public async Task RegisterAdapter_Changed_Pipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto
        (
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));
        
        rtPipeline.PipelineDefinition = "changedDefinition";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline]);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(1);

        var pipeline = configuration.Pipelines.First();
        await Assert.That(pipeline.DataFlowRtId).IsEqualTo(rtDataFlow.RtId);
        await Assert.That(pipeline.PipelineRtEntityId).IsEqualTo(rtPipeline.ToRtEntityId());
        await Assert.That(pipeline.NodeConfiguration).IsEqualTo(rtPipeline.PipelineDefinition);

        // Note: Online state is set in OnConnectedAsync, not in RegisterAdapterAsync
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateAsync(TenantId,
            rtAdapter.ToRtEntityId(),
            RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task RegisterAdapter_Removed_All_Pipelines()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto
        (
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));
        
        rtPipeline.PipelineDefinition = "changedDefinition";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, []);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(0);

        // Note: Online state is set in OnConnectedAsync, not in RegisterAdapterAsync
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateAsync(TenantId,
            rtAdapter.ToRtEntityId(),
            RtCommunicationStateEnum.Online);
    }

    [Test]
    public async Task RegisterAdapter_Add_Pipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtDataFlow = RtEntityCreator.CreateDataFlow();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var rtPipelineNew = RtEntityCreator.CreatePipeline();

        AdapterTenant.AddAdapter(rtAdapter.ToRtEntityId(), ConnectionId, new AdapterConfigurationDto
        (
            rtAdapter.ToRtEntityId(),
            null,
            [
                new PipelineConfigurationDto(rtDataFlow.RtId, rtPipeline.ToRtEntityId(), false,
                    rtPipeline.PipelineDefinition, [])
            ]
        ));
        
        rtPipeline.PipelineDefinition = "changedDefinition";
        InitAdapterConfiguration(rtAdapter, rtDataFlow, [rtPipeline, rtPipelineNew]);

        // Act
        var configuration =
            await AdapterService.RegisterAdapterAsync(TenantId, rtAdapter.ToRtEntityId(), ConnectionId);

        // Assert
        using var _ = Assert.Multiple();

        await Assert.That(configuration).IsNotNull();
        await Assert.That(configuration.Pipelines).Count().IsEqualTo(2);

        var pipeline = configuration.Pipelines.FirstOrDefault(e => e.PipelineRtEntityId == rtPipeline.ToRtEntityId());
        await Assert.That(pipeline).IsNotNull()
            .And.Member(e => e.NodeConfiguration, config => config.IsEqualTo(rtPipeline.PipelineDefinition));

        pipeline = configuration.Pipelines.FirstOrDefault(e => e.PipelineRtEntityId == rtPipelineNew.ToRtEntityId());
        await Assert.That(pipeline).IsNotNull()
            .And.Member(e => e.NodeConfiguration, config => config.IsEqualTo(rtPipelineNew.PipelineDefinition));

        // Note: Online state is set in OnConnectedAsync, not in RegisterAdapterAsync
        await CommunicationRepository.DidNotReceiveWithAnyArgs().SetAdapterCommunicationStateAsync(TenantId,
            rtAdapter.ToRtEntityId(),
            RtCommunicationStateEnum.Online);
    }
}
