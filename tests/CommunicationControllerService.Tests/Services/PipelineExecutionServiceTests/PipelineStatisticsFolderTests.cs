using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.PipelineExecutionServiceTests;

/// <summary>
/// Pins the pure fold/merge/window rules behind the hourly statistics buckets (AB#4370).
/// </summary>
internal class PipelineStatisticsFolderTests
{
    private static readonly DateTime Hour10 = new(2026, 7, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hour11 = new(2026, 7, 13, 11, 0, 0, DateTimeKind.Utc);

    private static RtPipelineExecution Execution(DateTime startedAt, RtPipelineExecutionStatusEnum status,
        int? durationMs = null)
    {
        return new RtPipelineExecution
        {
            ExecutionId = Guid.NewGuid().ToString(),
            StartedAt = startedAt,
            Status = status,
            DurationMs = durationMs
        };
    }

    [Test]
    public async Task ToBucketDeltas_GroupsByStartHour_AndCountsPerStatus()
    {
        var executions = new[]
        {
            Execution(Hour10.AddMinutes(5), RtPipelineExecutionStatusEnum.Completed, 100),
            Execution(Hour10.AddMinutes(59), RtPipelineExecutionStatusEnum.Failed, 300),
            // Cancelled/Interrupted count into neither success nor failure but keep durations
            Execution(Hour10.AddMinutes(30), RtPipelineExecutionStatusEnum.Cancelled, 50),
            Execution(Hour11.AddMinutes(1), RtPipelineExecutionStatusEnum.Completed)
        };

        var deltas = PipelineStatisticsFolder.ToBucketDeltas(executions);

        await Assert.That(deltas.Count).IsEqualTo(2);
        await Assert.That(deltas[Hour10].SuccessCount).IsEqualTo(1);
        await Assert.That(deltas[Hour10].FailureCount).IsEqualTo(1);
        await Assert.That(deltas[Hour10].TotalDurationMs).IsEqualTo(450L);
        await Assert.That(deltas[Hour10].DurationCount).IsEqualTo(3);
        await Assert.That(deltas[Hour11].SuccessCount).IsEqualTo(1);
        await Assert.That(deltas[Hour11].DurationCount).IsEqualTo(0);
    }

    [Test]
    public async Task MergeBuckets_AddsDeltasToExistingBucket_AndReturnsNewOrderedList()
    {
        var existing = new List<RtPipelineStatisticsHourBucketRecord>
        {
            new()
            {
                HourStartAt = Hour11, SuccessCount = 2, FailureCount = 0, TotalDurationMs = 20, DurationCount = 2
            },
            new()
            {
                HourStartAt = Hour10, SuccessCount = 1, FailureCount = 1, TotalDurationMs = 100, DurationCount = 2
            }
        };
        var deltas = PipelineStatisticsFolder.ToBucketDeltas(
        [
            Execution(Hour10.AddMinutes(10), RtPipelineExecutionStatusEnum.Completed, 50)
        ]);

        var merged = PipelineStatisticsFolder.MergeBuckets(existing, deltas, DateTime.MinValue);

        await Assert.That(merged.Count).IsEqualTo(2);
        await Assert.That(merged[0].HourStartAt).IsEqualTo(Hour10);
        await Assert.That(merged[0].SuccessCount).IsEqualTo(2);
        await Assert.That(merged[0].FailureCount).IsEqualTo(1);
        await Assert.That(merged[0].TotalDurationMs).IsEqualTo(150L);
        await Assert.That(merged[0].DurationCount).IsEqualTo(3);
        await Assert.That(merged[1].HourStartAt).IsEqualTo(Hour11);
        await Assert.That(merged[1].SuccessCount).IsEqualTo(2);
    }

    [Test]
    public async Task MergeBuckets_PrunesBucketsOlderThanCutoff()
    {
        var existing = new List<RtPipelineStatisticsHourBucketRecord>
        {
            new() { HourStartAt = Hour10, SuccessCount = 5 },
            new() { HourStartAt = Hour11, SuccessCount = 7 }
        };

        var merged = PipelineStatisticsFolder.MergeBuckets(existing,
            new Dictionary<DateTime, PipelineStatisticsFolder.BucketAccumulator>(), Hour11);

        await Assert.That(merged.Count).IsEqualTo(1);
        await Assert.That(merged[0].HourStartAt).IsEqualTo(Hour11);
    }

    [Test]
    public async Task SumBuckets_IncludesBucketsFromWindowStart_ExcludesOlder()
    {
        var buckets = new List<RtPipelineStatisticsHourBucketRecord>
        {
            new() { HourStartAt = Hour10, SuccessCount = 3, FailureCount = 1, TotalDurationMs = 90, DurationCount = 3 },
            new() { HourStartAt = Hour11, SuccessCount = 2, FailureCount = 0, TotalDurationMs = 10, DurationCount = 1 }
        };

        var fromHour11 = PipelineStatisticsFolder.SumBuckets(buckets, Hour11.AddMinutes(30));
        var fromHour10 = PipelineStatisticsFolder.SumBuckets(buckets, Hour10);

        // A bucket straddling the window edge counts fully (1h granularity)
        await Assert.That(fromHour11.SuccessCount).IsEqualTo(2);
        await Assert.That(fromHour11.FailureCount).IsEqualTo(0);
        await Assert.That(fromHour10.SuccessCount).IsEqualTo(5);
        await Assert.That(fromHour10.FailureCount).IsEqualTo(1);
        await Assert.That(fromHour10.TotalDurationMs).IsEqualTo(100L);
        await Assert.That(fromHour10.AvgDurationMs).IsEqualTo(25);
    }

    [Test]
    public async Task FloorToHour_ZeroesMinutesSecondsAndKeepsUtc()
    {
        var floored = PipelineStatisticsFolder.FloorToHour(Hour10.AddMinutes(59).AddSeconds(59));

        await Assert.That(floored).IsEqualTo(Hour10);
        await Assert.That(floored.Kind).IsEqualTo(DateTimeKind.Utc);
    }
}
