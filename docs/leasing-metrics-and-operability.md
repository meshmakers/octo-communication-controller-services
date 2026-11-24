---
description: "Leasing: metrics, alerts and rollout operability"
applies_to: "**/Services/AdapterLeasingMetrics.cs"
---

## Leasing: metrics, alerts and rollout operability (increment 9)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §11. Alert rules live in
**`meshmakers-infrastructure`**, not here (§11.5).

`Services/AdapterLeasingMetrics` (static, mirroring `WorkloadLifecycleMetrics`) emits 18 instruments
on the meter **`Meshmakers.Octo.Communication`** — the same meter the workload lifecycle metrics use, so
octo-common-services' `ObservabilityBuilder` already registers it and nothing had to be wired up.

| Instrument | Kind | Purpose |
|---|---|---|
| `octo.lease.held.duration` | histogram (s) | `LeaseGrantedAt` → release; the span that prices the borrower |
| `octo.lease.work.duration` | histogram (s) | what the member reported it spent running the pipeline |
| `octo.lease.overhead.duration` | histogram (s) | held − work — **the amortisation number** |
| `octo.lease.granted.count` / `octo.lease.queue.wait` | counter / histogram (s) | served count and wait distribution per borrowing tenant — the two halves of fairness |
| `octo.lease.enqueued.count` | counter | the queue's in-rate against `granted.count` |
| `octo.lease.queue.depth` | gauge | waiting items **per pool and per borrowing tenant** |
| `octo.lease.queue.oldest_wait` | gauge (s) | what the wait signal thresholds |
| `octo.lease.pool.members` | gauge | members on *this* controller pod, by `available` / `leased` / `draining` |
| `octo.lease.scaleup.signal` / `.window` / `.count` | gauge / gauge (s) / counter | the signals, the window in force, and the decision they produced |
| `octo.lease.pool.undersized` | gauge | 1 while the pool should grow and is at `MaxReplicas` — the alertable condition |
| `octo.lease.refused.count` | counter | by `octo.lease.stage` and `octo.lease.refusal_reason` |
| `octo.lease.released.count` / `.interrupted.count` / `.requeued.count` / `.member_drained.count` | counters | outcomes, mid-lease failures, at-least-once retries, member churn |

🔴 **`octo.lease.member_drained.count` carries two reasons, not one**: `ttl_expiry` is a
member that lost a borrower's work, `pool_idle` is the pool giving up a member it no longer needs
(increment 7's scale-down). They must not share a series — a drain-loop alert has to be able to ignore
the healthy one. `LeaseDrainReason`'s label switch had a single default arm until then, so the new value
would have reported itself as `ttl_expiry`; `EveryDrainReason_HasItsOwnStableSnakeCaseLabel` now holds
that the same way `EveryRefusalReason_HasItsOwnStableSnakeCaseLabel` holds the refusal reasons.

Tags on every series: `octo.tenant.id` (the **borrowing** tenant), `octo.pool.tenant_id`,
`octo.pool.rt_id`, `octo.pool.name`.

Five decisions worth keeping:

- 🔴 **The work span is measured by the MEMBER and travels on the release**
  (`LeaseResultDto.WorkDurationMs`, set by `AdapterPoolClient`). The controller cannot measure it:
  it stamps `StartedAt` at claim time, so its own view of the two spans is identical by construction
  and the overhead would read as zero forever. Null when the work item never ran — no fabricated
  zero, which would say the pool spent 100 % of the lease on overhead.
- 🔴 **A refusal reason is an enum, not the message.** `LeaseGrantResult.Reason`
  (`LeaseRefusalReason`) is the label; `StatusMessage` names the tenant and the adapter and stays
  free text. Every refusal path goes through `LeaseService.Refuse(...)`, so a reason added later
  cannot be forgotten on the metric.
- 🔴 **The member id is never a label.** Bounded at any instant by `MaxReplicas`, unbounded over
  time — and draining is precisely the path that restarts members, so the label set would grow
  fastest while the drain counter fires. Drains and expiries are counted per **pool**.
- **`octo.lease.pool.undersized` publishes the answer, not the inputs** — same pattern, and the same
  reason, as `octo.workload.offline_unexpected`. ⚠️ It is computed from the members on *this* pod,
  inheriting increment 7's per-instance ceiling arithmetic; `max by (pool)` in the alert is a
  mitigation, not a fix.
- ⚠️ **Log volume.** A lease is frequent. Depth, wait, member states and signals are metrics and are
  logged nowhere; `ScheduleForPoolAsync`'s "no idle member" line fires once on the **transition**
  into exhaustion rather than on every five-second round.

**Pool observations expire, they are not evicted.** `SweepStalePools` drops a pool no round has
observed for three minutes. "Evict everything this round's topology did not contain" is wrong with
more than one controller pod: each sees a different subset.

**Traces: a lease cannot be traced end to end.** `ObservabilityBuilder` has no `AddSource(...)` at
all and nothing in the estate propagates trace context over SignalR. Correlation is by lease id and
execution id in the logs. See plan §11.3 before anyone builds half of it.

Tests: `Services/AdapterLeasingMetricsTests` (the instrument shapes),
`Services/LeaseServiceTests/LeasingMetricsTests`,
`Services/LeaseSchedulerServiceTests/SchedulerMetricsTests`, and the two increment 9 cases in
`Services/TriggerManagementServiceTests/LeasedAdapterEnqueueTests`. 🔴 All four carry
`[NotInParallel(nameof(MeterListener))]`, and so does `WorkloadLifecycleMetricsTests`: a listener
started or disposed on one thread mutates the subscription lists another thread's `Add` is walking,
and the symptom is a measurement that is never delivered — one short of sixteen, once in a few dozen
runs. Each harness also forces the metrics class constructor before starting its listener.

🔴 **`[NotInParallel]` does not make the listener callback single-threaded.** It keeps other
*listeners* away, nothing else: tests that never open one still run concurrently and record on the same
static instruments, and the callback runs on **their** threads. Every harness used to collect into a
plain `List<T>` from that callback, which loses measurements (two `Add`s on one slot) and leaves `null`
slots behind a concurrent resize — a CI build failed `SchedulerMetricsTests.AnIdleShrink_…` with a
`NullReferenceException` while filtering, and a local stress run (four suite loops side by side) also
produced a "never recorded" `octo.pipeline.execution.count` in `PipelineExecutionMetricsTests`, a
class without the constraint key. **Every metrics test collects through `Helper/MeasurementCapture`**
(`Collect` / `CollectAsync`, filter usually `MeasurementCapture.TaggedWith(tag, uniqueValue)`,
`instrumentsOwner` for statics-backed instruments): it applies the test's filter *inside* the callback,
keeps what passes in a `ConcurrentQueue`, and disposes the listener before returning a snapshot. Do not
write another hand-rolled `MeterListener` harness. `Helper/MeasurementCaptureTests` pins the concurrent
case on a meter of its own (fails 3/3 when the queue is swapped back to a `List`).
`Hubs/HubAuthorizationMetricsTests` keeps its own `Recorder` — it was already locked and listens to a
per-test `IMeterFactory`.
