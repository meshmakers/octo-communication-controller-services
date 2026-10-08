# Pipeline statistics: windows, source and classification (AB#5583)

`RtPipelineStatistics` carries window counters (`LastHour*`, `Last12Hours*`, `Last24Hours*`,
`Last30Days*`) and the per-hour `HourlyBuckets`. Clients (Refinery Studio sparkline, Data Flows
list, cockpit) show both side by side, so they must agree. Before AB#5583 they did not: the Studio
showed `34 ok / 335 failed` in the header and `17 / 342` summed over the 24 hourly bars.

## Why they disagreed

| Cause | Effect |
|---|---|
| Totals used a **rolling** `now - 24h` window but counted the bucket straddling that edge **fully** (`SumBuckets` floors the start) — 25 clock hours. | Totals higher than the 24 bars. |
| `HourlyBuckets` held **folded** executions only. Executions still retained (the last ~1 h, up to two clock hours) were in the totals but in no bucket; the Studio seeds them into the current bar only, from the rolling `LastHour` counts. | Recent executions missing from the bars (fewer successes). |
| `LastHour*` was the live scan **plus the whole previous-hour bucket**; the Studio seeds `LastHour*` into the current bar while the previous bar already shows that bucket. | The previous hour counted twice in the bars (more failures). |

Status classification was already the same in both paths (Completed = success, Failed = failure),
but implemented twice; it is now one function (`PipelineStatisticsFolder.Classify`).

## The contract

**One source.** `HourlyBuckets` holds every counted execution of each UTC clock hour, folded
and still-retained alike:

- hours **before** `FoldedBefore` are folded history (their executions were deleted);
- hours **at or after** `FoldedBefore` are recomputed from the retained executions on every
  statistics sweep (a snapshot, replaced — never added to).

`FoldedBefore` (CK `System.Communication-3.41.0`) is hour-aligned, so no hour is ever split
between the two halves.

**One window.** A window of N hours is the N clock-hour buckets ending with the current, still
partial hour: `[FloorToHour(now) - (N-1)h, now]` (UTC). `Last12Hours*` / `Last24Hours*` /
`Last30Days*` (N = 12 / 24 / 720) are exactly the sum of `HourlyBuckets` over that window —
a 24-slot histogram ending with the current hour sums to `Last24Hours*`.

**LastHour is rolling.** `LastHour*` stays a rolling 60-minute count over the retained
executions: it feeds the alertable `octo.pipeline.execution.failures` gauge, which must not reset
to zero at every clock hour. The fold cutoff never passes `now - retentionHours` (≥ 1 h), so the
rolling hour is always fully retained and the count is exact.

**One classification.** Completed = success, Failed = failure; Running, Interrupted and Cancelled
count as neither — in the fold, the snapshot and the rolling hour alike.

## The fold

`ExecutionCleanupBackgroundService` calls `FoldAndPruneExecutionsAsync` every
`PipelineExecutionStuckCheckIntervalMinutes`. With one `now` for the whole pass:

1. cutoff = `FloorToHour(now) - PipelineExecutionRetentionHours` (never behind the stored
   boundary). A terminal execution is therefore kept between `retentionHours` and
   `retentionHours + 1` hours.
2. Terminal executions started before the cutoff are merged into the folded history (snapshot
   hours that fell behind the cutoff are dropped first — their executions are exactly what is
   being folded), persisted together with `FoldedBefore = cutoff`, then deleted.
3. `UpdateStatisticsAsync(now)` rebuilds the snapshot hours and the counters from the same list.

A straggler — an execution that turns terminal after its hour was folded — is not counted until
the next fold merges it into its hour (minutes later).

**Upgrade.** Statistics written before AB#5583 have no `FoldedBefore` and their newest bucket may
hold part of an hour whose rest is still retained. The first fold after the upgrade moves the
boundary past that hour, i.e. it folds (and deletes) up to one hour of executions earlier than the
retention would — once. Until that first fold the counters are computed from buckets + retained
executions, but only the folded buckets are persisted.

## Clients

A client that sums the buckets in the 24 clock hours ending with the current hour gets
`Last24Hours*`. It must **not** add `LastHour*` on top of an empty current bucket (that double-counts
the previous hour's tail), and it should anchor its window on `LastUpdatedAt` rather than the
browser clock when the two straddle an hour boundary.
