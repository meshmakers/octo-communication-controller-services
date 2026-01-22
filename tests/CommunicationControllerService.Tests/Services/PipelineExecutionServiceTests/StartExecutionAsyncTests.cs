using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class StartExecutionAsyncTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task StartExecutionAsync_UnknownTenant_ThrowsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var startDto = new PipelineExecutionStartDto
        {
            ExecutionId = Guid.NewGuid().ToString(),
            PipelineRtEntityId = rtPipeline.ToRtEntityId(),
            TriggerType = PipelineTriggerType.Manual,
            StartedAt = DateTime.UtcNow
        };

        await Assert.That(async () =>
                await PipelineExecutionService.StartExecutionAsync("unknown", rtAdapter.ToRtEntityId(), startDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Tenant not enabled");
    }

    [Test]
    public async Task StartExecutionAsync_ValidInput_CreatesExecutionRecord()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var executionId = Guid.NewGuid().ToString();
        var startDto = new PipelineExecutionStartDto
        {
            ExecutionId = executionId,
            PipelineRtEntityId = rtPipeline.ToRtEntityId(),
            TriggerType = PipelineTriggerType.Manual,
            StartedAt = DateTime.UtcNow
        };

        // Act
        await PipelineExecutionService.StartExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), startDto);

        // Assert
        await CommunicationRepository.Received(1).CreatePipelineExecutionAsync(
            TenantId,
            Arg.Is<RtPipelineExecution>(e =>
                e.ExecutionId == executionId &&
                e.Status == RtPipelineExecutionStatusEnum.Running &&
                e.TriggerType == RtPipelineTriggerTypeEnum.Manual),
            rtPipeline.ToRtEntityId(),
            rtAdapter.ToRtEntityId());

        await CommunicationRepository.Received(1).SetPipelineCurrentExecutionAsync(
            TenantId, rtPipeline.ToRtEntityId(), executionId);
    }

    [Test]
    public async Task StartExecutionAsync_WithInputData_StoresInputData()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var inputData = """{"key": "value"}""";
        var startDto = new PipelineExecutionStartDto
        {
            ExecutionId = Guid.NewGuid().ToString(),
            PipelineRtEntityId = rtPipeline.ToRtEntityId(),
            TriggerType = PipelineTriggerType.Event,
            StartedAt = DateTime.UtcNow,
            InputData = inputData
        };

        // Act
        await PipelineExecutionService.StartExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), startDto);

        // Assert
        await CommunicationRepository.Received(1).CreatePipelineExecutionAsync(
            TenantId,
            Arg.Is<RtPipelineExecution>(e => e.InputData == inputData),
            rtPipeline.ToRtEntityId(),
            rtAdapter.ToRtEntityId());
    }

    [Test]
    public async Task StartExecutionAsync_ScheduledTrigger_RecordsTriggerType()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var startDto = new PipelineExecutionStartDto
        {
            ExecutionId = Guid.NewGuid().ToString(),
            PipelineRtEntityId = rtPipeline.ToRtEntityId(),
            TriggerType = PipelineTriggerType.Scheduled,
            StartedAt = DateTime.UtcNow
        };

        // Act
        await PipelineExecutionService.StartExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), startDto);

        // Assert
        await CommunicationRepository.Received(1).CreatePipelineExecutionAsync(
            TenantId,
            Arg.Is<RtPipelineExecution>(e => e.TriggerType == RtPipelineTriggerTypeEnum.Scheduled),
            rtPipeline.ToRtEntityId(),
            rtAdapter.ToRtEntityId());
    }

    [Test]
    public async Task StartExecutionAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        var rtAdapter = RtEntityCreator.CreateAdapter();
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var startDto = new PipelineExecutionStartDto
        {
            ExecutionId = Guid.NewGuid().ToString(),
            PipelineRtEntityId = rtPipeline.ToRtEntityId(),
            TriggerType = PipelineTriggerType.Manual,
            StartedAt = DateTime.UtcNow
        };

        CommunicationRepository.CreatePipelineExecutionAsync(
            TenantId,
            Arg.Any<RtPipelineExecution>(),
            rtPipeline.ToRtEntityId(),
            rtAdapter.ToRtEntityId())
            .Returns(Task.FromException(new InvalidOperationException("Database error")));

        // Act & Assert
        await Assert.That(async () =>
                await PipelineExecutionService.StartExecutionAsync(TenantId, rtAdapter.ToRtEntityId(), startDto))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to start execution");
    }
}
