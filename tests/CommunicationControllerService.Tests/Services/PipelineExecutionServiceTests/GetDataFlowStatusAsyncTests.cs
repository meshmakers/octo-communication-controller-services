using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class GetDataFlowStatusAsyncTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task GetDataFlowStatusAsync_AllPipelinesIdle_ReturnsIdle()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline1, pipeline2 });

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, Arg.Any<RtEntityId>(), null, null, 1)
            .Returns(new List<RtPipelineExecution>());

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Idle);
        await Assert.That(result.Pipelines).Count().IsEqualTo(2);
        await Assert.That(result.Pipelines[0].State).IsEqualTo(PipelineExecutionState.Idle);
        await Assert.That(result.Pipelines[1].State).IsEqualTo(PipelineExecutionState.Idle);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_OnePipelineRunning_ReturnsRunning()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline1, pipeline2 });

        var runningExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Running);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline1.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { runningExecution });

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline2.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution>());

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Running);
        await Assert.That(result.Pipelines[0].State).IsEqualTo(PipelineExecutionState.Running);
        await Assert.That(result.Pipelines[1].State).IsEqualTo(PipelineExecutionState.Idle);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_OnePipelineFailedNoneRunning_ReturnsFailed()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline1, pipeline2 });

        var failedExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Failed);
        var completedExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Completed);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline1.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { failedExecution });

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline2.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { completedExecution });

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Failed);
        await Assert.That(result.Pipelines[0].State).IsEqualTo(PipelineExecutionState.Failed);
        await Assert.That(result.Pipelines[1].State).IsEqualTo(PipelineExecutionState.Completed);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_AllCompleted_ReturnsCompleted()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline1, pipeline2 });

        var completedExecution1 = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Completed);
        var completedExecution2 = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Completed);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline1.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { completedExecution1 });

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline2.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { completedExecution2 });

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Completed);
        await Assert.That(result.Pipelines[0].State).IsEqualTo(PipelineExecutionState.Completed);
        await Assert.That(result.Pipelines[1].State).IsEqualTo(PipelineExecutionState.Completed);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_RunningTakesPrecedenceOverFailed_ReturnsRunning()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline1 = RtEntityCreator.CreatePipeline();
        var pipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline1, pipeline2 });

        var runningExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Running);
        var failedExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Failed);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline1.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { runningExecution });

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline2.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { failedExecution });

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Running);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_NoPipelines_ReturnsIdle()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline>());

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Idle);
        await Assert.That(result.Pipelines).Count().IsEqualTo(0);
    }

    [Test]
    public async Task GetDataFlowStatusAsync_WithStatistics_IncludesStatisticsSummary()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline });

        var completedExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Completed);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { completedExecution });

        var statistics = RtEntityCreator.CreatePipelineStatistics();
        statistics.LastHourSuccessCount = 10;
        statistics.LastHourFailureCount = 2;
        statistics.LastHourAvgDurationMs = 500;
        statistics.LastExecutionAt = DateTime.UtcNow.AddMinutes(-5);

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, pipeline.ToRtEntityId())
            .Returns(statistics);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.Pipelines[0].Statistics).IsNotNull();
        await Assert.That(result.Pipelines[0].Statistics!.LastHourSuccessCount).IsEqualTo(10);
        await Assert.That(result.Pipelines[0].Statistics!.LastHourFailureCount).IsEqualTo(2);
        await Assert.That(result.Pipelines[0].Statistics!.LastHourAvgDurationMs).IsEqualTo(500);
        await Assert.That(result.Pipelines[0].LastExecutionAt).IsNotNull();
    }

    [Test]
    public async Task GetDataFlowStatusAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(Task.FromException<IReadOnlyCollection<RtPipeline>>(new InvalidOperationException("Database error")));

        // Act & Assert
        await Assert.That(async () =>
                await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to get data flow status");
    }

    [Test]
    public async Task GetDataFlowStatusAsync_InterruptedExecution_MapsToFailed()
    {
        // Arrange
        var dataFlow = RtEntityCreator.CreateDataFlow();
        var pipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelinesAsync(TenantId, dataFlow.RtId)
            .Returns(new List<RtPipeline> { pipeline });

        var interruptedExecution = RtEntityCreator.CreatePipelineExecution(status: RtPipelineExecutionStatusEnum.Interrupted);

        CommunicationRepository.GetPipelineExecutionsAsync(
                TenantId, pipeline.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { interruptedExecution });

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns((RtPipelineStatistics?)null);

        // Act
        var result = await PipelineExecutionService.GetDataFlowStatusAsync(TenantId, dataFlow.RtId);

        // Assert
        await Assert.That(result.State).IsEqualTo(DataFlowExecutionState.Failed);
        await Assert.That(result.Pipelines[0].State).IsEqualTo(PipelineExecutionState.Failed);
    }

    [Test]
    public async Task DeterminePipelineState_NoExecutions_ReturnsIdle()
    {
        var result = PipelineExecutionService.DeterminePipelineState(new List<RtPipelineExecution>());
        await Assert.That(result).IsEqualTo(PipelineExecutionState.Idle);
    }

    [Test]
    public async Task AggregateDataFlowState_EmptyList_ReturnsIdle()
    {
        var result = PipelineExecutionService.AggregateDataFlowState(new List<PipelineStatusDto>());
        await Assert.That(result).IsEqualTo(DataFlowExecutionState.Idle);
    }
}
