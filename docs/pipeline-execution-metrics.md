---
description: "Pipeline execution metrics: entities, hub methods, durability across adapter restart, hourly buckets, OTel metrics"
applies_to: **/Services/PipelineExecution*.cs, **/BackgroundServices/ExecutionCleanup*.cs, **/Services/PipelineStatisticsFolder.cs
---

## Pipeline Execution Metrics

The `PipelineExecutionService` tracks pipeline execution metrics reported by adapters via SignalR. This enables real-time monitoring and historical analysis of pipeline performance.

### CK Model Entities

| Type | Description |
|------|-------------|
| `RtPipelineExecution` | Records individual pipeline execution with status, timing, and optional input data |
| `RtPipelineStatistics` | Aggregated statistics per pipeline (1h, 12h, 24h, 30d periods) |
| `RtPipelineExecutionStatusEnum` | Status: Running, Completed, Failed, Interrupted, Cancelled |
| `RtPipelineTriggerTypeEnum` | Trigger: Manual, Scheduled, Event, Startup |

### Hub Methods (AdapterHub)

Adapters report execution lifecycle via SignalR:
- `ReportExecutionStartAsync(PipelineExecutionStartDto)` - Reports execution start
- `ReportExecutionEndAsync(PipelineExecutionEndDto)` - Reports execution completion
- `ReportInterruptedExecutionResultAsync(PipelineExecutionEndDto)` - Reports final status after reconnect
- `GetInterruptedExecutionIdsAsync()` - Gets IDs of executions interrupted by disconnect
- `ReportPipelineStatusAsync(PipelineStatusReportDto)` - Writes the status line a trigger node
  reported after a poll to that pipeline's live `StatusMessage` — and ONLY that
  attribute: `AdapterService.ReportPipelineStatusAsync` → `CommunicationRepository.
  SetPipelineStatusMessageAsync`, deliberately not `SetPipelineDeploymentStateAsync`, whose
  `ApplyDeploymentErrorTracking` would clear a `LastDeploymentError` the operator is still looking
  at. The pipeline must be one of the pipelines the cache says are deployed to the reporting
  adapter (the adapter identity comes from the connection headers); anything else is logged at
  Warn and dropped. The line is truncated server-side at `AdapterService.MaxPipelineStatusMessageLength`
  (1000). Fire-and-forget on the wire like the metrics sample — the hub swallows and logs. An
  adapter built before the contract never calls it; an adapter built after it and connected to a
  controller WITHOUT this method has its `SendAsync` dropped server-side (SignalR logs an unknown
  hub method), so the pipeline's `StatusMessage` simply stays what the deploy wrote. Tests:
  `Hubs/AdapterHubTests/ReportPipelineStatusAsyncTests`, `Services/AdapterServiceTests/
  ReportPipelineStatusAsyncTests`, integration `Repository/PipelineStatusMessageTests`.

### Interruption Handling

When an adapter disconnects unexpectedly:
1. All running executions for that adapter are marked as `Interrupted`
2. On reconnect, adapter can query interrupted execution IDs
3. Adapter reports final status via `ReportInterruptedExecutionResultAsync`

### Execution Durability Across Adapter Restart

A pipeline execution runs as an in-memory `Task.Run` inside the adapter runtime
(`AdapterTriggerContext.StartExecutePipelineAsync`, octo-communication-sdk); its state is
tracked out-of-band in the `RtPipelineExecution` entity. When the adapter **process** restarts
(deployment, crash, pod eviction) the in-memory task is lost, so the record must be driven to a
terminal state instead of being left stuck in `Running` / `Interrupted`. Three cooperating
mechanisms guarantee this:

1. **Disconnect → `Interrupted`** (`AdapterHub.OnDisconnectedAsync` →
   `MarkExecutionsAsInterruptedAsync`). Fires whenever the controller detects an adapter drop
   *while the controller itself is healthy* — i.e. the common adapter-only restart. The
   `IShutdownState.IsShuttingDown` guard deliberately skips this during a **controller**
   rolling-upgrade: there the adapter is not gone, it is reconnecting to a surviving controller
   pod with its live tasks intact, so marking its executions `Interrupted` would be a false
   positive. (This is why the stuck reaper does **not** touch the shutdown guard.)

2. **Fresh-startup orphan resolution** (`IAdapterHub.FailOrphanedExecutionsAsync(processStartUtc)`
   → `PipelineExecutionService.FailOrphanedExecutionsForAdapterAsync` →
   `CommunicationRepository.FailOrphanedExecutionsForAdapterAsync`). The **SDK** calls this once
   on a fresh process start (`isReconnect == false`), never on a transient reconnect. A fresh
   process has an empty in-memory registry, so any `Running` / `Interrupted` execution for this
   adapter whose `StartedAt` predates the process start is an orphan from the previous process and
   is transitioned to `Failed`. This is the primary fix and covers both the adapter-only restart
   and the simultaneous controller+adapter restart.

3. **Connection-aware reaper** (`ExecutionCleanupBackgroundService` →
   `FailStuckExecutionsAsync` → `CommunicationRepository.FailStuckExecutionsAsync`). The backstop
   for the case where a disconnect was never cleanly detected (e.g. the controller also restarted
   and missed the `OnDisconnected`). Past the `PipelineExecutionStuckGraceMinutes` grace period it
   fails **all** stale `Interrupted` executions (an `Interrupted` execution implies its adapter
   disconnected) **plus** stale `Running` executions **whose owning adapter is not `Online`**. A
   `Running` execution on a live (`Online`) adapter is **never** failed, regardless of how long it
   runs — this is what protects legitimate long-running ETL pipelines (e.g. multi-million-row
   CrateDB inserts) from being killed. The reaper keys off the adapter's persisted
   `CommunicationState`, so it is correct across controller pods.

SDK safety net: `AdapterExecutionService.GetLocalExecutionStatus` returns `Failed` (not
optimistic `Completed`) when an interrupted execution cannot be found in the local registry — an
execution interrupted mid-flight must never be recorded as a success.

#### 🔴 A LEASED execution is exempt from the reaper while its lease can be live

Mechanism 3 spares a `Running` execution only while its adapter is `Online`, and the comment beside
it says why: *"a long-running pipeline on a connected adapter must never be failed by the reaper,
regardless of how long it runs"*. A **leased** adapter can never satisfy that condition — it owns no
process and never registers, so its `CommunicationState` stays `Unregistered` by construction, and
the process doing the work is a pool member hanging off the `AdapterPool`. The protection therefore
inverted itself for leasing: instead of sparing a healthy long run it failed **every** leased run past
`PipelineExecutionStuckGraceMinutes`.

Measured on the local kind cluster on 2026-09-23: a 16-minute leased run reported
`ReleaseLeaseAsync … Completed, success=true` from its member, and the reaper wrote `Failed` over it
**12 ms later** with *"Adapter offline; running execution orphaned by adapter restart"*. Two separate
defects, both fixed:

- `FailStuckExecutionsAsync` takes a second cutoff, `leasedGraceCutoff`. An execution with a
  `LeaseGrantedAt`, no `LeaseReleasedAt` and a grant newer than that cutoff is removed from the set
  **before** the adapter-state filter — it has an owner already: a member that disconnects is handled
  at once by `LeaseService.HandleMemberDisconnectedAsync`, and a lease past its TTL is reclaimed and
  re-queued by `ReapExpiredLeasesAsync`. The service computes the cutoff as
  `LeaseTtlMinutes + graceMinutes` from **the same option the scheduler grants with**, so the two
  cannot drift; past it no lease can still be holding the work (the controller restarted while a
  member died, and no disconnect was ever observed), which is the one case this reaper still owns.
- `ApplyFailedStatusAsync` takes the status it expects and **re-reads the batch inside its own
  transaction**, dropping anything that has moved on. The candidate read and the write live in
  different sessions, so a terminal result arriving in between used to be overwritten — which is
  exactly the 12 ms above. `FailOrphanedExecutionsForAdapterAsync` consequently applies its two
  status sets separately instead of concatenating them.

Note the neighbouring defaults are equal and independent: `PipelineExecutionStuckGraceMinutes` and
`LeaseTtlMinutes` are both 15, and both reapers run in the same `ExecutionCleanupBackgroundService`
iteration (stuck reaper first, lease reaper second).

Integration coverage lives in
`tests/CommunicationControllerServices.IntegrationTests/Repository/FailStuckAndOrphanedExecutionsTests.cs`
(connection-aware sparing of live long-runners; adapter-scoped pre-start orphan resolution; the leased exemption, arranged so both executions hang off a non-Online adapter and only the age
of the lease separates them; and that a finished execution is never overwritten).

### Hourly Statistics Buckets + Execution Fold

Per-execution entities do not scale for high-frequency event pipelines (a Loxone
state-change pipeline produced ~300k executions/day; the 3-day retention window held
~700k documents and 1.4M association edges, saturating MongoDB and timing out the Studio
data-flow page). Executions are telemetry — `RtPipelineStatistics` is the durable record:

- `PipelineStatistics.HourlyBuckets` (CK model 3.25.0, record
  `PipelineStatisticsHourBucket`: HourStartAt, SuccessCount, FailureCount,
  TotalDurationMs (Int64), DurationCount) carries per-hour aggregates for a rolling
  30 days. Buckets are keyed by the UTC hour the execution STARTED in.
- **Fold-then-prune** (`PipelineExecutionService.FoldAndPruneExecutionsAsync`): every
  cleanup iteration drains terminal executions older than
  `PipelineExecutionRetentionHours` per pipeline in 500er batches — fold the batch into
  the buckets, persist the statistics entity, THEN erase exactly those executions
  (`DeleteExecutionsAsync`, `DeleteOptions.Erase`). Fold set == delete set, so nothing is
  double-counted or lost; a crash between upsert and delete double-counts at most one
  batch. Running executions are never drained regardless of age (protects long-running
  ETL pipelines — same contract as the reaper).
- **Window recompute** (`UpdateStatisticsAsync`): sliding windows (1h/12h/24h/30d) =
  bucket sums (`PipelineStatisticsFolder.SumBuckets`) + a live scan over the still-retained
  executions. The two sources are disjoint by construction. Buckets older than 30 days are
  pruned on write (rebuild + reassign the record list — never mutate an
  `AttributeValueList` in place, it materializes per read). `LastExecutionAt` never
  regresses when executions are folded away.
- Pure fold/merge/window rules live in `PipelineStatisticsFolder` (unit-tested); the
  MongoDB round-trip of the bucket record array is pinned by
  `FoldAndPruneRepositoryTests`.

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `PipelineExecutionReportProcessor` | Continuous | Drains execution reports from Channel in batches, bulk-inserts starts and bulk-updates completions |
| `ExecutionCleanupBackgroundService` | `PipelineExecutionStuckCheckIntervalMinutes` (default 5 min) | Per iteration: (1) connection-aware stuck reaper; (2) **execution fold** — terminal executions older than `PipelineExecutionRetentionHours` (default 1h) are folded into the hourly buckets on `RtPipelineStatistics` and then **erased**, and the sliding-window counters are refreshed for every pipeline; (3) daily orphan sweep erasing executions older than `PipelineExecutionRetentionDays` unconditionally (safety net for executions whose pipeline no longer exists). All deletes use `DeleteOptions.Erase` — the engine default `DeleteStrategies.Archive` only set `rtState=Archived` and let the collection grow unbounded (1M+ docs per tenant). The former hourly `PipelineStatisticsBackgroundService` was removed — folding owns statistics freshness. |
| `WorkloadStateMetricsBackgroundService` | `WorkloadStateMetricsIntervalMinutes` (default 5 min, also the startup grace) | Per-tenant observability switch: publishes the `DeploymentState` / `CommunicationState` / `ConfigurationState` severity of every adapter, application and pool of every tenant that opted in via `System/TenantModeConfiguration.PublishWorkloadObservability`, plus the per-tenant `octo.workload.sweep.*` sign of life. A tenant that did not opt in is skipped silently. It is also the process's **single reader of that flag**: the verdict is published into `WorkloadObservabilityOptIn`, which gates the pipeline and lifecycle instruments too, and a tenant that drops out is forgotten in all three families here. See "The per-tenant observability switch" and "OTel metrics for workload states". |

### Service Methods

```csharp
// IPipelineExecutionService
Task StartExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto startDto);
Task CompleteExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto endDto);
Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId);
Task ReportInterruptedExecutionResultAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto endDto);
Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId);
Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);
Task UpdateAllStatisticsAsync(string tenantId);
Task<int> CleanupOldExecutionsAsync(string tenantId, int retentionDays);
Task<int> TimeoutStaleExecutionsAsync(string tenantId, int timeoutHours);       // legacy, connection-unaware
Task<int> FailStuckExecutionsAsync(string tenantId, int graceMinutes);          // connection-aware reaper, leased exemption
Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, DateTime beforeUtc); // connection-aware reaper
```

### OTel metrics for pipeline executions

Before this, a pipeline execution existed **only as unstructured log text**
(`PipelineExecutionService.BatchStartExecutionsAsync|[tenant] Batch started N executions …`).
Log bodies are not a dimension of the `dash0.logs` metric and the inferred
`dash0.log.attribute.*` keys do not attach to it, so every rate grouped by them is 0 — there was
no way to alert on a failing or a stalled pipeline at all. On 2026-09-29 the `Monitoring.CkHealth`
pipeline on a test environment stopped after two runs (12:19, 13:19 UTC) and nobody noticed for four hours.

`PipelineExecutionMetrics` (static, mirroring `WorkloadLifecycleMetrics`) emits four instruments on
the **same meter `Meshmakers.Octo.Communication`** that octo-common-services' `ObservabilityBuilder`
already registers — so no observability wiring changed.

🔴 **Gated by the per-tenant opt-in since 2026-09-30.** These instruments shipped
unconditional (175 series from the test environment while nobody had opted in); they now answer to
`System/TenantModeConfiguration.PublishWorkloadObservability` like the workload-state family. The
counter is gated where it is recorded, the three gauges inside their collection callbacks — so the
statistics sweep still does its persistence work for a non-opted-in tenant and only its publication
stops. Details and reasoning: "The per-tenant observability switch".

| Instrument | Kind | Tags | Meaning |
|---|---|---|---|
| `octo.pipeline.execution.count` | counter `{execution}` | `octo.tenant.id`, `octo.pipeline.outcome` (`completed`/`failed`/`cancelled`/`interrupted`) | Adapter-reported execution outcomes, as they arrive |
| `octo.pipeline.execution.age` | observable gauge `s` | `octo.tenant.id`, `octo.pipeline.rt_id`, `octo.pipeline.name`, `octo.pipeline.deployment_state` | Seconds since this pipeline's last execution **started**; `-1` = never executed |
| `octo.pipeline.execution.failures` | observable gauge `{execution}` | same as above | Failed executions in the last hour (**0 when healthy**) |
| `octo.pipeline.execution.successes` | observable gauge `{execution}` | same as above | Succeeded executions in the last hour |

🔴 **The mistake this does not repeat.** The existing `octo.ck.*` gauges (emitted by the
`Monitoring.CkHealth` pipeline, not by this service) are **fault-only**: a healthy library emits
nothing. Absence there is ambiguous — "repaired" and "dead" look identical — and a staleness rule
built on them fires every time somebody fixes something. Every instrument here is published for
**every pipeline the sweep enumerates**, healthy or not, so a missing series means exactly one
thing: the controller is not reporting.

**Where the gauges come from.** `UpdateStatisticsAsync` — which `ExecutionCleanupBackgroundService`
already runs for every pipeline of every enabled tenant on each fold sweep
(`PipelineExecutionStuckCheckIntervalMinutes`, default 5 min) — publishes them, on **every** exit
path including the two that skip the database write. Two properties this buys over event-driven
counters: the values derive from persisted state, so they **survive a controller restart** instead
of starting from zero, and they exist for a pipeline that has not executed in weeks — the case that
must be alertable. Freshness is the sweep interval; the *age* is computed against the wall clock in
the gauge callback, so it keeps rising between sweeps. The identity half of the labels
(`name`, `deployment_state`) comes from `FoldAndPruneExecutionsAsync` / `UpdateAllStatisticsAsync`
via `ObservePipeline`, published **before** the fold so a pipeline whose statistics update throws is
still exported with a readable name. After a controller restart the gauges are empty until the
first sweep (the service's 5-minute startup delay plus one interval).

**Cardinality** — the cost driver, bounded on purpose:

- Gauges are tenant × pipeline (three series per pipeline). Pipelines are operator-authored, tens
  per tenant, slowly changing. Deleted pipelines are dropped by `RetainPipelines(tenantId, seen)` at
  the end of each sweep; a tenant that falls out of `GetEnabledTenantIds()` between two sweeps is
  dropped by `ForgetTenant` (the diff is kept in `ExecutionCleanupBackgroundService._lastSweptTenantIds`).
  Without either, a deleted pipeline's age gauge would climb forever and alert about nothing.
- The counter is tenant × outcome only — four series per tenant. **Deliberately not per pipeline**:
  executions are the high-frequency event here (batches of 100), the per-pipeline breakdown is
  already covered by the gauges at fixed cost, and tenant × pipeline × outcome on a hot path is how
  a metrics bill doubles overnight. The completion DTO does not carry the pipeline id either, so a
  per-pipeline counter would need a per-execution lookup or an in-process correlation map.
- Nothing carries an execution id (unbounded), an error message (effectively unbounded), a trigger
  type or an adapter (multiply every series, change no alerting decision).

`octo.pipeline.deployment_state` collapses `Enabled == false` and `DeploymentState` into one tag
(`disabled` wins over the persisted state) so an alert rule can express "expected to run" as a
single matcher instead of a join. A switched-off pipeline stays **visible** — silence would be
ambiguous again — it is just filterable.

**Counter semantics.** It counts *reported* outcomes, not distinct executions: an adapter replaying
a completion after a reconnect is counted again (the buffered-sync path filters duplicates, the live
batch path cannot). Executions the stuck reaper failed are **not** counted (no adapter ever reports
them) — those show up in `octo.pipeline.execution.failures`, which is derived from persisted status
and is therefore the authoritative per-pipeline number. `Running` is not an outcome.

**Alerting contract (PromQL, Dash0):** metric names keep their dots as the value of
`otel_metric_name`; only label keys become underscored.

```promql
# A pipeline is running into errors
max by (octo_tenant_id, octo_pipeline_rt_id, octo_pipeline_name)
  ({otel_metric_name="octo.pipeline.execution.failures", otel_metric_type="gauge"}) > 0

# A pipeline that was running has stopped running (threshold in seconds; -1 = never ran, excluded by ">")
max by (octo_tenant_id, octo_pipeline_rt_id, octo_pipeline_name)
  ({otel_metric_name="octo.pipeline.execution.age", otel_metric_type="gauge",
    octo_pipeline_deployment_state="deployed"}) > 3600
```

The stall threshold is per expectation, not per pipeline — pick it above the slowest schedule you
want covered (an hourly pipeline: 2×3600; a daily one needs its own rule). A per-pipeline expected
interval derived from the `RtPipelineTrigger` cron would remove that judgement call and is the
obvious follow-up.

Tests: `Services/PipelineExecutionMetricsTests` (through a real `MeterListener`, so the assertions
are about the names and tags the exporter actually publishes; unique tenant per test because the
instruments are process-wide and the suite runs concurrently) and
`Services/PipelineExecutionServiceTests/ExecutionMetricsWiringTests` (that the service actually
feeds them — a refactor dropping the calls would otherwise leave every test green while the
alerting went blind again).
