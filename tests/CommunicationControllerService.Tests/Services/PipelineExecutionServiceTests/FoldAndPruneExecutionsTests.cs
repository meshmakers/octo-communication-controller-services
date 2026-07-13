using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
/// Pins the fold-then-prune contract (AB#4370): terminal executions older than the retention
/// window are folded into the hourly buckets on the statistics entity, the buckets are
/// persisted BEFORE the executions are erased, and the sliding windows are refreshed for
/// every pipeline even when nothing was folded.
/// </summary>
[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class FoldAndPruneExecutionsTests : PipelineExecutionServiceTestsBase
{
    [Test]
    public async Task FoldAndPruneExecutionsAsync_TerminalBatch_FoldsIntoBucketsThenDeletes()
    {
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var pipelineRtEntityId = rtPipeline.ToRtEntityId();
        var startedAt = DateTime.UtcNow.AddHours(-3);

        var executions = new List<RtPipelineExecution>
        {
            new()
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                ExecutionId = Guid.NewGuid().ToString(),
                Status = RtPipelineExecutionStatusEnum.Completed,
                StartedAt = startedAt,
                DurationMs = 100
            },
            new()
            {
                RtId = OctoObjectId.GenerateNewId(),
                CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                ExecutionId = Guid.NewGuid().ToString(),
                Status = RtPipelineExecutionStatusEnum.Failed,
                StartedAt = startedAt,
                DurationMs = 200
            }
        };

        CommunicationRepository.GetAllPipelinesAsync(TenantId).Returns([rtPipeline]);
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(executions, new List<RtPipelineExecution>());
        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, pipelineRtEntityId)
            .Returns((RtPipelineStatistics?)null);
        CommunicationRepository.GetPipelineExecutionsAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());
        CommunicationRepository.DeleteExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<RtEntityId>>())
            .Returns(x => ((IReadOnlyList<RtEntityId>)x[1]).Count);

        var pruned = await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        await Assert.That(pruned).IsEqualTo(2);

        // Buckets carry the folded counters
        await CommunicationRepository.Received().UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s =>
                s.HourlyBuckets != null &&
                s.HourlyBuckets.Sum(b => b.SuccessCount) == 1 &&
                s.HourlyBuckets.Sum(b => b.FailureCount) == 1 &&
                s.HourlyBuckets.Sum(b => b.TotalDurationMs) == 300 &&
                s.LastExecutionAt == startedAt),
            pipelineRtEntityId);

        // Exactly the folded executions are erased
        await CommunicationRepository.Received(1).DeleteExecutionsAsync(
            TenantId,
            Arg.Is<IReadOnlyList<RtEntityId>>(ids =>
                ids.Count == 2 &&
                ids.Any(id => id.RtId == executions[0].RtId) &&
                ids.Any(id => id.RtId == executions[1].RtId)));

        // Buckets were persisted before the delete (fold-then-delete crash contract)
        Received.InOrder(() =>
        {
            CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Is<RtPipelineStatistics>(s => s.HourlyBuckets != null), pipelineRtEntityId);
            CommunicationRepository.DeleteExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<RtEntityId>>());
        });
    }

    [Test]
    public async Task FoldAndPruneExecutionsAsync_NothingToFold_StillRefreshesWindows()
    {
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var pipelineRtEntityId = rtPipeline.ToRtEntityId();

        CommunicationRepository.GetAllPipelinesAsync(TenantId).Returns([rtPipeline]);
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());
        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, pipelineRtEntityId)
            .Returns(new RtPipelineStatistics { Last24HoursSuccessCount = 5 });
        CommunicationRepository.GetPipelineExecutionsAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());

        var pruned = await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        await Assert.That(pruned).IsEqualTo(0);
        await CommunicationRepository.DidNotReceive()
            .DeleteExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<RtEntityId>>());
        // Windows decay to zero once nothing is retained and no buckets exist
        await CommunicationRepository.Received(1).UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s => s.Last24HoursSuccessCount == 0),
            pipelineRtEntityId);
    }

    [Test]
    public async Task FoldAndPruneExecutionsAsync_MergesIntoExistingBuckets()
    {
        var rtPipeline = RtEntityCreator.CreatePipeline();
        var pipelineRtEntityId = rtPipeline.ToRtEntityId();
        var startedAt = DateTime.UtcNow.AddHours(-2);
        var hour = PipelineStatisticsFolder.FloorToHour(startedAt);

        var existing = new RtPipelineStatistics
        {
            HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(
                new List<RtRecord>
                {
                    new RtPipelineStatisticsHourBucketRecord
                    {
                        HourStartAt = hour, SuccessCount = 3, FailureCount = 0, TotalDurationMs = 30, DurationCount = 3
                    }
                })
        };

        CommunicationRepository.GetAllPipelinesAsync(TenantId).Returns([rtPipeline]);
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(
            [
                new RtPipelineExecution
                {
                    RtId = OctoObjectId.GenerateNewId(),
                    CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
                    ExecutionId = Guid.NewGuid().ToString(),
                    Status = RtPipelineExecutionStatusEnum.Completed,
                    StartedAt = startedAt,
                    DurationMs = 70
                }
            ], new List<RtPipelineExecution>());
        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, pipelineRtEntityId).Returns(existing);
        CommunicationRepository.GetPipelineExecutionsAsync(TenantId, pipelineRtEntityId,
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());
        CommunicationRepository.DeleteExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<RtEntityId>>())
            .Returns(1);

        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        await CommunicationRepository.Received().UpsertPipelineStatisticsAsync(
            TenantId,
            Arg.Is<RtPipelineStatistics>(s =>
                s.HourlyBuckets != null &&
                s.HourlyBuckets.Count(b => b.HourStartAt == hour) == 1 &&
                s.HourlyBuckets.Single(b => b.HourStartAt == hour).SuccessCount == 4 &&
                s.HourlyBuckets.Single(b => b.HourStartAt == hour).TotalDurationMs == 100),
            pipelineRtEntityId);
    }
}
