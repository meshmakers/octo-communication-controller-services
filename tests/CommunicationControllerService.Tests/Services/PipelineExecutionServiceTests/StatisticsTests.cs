using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
        var now = DateTime.UtcNow;

        // Create executions spanning different time windows
        var executionsLast30Days = new List<RtPipelineExecution>();

        // 5 successful + 1 failed in last hour (also counted in 12h, 24h, 30d)
        for (var i = 0; i < 5; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Completed, now.AddMinutes(-30), 1000));
        }
        executionsLast30Days.Add(CreateExecutionWithTiming(
            RtPipelineExecutionStatusEnum.Failed, now.AddMinutes(-45), 1000));

        // Additional 44 successful + 9 failed in 12h window (not in 1h)
        for (var i = 0; i < 44; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Completed, now.AddHours(-6), 1000));
        }
        for (var i = 0; i < 9; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Failed, now.AddHours(-6), 1000));
        }

        // Additional 50 successful + 10 failed in 24h window (not in 12h)
        for (var i = 0; i < 50; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Completed, now.AddHours(-18), 1000));
        }
        for (var i = 0; i < 10; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Failed, now.AddHours(-18), 1000));
        }

        // Additional 900 successful + 180 failed in 30d window (not in 24h)
        for (var i = 0; i < 900; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Completed, now.AddDays(-15), 1000));
        }
        for (var i = 0; i < 180; i++)
        {
            executionsLast30Days.Add(CreateExecutionWithTiming(
                RtPipelineExecutionStatusEnum.Failed, now.AddDays(-15), 1000));
        }

        // Sort descending by StartedAt (as the repository would return)
        executionsLast30Days.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));

        // Mock the paginated overload used by batch-based statistics computation
        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(callInfo =>
            {
                var skip = callInfo.ArgAt<int>(4);
                return skip == 0
                    ? (IReadOnlyList<RtPipelineExecution>)executionsLast30Days
                    : new List<RtPipelineExecution>();
            });

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert - 1h: 5+1, 12h: 49+10, 24h: 99+20, 30d: 999+200
        await CommunicationRepository.Received(1).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s =>
                s.LastHourSuccessCount == 5 &&
                s.LastHourFailureCount == 1 &&
                s.Last12HoursSuccessCount == 49 &&
                s.Last12HoursFailureCount == 10 &&
                s.Last24HoursSuccessCount == 99 &&
                s.Last24HoursFailureCount == 20 &&
                s.Last30DaysSuccessCount == 999 &&
                s.Last30DaysFailureCount == 200),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_NoExecutions_NoExistingStatistics_SkipsUpsert()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns((RtPipelineStatistics?)null);

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert - upsert should NOT be called since no executions and no existing stats
        await CommunicationRepository.DidNotReceive().UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Any<RtPipelineStatistics>(),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_NoExecutions_ExistingEmptyStatistics_SkipsUpsert()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(new RtPipelineStatistics
            {
                LastExecutionAt = null,
                LastHourSuccessCount = 0,
                LastHourFailureCount = 0,
                Last12HoursSuccessCount = 0,
                Last12HoursFailureCount = 0,
                Last24HoursSuccessCount = 0,
                Last24HoursFailureCount = 0,
                Last30DaysSuccessCount = 0,
                Last30DaysFailureCount = 0
            });

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert - upsert should NOT be called since stats are already empty
        await CommunicationRepository.DidNotReceive().UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Any<RtPipelineStatistics>(),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_NoExecutions_ExistingNonEmptyStatistics_ResetsToZero()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());

        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, rtPipeline.ToRtEntityId())
            .Returns(new RtPipelineStatistics
            {
                LastExecutionAt = DateTime.UtcNow.AddDays(-5),
                LastHourSuccessCount = 0,
                LastHourFailureCount = 0,
                Last12HoursSuccessCount = 0,
                Last12HoursFailureCount = 0,
                Last24HoursSuccessCount = 0,
                Last24HoursFailureCount = 0,
                Last30DaysSuccessCount = 10,
                Last30DaysFailureCount = 2
            });

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert - upsert SHOULD be called to reset stats to zero
        await CommunicationRepository.Received(1).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s =>
                s.LastExecutionAt == null &&
                s.LastHourSuccessCount == 0 &&
                s.LastHourFailureCount == 0 &&
                s.Last12HoursSuccessCount == 0 &&
                s.Last12HoursFailureCount == 0 &&
                s.Last24HoursSuccessCount == 0 &&
                s.Last24HoursFailureCount == 0 &&
                s.Last30DaysSuccessCount == 0 &&
                s.Last30DaysFailureCount == 0),
            rtPipeline.ToRtEntityId());
    }

    [Test]
    public async Task UpdateStatisticsAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(Task.FromException<IReadOnlyList<RtPipelineExecution>>(new InvalidOperationException("Database error")));

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
        var now = DateTime.UtcNow;

        CommunicationRepository.GetAllPipelinesAsync(TenantId)
            .Returns(new List<RtPipeline> { rtPipeline1, rtPipeline2 });

        // Provide executions so the optimization doesn't skip the upsert
        var executions = new List<RtPipelineExecution>
        {
            CreateExecutionWithTiming(RtPipelineExecutionStatusEnum.Completed, now.AddMinutes(-30), 1000)
        };

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, Arg.Any<RtEntityId>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(callInfo =>
            {
                var skip = callInfo.ArgAt<int>(4);
                return skip == 0
                    ? (IReadOnlyList<RtPipelineExecution>)executions
                    : new List<RtPipelineExecution>();
            });

        // Act
        await PipelineExecutionService.UpdateAllStatisticsAsync(TenantId);

        // Assert
        await CommunicationRepository.Received(2).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Any<RtPipelineStatistics>(),
            Arg.Any<RtEntityId>());
    }

    [Test]
    public async Task UpdateStatisticsAsync_BatchedQuery_UsesSkipTakePagination()
    {
        // Arrange
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var now = DateTime.UtcNow;

        // Provide executions so the optimization doesn't skip the upsert
        var executions = new List<RtPipelineExecution>
        {
            CreateExecutionWithTiming(RtPipelineExecutionStatusEnum.Completed, now.AddMinutes(-30), 1000)
        };

        CommunicationRepository.GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>())
            .Returns(callInfo =>
            {
                var skip = callInfo.ArgAt<int>(4);
                return skip == 0
                    ? (IReadOnlyList<RtPipelineExecution>)executions
                    : new List<RtPipelineExecution>();
            });

        // Act
        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, rtPipeline.ToRtEntityId());

        // Assert - should use the paginated overload with skip/take (not the old overload with limit)
        await CommunicationRepository.Received().GetPipelineExecutionsAsync(
            TenantId, rtPipeline.ToRtEntityId(), Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<int>(), Arg.Any<int>());

        // Should NOT call GetExecutionAggregateAsync at all
        await CommunicationRepository.DidNotReceive().GetExecutionAggregateAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>(), Arg.Any<DateTime>());
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

    [Test]
    public async Task TimeoutStaleExecutionsAsync_TimesOutStaleExecutions()
    {
        // Arrange
        var timeoutHours = 24;
        var timedOutCount = 5;

        CommunicationRepository.TimeoutStaleExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(timedOutCount);

        // Act
        var result = await PipelineExecutionService.TimeoutStaleExecutionsAsync(TenantId, timeoutHours);

        // Assert
        await Assert.That(result).IsEqualTo(timedOutCount);

        await CommunicationRepository.Received(1).TimeoutStaleExecutionsAsync(TenantId, Arg.Any<DateTime>());
        await CommunicationEventService.Received(1).StoreInformationEventAsync(
            TenantId,
            Arg.Is<string>(s => s.Contains("Timed out 5 stale pipeline executions")));
    }

    [Test]
    public async Task TimeoutStaleExecutionsAsync_NoStaleExecutions_NoEvent()
    {
        // Arrange
        CommunicationRepository.TimeoutStaleExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(0);

        // Act
        var result = await PipelineExecutionService.TimeoutStaleExecutionsAsync(TenantId, 24);

        // Assert
        await Assert.That(result).IsEqualTo(0);

        await CommunicationEventService.DidNotReceive().StoreInformationEventAsync(
            Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("Timed out")));
    }

    [Test]
    public async Task TimeoutStaleExecutionsAsync_RepositoryThrows_WrapsException()
    {
        // Arrange
        CommunicationRepository.TimeoutStaleExecutionsAsync(TenantId, Arg.Any<DateTime>())
            .Returns(Task.FromException<int>(new InvalidOperationException("Database error")));

        // Act & Assert
        await Assert.That(async () =>
                await PipelineExecutionService.TimeoutStaleExecutionsAsync(TenantId, 24))
            .Throws<PipelineExecutionServiceException>()
            .WithMessageContaining("Failed to timeout stale executions");
    }

    private static RtPipelineExecution CreateExecutionWithTiming(
        RtPipelineExecutionStatusEnum status, DateTime startedAt, int durationMs)
    {
        var execution = RtEntityCreator.CreatePipelineExecution(status: status);
        execution.StartedAt = startedAt;
        execution.DurationMs = durationMs;
        execution.CompletedAt = startedAt.AddMilliseconds(durationMs);
        return execution;
    }
}
