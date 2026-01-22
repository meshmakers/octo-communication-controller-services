using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class StatisticsTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task UpdateStatisticsAsync_ValidPipeline_UpdatesAllPeriods()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var execution = RtEntityCreator.CreatePipelineExecution();

        var lastHourAggregate = new ExecutionAggregateResult(5, 1, 6000, 6);
        var last12HoursAggregate = new ExecutionAggregateResult(50, 10, 60000, 60);
        var last24HoursAggregate = new ExecutionAggregateResult(100, 20, 120000, 120);
        var last30DaysAggregate = new ExecutionAggregateResult(1000, 200, 1200000, 1200);

        CommunicationRepository.GetExecutionAggregateAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(
                lastHourAggregate,
                last12HoursAggregate,
                last24HoursAggregate,
                last30DaysAggregate);

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution> { execution });

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        await CommunicationRepository.Received(1).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s =>
                s.LastHourSuccessCount == 5 &&
                s.LastHourFailureCount == 1 &&
                s.Last12HoursSuccessCount == 50 &&
                s.Last12HoursFailureCount == 10 &&
                s.Last24HoursSuccessCount == 100 &&
                s.Last24HoursFailureCount == 20 &&
                s.Last30DaysSuccessCount == 1000 &&
                s.Last30DaysFailureCount == 200),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_NoRecentExecutions_SetsNullLastExecutionAt()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        var emptyAggregate = new ExecutionAggregateResult(0, 0, 0, 0);

        CommunicationRepository.GetExecutionAggregateAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(emptyAggregate);

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), null, null, 1)
            .Returns(new List<RtPipelineExecution>());

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert
        await CommunicationRepository.Received(1).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s => s.LastExecutionAt == null),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetExecutionAggregateAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(Task.FromException<ExecutionAggregateResult>(new InvalidOperationException("Database error")));

        // Act & Assert
        await Assert.That(async () =>
                await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId()))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to update statistics");
    }

    [Test]
    public async Task UpdateAllStatisticsAsync_MultiplePipelines_UpdatesAll()
    {
        // Arrange
        var rtPipeline1 = RtEntityCreator.CreatePipeline();
        var rtPipeline2 = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetAllPipelinesAsync(TenantId)
            .Returns(new List<RtPipeline> { rtPipeline1, rtPipeline2 });

        var emptyAggregate = new ExecutionAggregateResult(0, 0, 0, 0);

        CommunicationRepository.GetExecutionAggregateAsync(
            TenantId, Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>(), Arg.Any<DateTime>(), Arg.Any<DateTime>())
            .Returns(emptyAggregate);

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>(), null, null, 1)
            .Returns(new List<RtPipelineExecution>());

        // Act
        await PipelineExecutionService.UpdateAllStatisticsAsync(TenantId);

        // Assert
        await CommunicationRepository.Received(2).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Any<RtPipelineStatistics>(),
            Arg.Any<Meshmakers.Octo.ConstructionKit.Contracts.RtEntityId>());
    }

    [Test]
    public async Task CleanupOldExecutionsAsync_DeletesOldExecutions()
    {
        // Arrange
        var retentionDays = 30;
        var deletedCount = 50;

        CommunicationRepository.DeleteOldExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(deletedCount);

        // Act
        var result = await PipelineExecutionService.CleanupOldExecutionsAsync(TenantId, retentionDays);

        // Assert
        await Assert.That(result).IsEqualTo(deletedCount);

        await CommunicationRepository.Received(1).DeleteOldExecutionsAsync(TenantId, Arg.Any<DateTime>());
    }

    [Test]
    public async Task CleanupOldExecutionsAsync_NoExecutionsToDelete_NoEvent()
    {
        // Arrange
        var retentionDays = 30;

        CommunicationRepository.DeleteOldExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(0);

        // Act
        var result = await PipelineExecutionService.CleanupOldExecutionsAsync(TenantId, retentionDays);

        // Assert
        await Assert.That(result).IsEqualTo(0);

        await CommunicationEventService.DidNotReceive().StoreInformationEventAsync(
            Arg.Any<string>(),
            Arg.Any<string>());
    }

    [Test]
    public async Task CleanupOldExecutionsAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        CommunicationRepository.DeleteOldExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(Task.FromException<int>(new InvalidOperationException("Database error")));

        // Act & Assert
        await Assert.That(async () =>
                await PipelineExecutionService.CleanupOldExecutionsAsync(TenantId, 30))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to cleanup old executions");
    }
}
