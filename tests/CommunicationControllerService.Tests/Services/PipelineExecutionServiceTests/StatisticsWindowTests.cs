using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
/// AB#5583: the 12h/24h/30d counters and the persisted hourly buckets share one window (N UTC
/// clock hours ending with the current partial hour), one source (the bucket list: folded history
/// plus a snapshot of the retained executions) and one status classification. Everything here
/// runs against a fixed clock.
/// </summary>
[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
internal class StatisticsWindowTests : PipelineExecutionServiceTestsBase
{
    // 10:30 — the current clock hour 10:00 is partial.
    private static readonly DateTime Now = new(2026, 10, 6, 10, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime CurrentHour = new(2026, 10, 6, 10, 0, 0, DateTimeKind.Utc);

    private readonly RtPipeline _pipeline = RtEntityCreator.CreatePipeline();
    private RtEntityId PipelineId => _pipeline.ToRtEntityId();

    private RtPipelineStatistics? _upserted;

    public StatisticsWindowTests()
    {
        Clock.UtcNow = Now;
        CommunicationRepository.GetAllPipelinesAsync(TenantId).Returns([_pipeline]);
        CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Do<RtPipelineStatistics>(s => _upserted = s), PipelineId)
            .Returns(Task.CompletedTask);
        CommunicationRepository.DeleteExecutionsAsync(TenantId, Arg.Any<IReadOnlyList<RtEntityId>>())
            .Returns(x => ((IReadOnlyList<RtEntityId>)x[1]).Count);
    }

    // ---------------------------------------------------------------- pure rules

    [Test]
    public async Task Classify_OnlyCompletedAndFailedCount()
    {
        await Assert.That(PipelineStatisticsFolder.Classify(RtPipelineExecutionStatusEnum.Completed))
            .IsEqualTo(PipelineStatisticsFolder.ExecutionOutcome.Success);
        await Assert.That(PipelineStatisticsFolder.Classify(RtPipelineExecutionStatusEnum.Failed))
            .IsEqualTo(PipelineStatisticsFolder.ExecutionOutcome.Failure);
        await Assert.That(PipelineStatisticsFolder.Classify(RtPipelineExecutionStatusEnum.Running))
            .IsEqualTo(PipelineStatisticsFolder.ExecutionOutcome.Neither);
        await Assert.That(PipelineStatisticsFolder.Classify(RtPipelineExecutionStatusEnum.Interrupted))
            .IsEqualTo(PipelineStatisticsFolder.ExecutionOutcome.Neither);
        await Assert.That(PipelineStatisticsFolder.Classify(RtPipelineExecutionStatusEnum.Cancelled))
            .IsEqualTo(PipelineStatisticsFolder.ExecutionOutcome.Neither);
    }

    [Test]
    public async Task WindowStart_IsNClockHoursEndingWithTheCurrentPartialHour()
    {
        await Assert.That(PipelineStatisticsFolder.WindowStart(Now, 24)).IsEqualTo(CurrentHour.AddHours(-23));
        await Assert.That(PipelineStatisticsFolder.WindowStart(Now, 12)).IsEqualTo(CurrentHour.AddHours(-11));
        await Assert.That(PipelineStatisticsFolder.WindowStart(Now, 1)).IsEqualTo(CurrentHour);
        // Exactly on the hour the new hour is already the current (empty) slot.
        await Assert.That(PipelineStatisticsFolder.WindowStart(CurrentHour, 24)).IsEqualTo(CurrentHour.AddHours(-23));
        await Assert.That(PipelineStatisticsFolder.WindowStart(CurrentHour.AddTicks(-1), 24))
            .IsEqualTo(CurrentHour.AddHours(-24));
    }

    [Test]
    public async Task FoldCutoff_IsHourAligned_KeepsRetention_AndNeverMovesBack()
    {
        var cutoff = PipelineStatisticsFolder.FoldCutoff(Now, 1, null);

        await Assert.That(cutoff).IsEqualTo(CurrentHour.AddHours(-1));
        await Assert.That(cutoff <= Now.AddHours(-1)).IsTrue();
        await Assert.That(PipelineStatisticsFolder.FoldCutoff(Now, 3, null)).IsEqualTo(CurrentHour.AddHours(-3));
        // A larger retention configured later must not un-fold history.
        await Assert.That(PipelineStatisticsFolder.FoldCutoff(Now, 3, CurrentHour.AddHours(-1)))
            .IsEqualTo(CurrentHour.AddHours(-1));
    }

    // ---------------------------------------------------------------- UpdateStatisticsAsync

    [Test]
    public async Task Update_TotalsEqualTheSumOfThePersistedBucketsInTheSameWindow()
    {
        var boundary = CurrentHour.AddHours(-1); // 09:00
        Statistics(boundary,
            Bucket(CurrentHour.AddHours(-24), 100, 100), // 10:00 yesterday — the 25th hour, outside 24h
            Bucket(CurrentHour.AddHours(-23), 1, 2), // 11:00 yesterday — first slot of 24h
            Bucket(CurrentHour.AddHours(-12), 4, 8), // 22:00 yesterday — 24h only
            Bucket(CurrentHour.AddHours(-11), 16, 32), // 23:00 yesterday — first slot of 12h
            Bucket(CurrentHour.AddHours(-1), 50, 50)); // stale 09:00 snapshot — recomputed, not added

        Retained(
            Exec(CurrentHour.AddHours(-2).AddMinutes(30), RtPipelineExecutionStatusEnum.Completed), // 08:30 straggler before the boundary
            Exec(CurrentHour.AddHours(-1).AddMinutes(10), RtPipelineExecutionStatusEnum.Completed), // 09:10
            Exec(CurrentHour.AddHours(-1).AddMinutes(50), RtPipelineExecutionStatusEnum.Failed), // 09:50
            Exec(CurrentHour.AddMinutes(5), RtPipelineExecutionStatusEnum.Completed), // 10:05
            Exec(CurrentHour.AddMinutes(20), RtPipelineExecutionStatusEnum.Cancelled),
            Exec(CurrentHour.AddMinutes(25), RtPipelineExecutionStatusEnum.Interrupted),
            Exec(CurrentHour.AddMinutes(28), RtPipelineExecutionStatusEnum.Running));

        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, PipelineId);

        var s = _upserted!;
        // 24h = 11:00 + 22:00 + 23:00 (folded) + 09:00 + 10:00 (live). The straggler waits for the fold.
        await Assert.That(s.Last24HoursSuccessCount).IsEqualTo(1 + 4 + 16 + 1 + 1);
        await Assert.That(s.Last24HoursFailureCount).IsEqualTo(2 + 8 + 32 + 1);
        await Assert.That(s.Last12HoursSuccessCount).IsEqualTo(16 + 1 + 1);
        await Assert.That(s.Last12HoursFailureCount).IsEqualTo(32 + 1);
        // Rolling 60 minutes (09:30–10:30): 09:50 failed, 10:05 completed.
        await Assert.That(s.LastHourSuccessCount).IsEqualTo(1);
        await Assert.That(s.LastHourFailureCount).IsEqualTo(1);

        // The persisted buckets are the one source: summing the 24 visible slots gives the total.
        await Assert.That(s.FoldedBefore).IsEqualTo(boundary);
        var visible = s.HourlyBuckets!.Where(b => b.HourStartAt >= CurrentHour.AddHours(-23)).ToList();
        await Assert.That(visible.Sum(b => b.SuccessCount)).IsEqualTo(s.Last24HoursSuccessCount);
        await Assert.That(visible.Sum(b => b.FailureCount)).IsEqualTo(s.Last24HoursFailureCount);
        await Assert.That(s.HourlyBuckets!.Single(b => b.HourStartAt == CurrentHour).SuccessCount).IsEqualTo(1);
        await Assert.That(s.HourlyBuckets!.Single(b => b.HourStartAt == boundary).FailureCount).IsEqualTo(1);
    }

    [Test]
    public async Task Update_OnTheHour_DropsTheOldestSlot_AndStartsAnEmptyCurrentSlot()
    {
        Clock.UtcNow = CurrentHour; // exactly 10:00:00
        Statistics(CurrentHour.AddHours(-1),
            Bucket(CurrentHour.AddHours(-24), 7, 7), // left the window this instant
            Bucket(CurrentHour.AddHours(-23), 1, 1));
        Retained();

        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, PipelineId);

        await Assert.That(_upserted!.Last24HoursSuccessCount).IsEqualTo(1);
        await Assert.That(_upserted.Last24HoursFailureCount).IsEqualTo(1);
    }

    [Test]
    public async Task Update_WithoutBoundary_CountsRetainedExecutions_ButPersistsFoldedBucketsOnly()
    {
        // Statistics written before AB#5583: buckets hold folded executions only.
        Statistics(null, Bucket(CurrentHour.AddHours(-3), 2, 0));
        Retained(Exec(CurrentHour.AddMinutes(10), RtPipelineExecutionStatusEnum.Failed));

        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, PipelineId);

        await Assert.That(_upserted!.Last24HoursSuccessCount).IsEqualTo(2);
        await Assert.That(_upserted.Last24HoursFailureCount).IsEqualTo(1);
        await Assert.That(_upserted.FoldedBefore).IsNull();
        await Assert.That(_upserted.HourlyBuckets!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Update_NewStatistics_SetsTheBoundary_AndPersistsTheLiveSnapshotRightAway()
    {
        // No statistics yet: nothing is folded, so no hour can be split — bars and totals must
        // agree from the very first sweep.
        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns((RtPipelineStatistics?)null);
        Retained(
            Exec(CurrentHour.AddHours(-1).AddMinutes(40), RtPipelineExecutionStatusEnum.Failed),
            Exec(CurrentHour.AddMinutes(10), RtPipelineExecutionStatusEnum.Completed));

        await PipelineExecutionService.UpdateStatisticsAsync(TenantId, PipelineId);

        var s = _upserted!;
        await Assert.That(s.FoldedBefore)
            .IsEqualTo(PipelineStatisticsFolder.WindowStart(Now, PipelineStatisticsFolder.RetentionWindowHours));
        await Assert.That(s.HourlyBuckets!.Sum(b => b.SuccessCount)).IsEqualTo(s.Last24HoursSuccessCount);
        await Assert.That(s.HourlyBuckets!.Sum(b => b.FailureCount)).IsEqualTo(s.Last24HoursFailureCount);
        await Assert.That(s.Last24HoursSuccessCount).IsEqualTo(1);
        await Assert.That(s.Last24HoursFailureCount).IsEqualTo(1);
    }

    // ---------------------------------------------------------------- FoldAndPruneExecutionsAsync

    [Test]
    public async Task Fold_ReplacesSnapshotHoursWithFoldedExecutions_WithoutDoubleCounting()
    {
        var previousBoundary = CurrentHour.AddHours(-2); // 08:00
        Statistics(previousBoundary,
            Bucket(CurrentHour.AddHours(-3), 2, 0), // 07:00 folded history
            Bucket(CurrentHour.AddHours(-2), 5, 0), // 08:00 stale snapshot — its executions are folded now
            Bucket(CurrentHour.AddHours(-1), 1, 0)); // 09:00 snapshot — stays live

        var folded = new List<RtPipelineExecution>
        {
            Exec(CurrentHour.AddHours(-2).AddMinutes(1), RtPipelineExecutionStatusEnum.Completed),
            Exec(CurrentHour.AddHours(-2).AddMinutes(2), RtPipelineExecutionStatusEnum.Completed),
            Exec(CurrentHour.AddHours(-2).AddMinutes(3), RtPipelineExecutionStatusEnum.Failed)
        };
        var cutoffs = new List<DateTime>();
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, PipelineId,
                Arg.Do<DateTime>(cutoffs.Add), Arg.Any<int>())
            .Returns(folded, new List<RtPipelineExecution>());

        // Retained after the fold: the 09:00 execution only.
        Retained(Exec(CurrentHour.AddHours(-1).AddMinutes(5), RtPipelineExecutionStatusEnum.Completed));

        var foldWrites = new List<RtPipelineStatistics>();
        CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Do<RtPipelineStatistics>(s =>
                {
                    foldWrites.Add(s);
                    // The statistics update of the same pass reads what the fold wrote.
                    CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns(s);
                }), PipelineId)
            .Returns(Task.CompletedTask);

        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        // Hour-aligned cutoff: everything started before 09:00 is folded (retention ≥ 1h kept).
        await Assert.That(cutoffs[0]).IsEqualTo(CurrentHour.AddHours(-1));

        var foldWrite = foldWrites[0];
        await Assert.That(foldWrite.FoldedBefore).IsEqualTo(CurrentHour.AddHours(-1));
        await Assert.That(BucketAt(foldWrite, CurrentHour.AddHours(-3)).SuccessCount).IsEqualTo(2);
        await Assert.That(BucketAt(foldWrite, CurrentHour.AddHours(-2)).SuccessCount).IsEqualTo(2);
        await Assert.That(BucketAt(foldWrite, CurrentHour.AddHours(-2)).FailureCount).IsEqualTo(1);
        await Assert.That(BucketAt(foldWrite, CurrentHour.AddHours(-1)).SuccessCount).IsEqualTo(1);

        var final = foldWrites[^1];
        await Assert.That(final.Last24HoursSuccessCount).IsEqualTo(2 + 2 + 1);
        await Assert.That(final.Last24HoursFailureCount).IsEqualTo(1);
        await Assert.That(final.HourlyBuckets!.Sum(b => b.SuccessCount)).IsEqualTo(final.Last24HoursSuccessCount);
    }

    [Test]
    public async Task Fold_LegacyStatistics_MovesTheBoundaryPastThePartiallyFoldedHour()
    {
        // Pre-AB#5583 fold at 10:20 with a rolling cutoff of 09:20: hour 09:00 is partly folded.
        Statistics(null, Bucket(CurrentHour.AddHours(-1), 3, 0));

        var cutoffs = new List<DateTime>();
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, PipelineId,
                Arg.Do<DateTime>(cutoffs.Add), Arg.Any<int>())
            .Returns(
                [Exec(CurrentHour.AddHours(-1).AddMinutes(40), RtPipelineExecutionStatusEnum.Completed)],
                new List<RtPipelineExecution>());
        Retained(Exec(CurrentHour.AddMinutes(10), RtPipelineExecutionStatusEnum.Completed));

        var writes = new List<RtPipelineStatistics>();
        CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Do<RtPipelineStatistics>(s =>
                {
                    writes.Add(s);
                    CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns(s);
                }), PipelineId)
            .Returns(Task.CompletedTask);

        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        await Assert.That(cutoffs[0]).IsEqualTo(CurrentHour);
        await Assert.That(writes[0].FoldedBefore).IsEqualTo(CurrentHour);
        await Assert.That(BucketAt(writes[0], CurrentHour.AddHours(-1)).SuccessCount).IsEqualTo(4);

        var final = writes[^1];
        await Assert.That(final.Last24HoursSuccessCount).IsEqualTo(5);
        await Assert.That(BucketAt(final, CurrentHour).SuccessCount).IsEqualTo(1);
    }

    [Test]
    public async Task Fold_NothingToFold_BoundaryAdvances_DropsSnapshotHoursBehindIt()
    {
        // Boundary 08:00 from an earlier pass; its 08:00 snapshot has no retained executions left.
        Statistics(CurrentHour.AddHours(-2), Bucket(CurrentHour.AddHours(-2), 4, 0));
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, PipelineId,
                Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());
        Retained();

        var writes = new List<RtPipelineStatistics>();
        CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Do<RtPipelineStatistics>(s =>
                {
                    writes.Add(s);
                    CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns(s);
                }), PipelineId)
            .Returns(Task.CompletedTask);

        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);

        await Assert.That(writes[0].FoldedBefore).IsEqualTo(CurrentHour.AddHours(-1));
        await Assert.That(writes[0].HourlyBuckets!.Count).IsEqualTo(0);
        await Assert.That(writes[^1].Last24HoursSuccessCount).IsEqualTo(0);
    }

    [Test]
    public async Task Fold_RepeatedSweepsWithoutNewExecutions_AreIdempotent()
    {
        Statistics(CurrentHour.AddHours(-1),
            Bucket(CurrentHour.AddHours(-3), 2, 1), // folded history
            Bucket(CurrentHour.AddHours(-1), 9, 9)); // stale snapshot
        CommunicationRepository.GetTerminalExecutionsOlderThanAsync(TenantId, PipelineId,
                Arg.Any<DateTime>(), Arg.Any<int>())
            .Returns(new List<RtPipelineExecution>());
        Retained(
            Exec(CurrentHour.AddHours(-1).AddMinutes(5), RtPipelineExecutionStatusEnum.Completed),
            Exec(CurrentHour.AddMinutes(5), RtPipelineExecutionStatusEnum.Failed));

        var writes = new List<RtPipelineStatistics>();
        CommunicationRepository.UpsertPipelineStatisticsAsync(TenantId,
                Arg.Do<RtPipelineStatistics>(s =>
                {
                    writes.Add(s);
                    CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns(s);
                }), PipelineId)
            .Returns(Task.CompletedTask);

        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);
        var first = writes[^1];
        await PipelineExecutionService.FoldAndPruneExecutionsAsync(TenantId, 1);
        var second = writes[^1];

        // Same boundary, no fold write: the snapshot is replaced, never added to.
        await Assert.That(writes.Count).IsEqualTo(2);
        await Assert.That(second.FoldedBefore).IsEqualTo(first.FoldedBefore);
        await Assert.That(second.Last24HoursSuccessCount).IsEqualTo(first.Last24HoursSuccessCount);
        await Assert.That(second.Last24HoursFailureCount).IsEqualTo(first.Last24HoursFailureCount);
        await Assert.That(second.Last24HoursSuccessCount).IsEqualTo(2 + 1);
        await Assert.That(second.Last24HoursFailureCount).IsEqualTo(1 + 1);
        await Assert.That(second.HourlyBuckets!.Select(b => (b.HourStartAt, b.SuccessCount, b.FailureCount)))
            .IsEquivalentTo(first.HourlyBuckets!.Select(b => (b.HourStartAt, b.SuccessCount, b.FailureCount)));
    }

    // ---------------------------------------------------------------- helpers

    private RtPipelineStatistics Statistics(DateTime? foldedBefore,
        params RtPipelineStatisticsHourBucketRecord[] buckets)
    {
        var statistics = new RtPipelineStatistics
        {
            FoldedBefore = foldedBefore,
            LastExecutionAt = CurrentHour.AddHours(-30),
            HourlyBuckets = new AttributeRecordValueList<RtPipelineStatisticsHourBucketRecord>(
                buckets.Cast<RtRecord>().ToList())
        };
        CommunicationRepository.GetPipelineStatisticsAsync(TenantId, PipelineId).Returns(statistics);
        return statistics;
    }

    private void Retained(params RtPipelineExecution[] executions)
    {
        var sorted = executions.OrderByDescending(e => e.StartedAt).ToList();
        CommunicationRepository.GetPipelineExecutionsAsync(TenantId, PipelineId,
                Arg.Any<DateTime?>(), Arg.Any<DateTime?>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns(x => x.ArgAt<int>(4) == 0 ? sorted : new List<RtPipelineExecution>());
    }

    private static RtPipelineStatisticsHourBucketRecord Bucket(DateTime hour, int success, int failure)
    {
        return new RtPipelineStatisticsHourBucketRecord
        {
            HourStartAt = hour,
            SuccessCount = success,
            FailureCount = failure,
            TotalDurationMs = 0,
            DurationCount = 0
        };
    }

    private static RtPipelineStatisticsHourBucketRecord BucketAt(RtPipelineStatistics statistics, DateTime hour)
    {
        return statistics.HourlyBuckets!.Single(b => b.HourStartAt == hour);
    }

    private static RtPipelineExecution Exec(DateTime startedAt, RtPipelineExecutionStatusEnum status)
    {
        return new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkPipelineExecutionTypeId,
            ExecutionId = Guid.NewGuid().ToString(),
            Status = status,
            StartedAt = startedAt,
            DurationMs = 10
        };
    }
}
