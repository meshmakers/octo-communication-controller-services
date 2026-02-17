using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class BatchStartExecutionAsyncTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task BatchStartExecutionsAsync_EmptyList_DoesNothing()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();

        // Act
        await PipelineExecutionService.BatchStartExecutionsAsync(TenantId, rtAdapter.ToRtEntityId(), []);

        // Assert
        await CommunicationRepository.DidNotReceive().BulkInsertPipelineExecutionsAsync(
            Arg.Any<string>(),
            Arg.Any<IEnumerable<RtPipelineExecution>>(),
            Arg.Any<ConstructionKit.Contracts.RtEntityId>(),
            Arg.Any<ConstructionKit.Contracts.RtEntityId>());
    }

    [Test]
    public async Task BatchStartExecutionsAsync_UnknownTenant_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var dtos = new List<PipelineExecutionStartDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                PipelineRtEntityId = rtPipeline.ToRtEntityId(),
                TriggerType = PipelineTriggerType.Scheduled,
                StartedAt = DateTime.UtcNow
            }
        };

        await Assert.That(async () =>
                await PipelineExecutionService.BatchStartExecutionsAsync("unknown", rtAdapter.ToRtEntityId(), dtos))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task BatchStartExecutionsAsync_MultipleDtos_CallsBulkInsert()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var dtos = new List<PipelineExecutionStartDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                PipelineRtEntityId = rtPipeline.ToRtEntityId(),
                TriggerType = PipelineTriggerType.Scheduled,
                StartedAt = DateTime.UtcNow
            },
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                PipelineRtEntityId = rtPipeline.ToRtEntityId(),
                TriggerType = PipelineTriggerType.Event,
                StartedAt = DateTime.UtcNow
            }
        };

        // Act
        await PipelineExecutionService.BatchStartExecutionsAsync(TenantId, rtAdapter.ToRtEntityId(), dtos);

        // Assert - should call BulkInsert once (both DTOs are for the same pipeline)
        await CommunicationRepository.Received(1).BulkInsertPipelineExecutionsAsync(
            TenantId,
            Arg.Is<IEnumerable<RtPipelineExecution>>(list => list.Count() == 2),
            rtPipeline.ToRtEntityId(),
            rtAdapter.ToRtEntityId());
    }

    [Test]
    public async Task BatchStartExecutionsAsync_DifferentPipelines_GroupsByPipeline()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();
        var dtos = new List<PipelineExecutionStartDto>
        {
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                PipelineRtEntityId = rtPipeline1.ToRtEntityId(),
                TriggerType = PipelineTriggerType.Scheduled,
                StartedAt = DateTime.UtcNow
            },
            new()
            {
                ExecutionId = Guid.NewGuid().ToString(),
                PipelineRtEntityId = rtPipeline2.ToRtEntityId(),
                TriggerType = PipelineTriggerType.Event,
                StartedAt = DateTime.UtcNow
            }
        };

        // Act
        await PipelineExecutionService.BatchStartExecutionsAsync(TenantId, rtAdapter.ToRtEntityId(), dtos);

        // Assert - should call BulkInsert twice (one per pipeline)
        await CommunicationRepository.Received(2).BulkInsertPipelineExecutionsAsync(
            TenantId,
            Arg.Any<IEnumerable<RtPipelineExecution>>(),
            Arg.Any<ConstructionKit.Contracts.RtEntityId>(),
            rtAdapter.ToRtEntityId());
    }
}
