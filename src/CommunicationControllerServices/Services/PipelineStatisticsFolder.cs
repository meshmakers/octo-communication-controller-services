using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Pure fold/merge/window logic behind the hourly statistics buckets (AB#4370). Terminal
/// executions older than the retention window are folded into per-hour buckets on the
/// pipeline's RtPipelineStatistics entity and then physically deleted; the sliding-window
/// counters are recomputed from the buckets plus the still-retained recent executions.
/// Kept free of repository/session concerns so every rule is unit-testable.
/// </summary>
internal static class PipelineStatisticsFolder
{
    /// <summary>
    /// Mutable per-hour aggregate used while folding a batch.
    /// </summary>
    internal sealed class BucketAccumulator
    {
        public int SuccessCount;
        public int FailureCount;
        public long TotalDurationMs;
        public int DurationCount;
    }

    /// <summary>
    /// Aggregate totals of one sliding window (buckets + live executions combined).
    /// </summary>
    internal sealed record WindowTotals(int SuccessCount, int FailureCount, long TotalDurationMs, int DurationCount)
    {
        public int AvgDurationMs => DurationCount > 0 ? (int)(TotalDurationMs / DurationCount) : 0;
    }

    /// <summary>
    /// Groups executions into hour-start-keyed deltas. Buckets are keyed by the UTC hour the
    /// execution STARTED in — same semantics as the previous full-rescan statistics.
    /// </summary>
    public static Dictionary<DateTime, BucketAccumulator> ToBucketDeltas(IEnumerable<RtPipelineExecution> executions)
    {
        var deltas = new Dictionary<DateTime, BucketAccumulator>();

        foreach (var exec in executions)
        {
            var hour = FloorToHour(exec.StartedAt);
            if (!deltas.TryGetValue(hour, out var acc))
            {
                acc = new BucketAccumulator();
                deltas[hour] = acc;
            }

            if (exec.Status == RtPipelineExecutionStatusEnum.Completed)
            {
                acc.SuccessCount++;
            }
            else if (exec.Status == RtPipelineExecutionStatusEnum.Failed)
            {
                acc.FailureCount++;
            }

            if (exec.DurationMs.HasValue)
            {
                acc.TotalDurationMs += exec.DurationMs.Value;
                acc.DurationCount++;
            }
        }

        return deltas;
    }

    /// <summary>
    /// Merges bucket deltas into the existing bucket list and drops buckets older than
    /// <paramref name="pruneBefore" />. Always returns a NEW list ordered by hour — an
    /// AttributeValueList materializes per read, so callers must reassign the attribute
    /// rather than mutate records in place.
    /// </summary>
    public static List<RtPipelineStatisticsHourBucketRecord> MergeBuckets(
        IEnumerable<RtPipelineStatisticsHourBucketRecord>? existing,
        IReadOnlyDictionary<DateTime, BucketAccumulator> deltas,
        DateTime pruneBefore)
    {
        var byHour = new Dictionary<DateTime, (int Success, int Failure, long TotalDurationMs, int DurationCount)>();

        foreach (var bucket in existing ?? [])
        {
            if (bucket.HourStartAt < pruneBefore)
            {
                continue;
            }

            byHour[bucket.HourStartAt] = (bucket.SuccessCount, bucket.FailureCount, bucket.TotalDurationMs,
                bucket.DurationCount);
        }

        foreach (var (hour, delta) in deltas)
        {
            if (hour < pruneBefore)
            {
                continue;
            }

            var current = byHour.TryGetValue(hour, out var value) ? value : (0, 0, 0L, 0);
            byHour[hour] = (current.Item1 + delta.SuccessCount, current.Item2 + delta.FailureCount,
                current.Item3 + delta.TotalDurationMs, current.Item4 + delta.DurationCount);
        }

        return byHour
            .OrderBy(kv => kv.Key)
            .Select(kv => new RtPipelineStatisticsHourBucketRecord
            {
                HourStartAt = kv.Key,
                SuccessCount = kv.Value.Item1,
                FailureCount = kv.Value.Item2,
                TotalDurationMs = kv.Value.Item3,
                DurationCount = kv.Value.Item4
            })
            .ToList();
    }

    /// <summary>
    /// Sums all buckets whose hour starts at or after <paramref name="from" />. A bucket that
    /// straddles the window edge is counted fully (1-hour granularity) — acceptable for the
    /// 12h/24h/30d windows; the 1h window is served exactly by the still-retained executions.
    /// </summary>
    public static WindowTotals SumBuckets(IEnumerable<RtPipelineStatisticsHourBucketRecord> buckets, DateTime from)
    {
        var success = 0;
        var failure = 0;
        long totalDuration = 0;
        var durationCount = 0;

        foreach (var bucket in buckets)
        {
            if (bucket.HourStartAt < FloorToHour(from))
            {
                continue;
            }

            success += bucket.SuccessCount;
            failure += bucket.FailureCount;
            totalDuration += bucket.TotalDurationMs;
            durationCount += bucket.DurationCount;
        }

        return new WindowTotals(success, failure, totalDuration, durationCount);
    }

    public static DateTime FloorToHour(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
    }
}
