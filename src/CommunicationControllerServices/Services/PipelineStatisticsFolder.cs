using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Pure fold/merge/window logic behind the hourly statistics buckets (AB#4370, AB#5583).
/// Kept free of repository/session concerns so every rule is unit-testable.
/// </summary>
/// <remarks>
/// <para><b>Window semantics (AB#5583).</b> There is exactly one source for the 12h/24h/30d
/// counters: <c>RtPipelineStatistics.HourlyBuckets</c>. A window of N hours is the N UTC clock-hour
/// buckets ending with the current, still partial hour — <c>[FloorToHour(now) - (N-1)h, now]</c> —
/// which is exactly what a 24-slot hourly histogram renders. The counters are the plain sum of the
/// buckets in that window, so a client that sums the buckets gets the same number.</para>
/// <para><b>Buckets cover every counted execution.</b> Hours before <c>FoldedBefore</c> hold folded
/// history (the executions are deleted); hours at or after it are recomputed from the still-retained
/// executions on every sweep. The boundary is hour-aligned, so no hour is ever split between the two
/// halves and nothing is counted twice.</para>
/// <para><b>LastHour</b> is the exception: a rolling 60-minute count over the retained executions
/// (it feeds the alertable failure gauge, which must not reset at every clock hour). The fold
/// boundary never passes <c>now - retentionHours</c> (≥ 1h), so the rolling hour is always
/// completely retained and the count is exact. Single exception: the one-time upgrade fold of
/// statistics written before AB#5583 may move the boundary up to one hour further (see
/// <c>PipelineExecutionService.FoldAndPrunePipelineAsync</c>), so LastHour can undercount for at
/// most one hour after the upgrade.</para>
/// <para><b>Classification</b> is shared by every path: Completed = success, Failed = failure;
/// Running, Interrupted and Cancelled count as neither (<see cref="Classify" />).</para>
/// </remarks>
internal static class PipelineStatisticsFolder
{
    /// <summary>Hours of the longest window (30 days); buckets before its start are dropped.</summary>
    public const int RetentionWindowHours = 30 * 24;

    /// <summary>How an execution status counts in the statistics.</summary>
    internal enum ExecutionOutcome
    {
        /// <summary>Not counted as success or failure (Running, Interrupted, Cancelled).</summary>
        Neither,
        /// <summary>Counted as success.</summary>
        Success,
        /// <summary>Counted as failure.</summary>
        Failure
    }

    /// <summary>
    /// The single status classification used by the fold, the live snapshot and the rolling hour.
    /// </summary>
    public static ExecutionOutcome Classify(RtPipelineExecutionStatusEnum status) => status switch
    {
        RtPipelineExecutionStatusEnum.Completed => ExecutionOutcome.Success,
        RtPipelineExecutionStatusEnum.Failed => ExecutionOutcome.Failure,
        _ => ExecutionOutcome.Neither
    };

    /// <summary>
    /// Start of an N-hour window: the N clock hours ending with the current partial hour.
    /// </summary>
    public static DateTime WindowStart(DateTime now, int hours)
    {
        return FloorToHour(now).AddHours(-(Math.Max(1, hours) - 1));
    }

    /// <summary>
    /// Hour-aligned fold cutoff. Terminal executions that started before it are folded and
    /// deleted. Always at or before <c>now - retentionHours</c> (so an execution is kept at least
    /// that long) and never moves backwards behind <paramref name="foldedBefore" />. The one-time
    /// upgrade of pre-AB#5583 statistics may move the boundary past this cutoff by up to one hour.
    /// </summary>
    public static DateTime FoldCutoff(DateTime now, int retentionHours, DateTime? foldedBefore)
    {
        var aligned = FloorToHour(now).AddHours(-Math.Max(1, retentionHours));
        return foldedBefore > aligned ? foldedBefore.Value : aligned;
    }

    /// <summary>
    /// The folded-history half of a persisted bucket list: buckets before
    /// <paramref name="foldedBefore" />. Statistics written before AB#5583 carry no boundary and
    /// held folded buckets only, so without a boundary every bucket is folded history.
    /// </summary>
    public static List<RtPipelineStatisticsHourBucketRecord> FoldedHistory(
        IEnumerable<RtPipelineStatisticsHourBucketRecord>? buckets, DateTime? foldedBefore)
    {
        return (buckets ?? [])
            .Where(b => foldedBefore == null || b.HourStartAt < foldedBefore.Value)
            .ToList();
    }

    /// <summary>
    /// The live-snapshot half of a persisted bucket list: buckets at or after
    /// <paramref name="from" />. Only meaningful when the statistics carry a boundary.
    /// </summary>
    public static List<RtPipelineStatisticsHourBucketRecord> SnapshotFrom(
        IEnumerable<RtPipelineStatisticsHourBucketRecord>? buckets, DateTime from)
    {
        return (buckets ?? []).Where(b => b.HourStartAt >= from).ToList();
    }

    /// <summary>
    /// Mutable per-hour aggregate used while folding a batch or building the live snapshot.
    /// </summary>
    internal sealed class BucketAccumulator
    {
        public int SuccessCount;
        public int FailureCount;
        public long TotalDurationMs;
        public int DurationCount;
    }

    /// <summary>
    /// Aggregate totals of one window, summed over the hourly buckets.
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
            // AB#4924: a Queued execution has no StartedAt and must not fold into the
            // statistics before it actually runs — it would land in whatever bucket a
            // default timestamp falls into and inflate that hour's counts. It is folded
            // on the lease grant, when StartedAt is stamped.
            if (exec.StartedAt is not { } startedAt)
            {
                continue;
            }

            var hour = FloorToHour(startedAt);
            if (!deltas.TryGetValue(hour, out var acc))
            {
                acc = new BucketAccumulator();
                deltas[hour] = acc;
            }

            switch (Classify(exec.Status))
            {
                case ExecutionOutcome.Success:
                    acc.SuccessCount++;
                    break;
                case ExecutionOutcome.Failure:
                    acc.FailureCount++;
                    break;
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
    /// Sums all buckets whose hour starts at or after <paramref name="from" /> (floored to the
    /// hour). Pass <see cref="WindowStart" /> so the window is exactly N whole clock-hour buckets
    /// — never a 25th bucket straddling a rolling edge (AB#5583).
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

    /// <summary>Start of the UTC clock hour containing <paramref name="value" />.</summary>
    public static DateTime FloorToHour(DateTime value)
    {
        return new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0, DateTimeKind.Utc);
    }
}
