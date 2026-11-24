---
description: "Leasing: the pool queue and its scheduler, sweeps, tick, drained release, scale-up and scale-down"
applies_to: **/Services/LeaseScheduler*.cs, **/BackgroundServices/LeaseScheduler*.cs, **/Repository/ExecutionLeaseClaimStore.cs
---

## Leasing: the pool queue and its scheduler (increment 7)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §9.

| Piece | Where |
|---|---|
| One queue per pool, rotation, priority, cap, TTL reaper, scale-up, **idle scale-down**, queue projection, cancel | `Services/LeaseSchedulerService.cs` |
| Scheduling round (default every **5 s**) | `BackgroundServices/LeaseSchedulerBackgroundService.cs` |
| Lease reaper (on the cleanup service's cadence, next to the stuck reaper) | `BackgroundServices/ExecutionCleanupBackgroundService.cs` |
| `Queued` is written here and nowhere else | `TriggerManagementService.StartExecutePipelineAsync` → `CommunicationRepository.EnqueueExecutionAsync` |
| `LeaseReleasedAt`, terminal-state fallback, interrupt + re-queue, drain | `Services/LeaseService.cs` |
| `GET/DELETE {tenantId}/v1/adapterPool/{id}/queue` — the shared contract all three surfaces consume | `TenantApi/v1/Controllers/AdapterPoolController.cs` |

🔴 **A `Leased` adapter never reaches the execute queue.** `StartExecutePipelineAsync` branches
*before* the wake gate: there is no workload to wake, and the per-pipeline execute queue has
no consumer, so a send would be a message nobody reads. A **dry run** deliberately does not queue —
it is a synchronous answer to a caller holding the request open, and parking it behind a rotation
would turn "validate this pipeline" into something that returns minutes later.

🔴 **A borrower that names no pool is refused at the enqueue, not queued.** The code here
used to say the lease would refuse it "with a named reason". It does not:
`LeaseSchedulerService.GetTopologyAsync` drops a leased adapter whose `LentFrom` mirror is missing or
unusable out of the topology, so nothing downstream ever reaches `GrantLeaseAsync` and no refusal is
ever produced. Measured on 2026-09-23 against a tenant carried over from the pre-4.0.1 model, where
the association replaced two attributes and **nothing re-links the adapter** (the migration note says
so explicitly): `POST /{tenant}/v1/pipeline/execute` answered **200**, the execution sat in `Queued`
with `errorMessage` null for as long as it was watched, and `CancelQueuedExecution` could not reach it
either — that verb is keyed by **pool**, and an adapter with no mirror belongs to none. The adapter
itself showed `statusMessage: null` and `lastDeploymentError: null`. So: nothing written, same stance
as the kill switch, and `TriggerManagementServiceException.LeasedAdapterNamesNoPool` names both
repairs (link it in the Studio, or `POST {tenantId}/v1/adapterPool/mirrors/refresh`). Counted as
`LeaseRefusalReason.BorrowerNamesNoPool` at the `Enqueue` stage.

The scheduler's own warning about that adapter now fires on the **transition** only (WARN once, DEBUG
on repeat) — the leasing design §9.8 rule, which this line had not been given: it warned on every scheduling
round, i.e. once a minute per broken adapter for as long as it stayed broken, which is how a warning
stops being read.

🔴 **A memo may only be swept on the cadence of the thing that writes it**, and the first attempt at
this got it wrong in a way no unit test could see. The refusal memo beside it is written on the
scheduling tick and swept against `LeaseSchedulerIntervalSeconds` (5 s × 10 rounds = 50 s); this one
is written by `GetTopologyAsync`, which rebuilds on `LeaseTopologyRefreshSeconds` (60 s). Swept
against the tick the entry had always expired by the time the topology looked again, so every
observation read as a transition and warned — measured live on 2026-09-23 as three consecutive rounds
and three WARNs, exactly as before the change. `NoPoolMemoRetentionSeconds()` derives the window from
the topology cadence (and never below the tick's own), and `NoPoolMemoRetentionTests` pins that
derivation rather than an observed interval: the defaults differ by a factor of twelve, and both
cadences cover two rounds run back to back.

🔴 **Fairness is round-robin across tenants, never global FIFO — and the class never crosses a
tenant boundary.** Both halves are load-bearing and both have a test that a global FIFO would fail:
`FairnessTests` arranges the starving tenant's work to be **older** than everyone else's, so arrival
order and fair order disagree. A fairness test a global FIFO would also pass proves nothing, and a
silent regression to global FIFO is the most likely way this scheduler stops being fair.

🔴 **The admission gate.** `ILeaseService.GrantLeaseAsync` takes an optional callback that runs after
an idle member is reserved and before the lease is pushed. The scheduler claims the work item there.
That position is forced: the claim writes `LeasedOnMemberId`, which needs a reserved member, and it
has to land before the member is handed anything, so a second controller pod that claimed the same
item first can stop this one from dispatching it. A gate that declines **or throws** puts the member
back — otherwise every lost race costs the pool a member.

🔴 **The claim is latched at the MongoDB filter level**, not merely checked. `CreateConditionalUpdate`
with `AttributeNewerThanGuard("attributes.leaseGrantedAt", DateTime.MinValue)` means "apply only
while unclaimed". The pre-read is not enough: two pods can both read `Queued` before either writes.
A conditional update reports nothing about whether it applied, so the claim re-reads and confirms.

🔴 **A re-queue is a NEW execution entity.** Concept §6 wants the previous attempt `Interrupted` *and*
the work re-queued; one entity cannot be both. Each attempt is one entity, so the interrupted one
keeps its own `LeaseGrantedAt`/`LeaseReleasedAt` span (a billing input, §4b) and the retry gets its
own `QueuedAt` and therefore its own honest `LeaseWaitMs`.

🔴 **`LeaseReleasedAt` is stamped on every release path**, TTL and crash included. A span stamped
only on the happy path silently under-bills exactly the failures a borrower did pay for. The release
is also the controller's **last-resort** completion signal: an execution still `Running` when the
member hands the process back is completed/failed from the lease result, but one that already
reached a terminal state through the normal adapter path is left strictly alone.

### 🔴 A `Queued` execution and the five sweeps — measured, and it corrected the plan

Three sweeps filter on an **exact** status (`FailStuckExecutionsAsync`,
`TimeoutStaleExecutionsAsync`, `FailOrphanedExecutionsForAdapterAsync`), so `Queued` is excluded by
construction and no clause was needed — contrary to the plan, the "reaper half" was already done.

Two do not: `GetTerminalExecutionsOlderThanAsync` (`Status != Running`) and
`DeleteOldExecutionsAsync` (**no status filter at all**). Both key off `StartedAt < cutoff`, both
**erase** what they match, and a queued execution has no `StartedAt`. On paper that is silent work
loss. It is not, and the reason is not in this repo: **MongoDB's range operators are type-bracketed**
— `$lt` against a date never matches a null or missing value, even though null sorts below every
date in BSON *ordering*. Removing either explicit `Queued` exclusion leaves
`FailStuckAndOrphanedExecutionsTests.AQueuedExecutionIsInvisibleToEveryReaperAndSweep` green. They
are kept so the invisibility is a property of the query rather than of the storage engine.

### 🔴 The tick is a floor, not the cadence (leasing design §9.6)

`LeaseSchedulerIntervalSeconds` used to be both, and that made it the throughput ceiling. Measured on
the second local walk: grants came **5.11–5.14 s apart for runs of 0.43–1.36 s**, so the member was
idle **75–92 %** of the time and no member could exceed roughly **twelve executions a minute** however
small the work was. For `Interactive` it meant a Studio Execute paid up to a full interval before
anything started.

`ILeaseSchedulerWakeSignal` lets the two events that can make a grant possible pull the next round
forward. **Two, not one** — and the second is the one that was missing from the original write-up:

| Event | Where | What it changes |
|---|---|---|
| a lease is **released** | `LeaseService.ReleaseLeaseAsync` | a member became available for queued work |
| work is **enqueued** | `TriggerManagementService.StartExecutePipelineAsync` | work arrived while a member may be idle *already* |

The release side alone looks sufficient and is not: with two borrowers enqueuing continuously
somebody is always working, which is exactly why the round-robin walk never exposed the enqueue case.
A Studio Execute against an idle pool is the case that matters, and no release happens there.

Three properties, each load-bearing:

- **Coalescing.** The semaphore has capacity **one**, so at most one round is ever pending. Six
  executions enqueued in 320 ms — the shape the fairness walk produced — must cost one extra round,
  not six: a round reads the queue of *every* borrower of the pool.
- **A minimum gap** (`LeaseSchedulerWakeMinIntervalMilliseconds`, default 250, 0 disables) between
  requested rounds, measured from the **start** of the previous round so a long round is never
  followed by a wait it has already served. Without it a burst, or a pool whose members release
  continuously, runs rounds back to back and makes the scheduler the most expensive thing in the
  controller — the outcome the interval was chosen to avoid.
- **The periodic tick stays.** It covers what no signal reaches: TTL expiry, topology changes, and the
  multi-pod case where the event arrived at a *different* controller instance. Same reasoning as
  `SweepStalePools` — several controllers act independently and each sees a different subset, so
  nothing may depend on having observed an event.

🔴 **A `Drained` release wakes nobody — unless it handed work back.** A draining member takes no
further work (concept §6 drains and restarts a member whose post-lease cleanliness is unproven), so a
round on its account would read every borrower's queue and grant nothing. Since the member-drain change a Drained
release that never ran its work item re-queues it, and then a round IS requested: another member may
be idle right now.

### 🔴 A `Drained` release marks the member draining and re-queues refused work

Reproduced on a test environment (2026-10-07): a lease participant failed to leave, the member drained itself
and released with `Reason=Drained` — and the controller only logged it. The registry kept the member
available, the next round granted it the next lease, the member refused it in < 400 ms, and
`ApplyLeaseOutcomeAsync` set the borrower's execution to **FAILED**. Every following execution of every
borrower failed until the pod was deleted. `LeaseReleaseReasonDto.Drained` had promised the opposite all
along ("the controller re-queues the work and does not hand this member another lease").

`LeaseService.ReleaseLeaseAsync` now does both halves:

- **Registry.** `MarkDraining` is called **before** the release frees the lease, so there is no instant
  in which the member is idle and not draining — and also for a **stale** Drained release, because the
  member's drain flag never resets. `MarkDraining` returns whether it was a transition, so the WARN and
  `octo.lease.member_drained.count{octo.lease.drain_reason="member_reported"}` fire once per member.
- **Outcome.** `Drained` + `Success=false` + **no `WorkDurationMs`** = the work item never ran (refused
  up front, or the unwind after a failed enter drained the member). It is returned to the queue through
  the interrupt + re-queue path with **`keepQueuePosition: true`**: the retry is enqueued with the
  original `QueuedAt` (`InterruptedLeasedExecution.QueuedAt`), so a refusal neither sends the item to
  the back nor changes its class order. Counted on `octo.lease.requeued.count` as `member_refused`,
  never on `interrupted.count` (the lease did end with a release). A Drained release **with** a work
  span ran the pipeline and applies its own outcome — re-queuing it would run work twice.
- **Loop guard.** `MaxRefusedLeaseRequeues` (3): a chain refused more often is failed loudly. The count
  rides in memory from attempt to retry (`_refusalCounts`, pruned after an hour); a controller restart
  forgets it, which costs at most another three refusals.
- 🔴 A `Failed` release without a work span is deliberately **not** treated as a refusal: that is a
  lease that could not be entered (borrower credential, CK model), which fails the same way on every
  member and must surface instead of looping.

The member half (comm-sdk): a failed leave keeps the work item's own outcome, a draining member never
registers again, is not ready, and **exits** once idle so the pool restarts it
(`AdapterPoolMemberDrainExitService`). Tests: `Services/LeaseServiceTests/DrainedReleaseTests`, the two
member-drain cases in `LeasingMetricsTests`, `MarkDraining_ReportsOnlyTheTransition`, integration
`AdapterPoolQueueTests.ARefusedLease_RequeuedWithItsOriginalQueuedAt_KeepsItsPlaceInTheQueue`.

🔴 **The request is a pending permit, not an event.** A release landing while a round is still running
has nobody waiting at that instant; dropping it would leave the member it just freed idle until the
tick — precisely the latency this removes.

The gap is measured with `Stopwatch`, not the wall clock and not an injected `TimeProvider`: it is a
duration, so it must be monotonic, and `TimeProvider` is registered nowhere in this service — taking
it as a dependency would be a startup failure the compiler cannot see.

Tests: `Services/LeaseSchedulerWakeSignalTests` (coalescing, the pending permit, re-arming, disposal),
the wake cases in `Services/LeaseServiceTests/ReleaseAndDisconnectTests` (clean, failed, drained,
unknown lease) and in `Services/TriggerManagementServiceTests/LeasedAdapterEnqueueTests` (leased
enqueues wake, a manual adapter does not).

### Scale-up: the window is derived, not invented

`LeaseScaleUpAveragingWindowSeconds` defaults to **0** = "derive per pool from that pool's own
`ScaleUpQueueWaitSeconds`". Q14 defers the window to measurement, so a hard-coded constant would be
the one thing the decision rules out; the derived value is the author's own declaration of how long
a work item may wait. The window applies to the **depth** signal only — the wait signal is already
time-integrated. Samples are retained for **twice** the window: pruning at exactly the window drops
the sample that proves the history is long enough, and the signal then never fires.

### Scale-down: only while the WHOLE pool is idle

`IdleTimeoutMinutes` was dead configuration for a pool. Concept §4a and the CK model comment both said
it drains a member above `MinReplicas`; nothing read it — the idle watchdog skips pools on purpose
(increment 5) and nothing took its place, so a pool only ever grew. Measured on the local kind cluster:
a queue-driven scale-up to two members on 2026-09-23 was still two members days later with an empty
queue. `LeaseSchedulerService.TryShrinkIdlePoolAsync` now runs in the per-pool round, in the
`depth == 0` branch, and every part of its condition is required:

- queue depth **0** across every borrower, and **no** member holds a lease;
- nothing granted for this pool for longer than `IdleTimeoutMinutes`;
- member count **greater than** `MinReplicas`.

⇒ `ILeaseService.DrainMemberAsync` on one member, then `ScaleAdapterPoolAsync(members.Count - 1)`. One
per round; `WorkloadLifecycleService.ClampToAdapterPoolRange` still bounds the request, but the request
is never below the floor in the first place — a log line that disagrees with what the pool did is worse
than no log line.

🔴 **Why the whole pool, and not the member being given up.** Scaling a Deployment down lets the
ReplicaSet controller choose which pod dies, and this service cannot steer it: the controller has no
cluster access, every change travels to the operator as a declarative scale. A per-member rule would
nominate member A and watch Kubernetes kill member B, mid-execution for some borrower. With nothing
running the choice cannot hurt; the drain then only stops the nominated member accepting work in the
seconds before the scale lands. Steering the victim
(`controller.kubernetes.io/pod-deletion-cost`) needs a new operator verb across three repos and is
deliberately **out of scope** — recorded as a follow-up in concept §4a.

Four traps, each with its comment in place:

- **The "last lease" clock is in memory** (`ConcurrentDictionary<AdapterPoolKey, DateTime>`), advanced
  on a successful grant *and* by any round that sees a member holding a lease — the second covers the
  hand-driven `POST …/lease` and a grant made by another controller pod. 🔴 It is **seeded when the pool
  is first seen in a round**, so a pool that has never been leased since process start counts as idle
  *since then*, not since forever: otherwise the first round after a controller restart drains a pool
  that the pod it replaced scaled up seconds earlier. The price is that a restart delays the first
  shrink by up to `IdleTimeoutMinutes`, and erring that way round is the point.
- **The member count does not drop when the scale is requested** — the pod needs seconds to
  disconnect. `_lastRequestedReplicas` holds the pool off while the observed count has not caught up;
  self-limiting, no timer. Without it the next round issues a second scale on a stale count. Written
  before the scale request and kept whatever it does: a permanently failing scale then freezes the
  shrink for that pool rather than draining a member every five seconds. The scale-**up** path writes
  the same memo, or a pool that grew after a shrink would be held for the life of the process.
- ⚠️ **The member view is per controller instance**, as everywhere else in this file. The estate is
  single-replica in steady state and a rolling upgrade is the overlap window — but it is the
  "nothing is running" gate that makes a partial view harmless, not the topology: under-counting can
  only ask for a *smaller* pool, the floor clamp bounds that, and the empty-queue half is read from the
  borrowers' databases and is not per-instance at all.
- **A pool with no borrowers is invisible** — the topology is built from the borrowers' declarations,
  so such a pool never shrinks. Named as a known limitation in concept §4a; not fixed here.

**Logging:** the evaluation logs **nothing** per round — same reasoning as `ObserveRound`, twelve lines
a minute per pool is how a log stops being read. An actual shrink is one INFO line plus a tenant event,
because it changes what the lender pays for. The drain is counted as
`LeaseDrainReason.PoolIdle` → `pool_idle`, a label of its own (increment 9).

The window is the pool's `IdleTimeoutMinutes`; `LeaseIdleShrinkWindowSeconds` (default 0 = "use the
pool's value") overrides every pool at once, exactly as `LeaseScaleUpAveragingWindowSeconds` does for
the depth window — and is what makes a shrink observable in a test at all.

Tests: `Services/LeaseSchedulerServiceTests/IdlePoolShrinkTests` (every part of the condition failing
on its own, the happy path, one-per-round, the hold-off and its release, first-sight seeding, the
silent evaluation with its positive control), the drain label in `Services/AdapterLeasingMetricsTests`
and `SchedulerMetricsTests`, and `Repository/AdapterPoolIdleShrinkInputsTests` in the integration suite
— 🔴 that last one exists because `IdleTimeoutMinutes` is **inherited from `DeployableWorkload`** and had
never been read for a pool: it proves against real MongoDB that the attribute survives the round trip
on an `RtAdapterPool` and that a pool which never declared it reads back as the model's **30**, not
`0`. A zero would collapse onto the one-minute floor and make every seeded pool in the estate give up a
member a minute after its last lease — and that is not a reading a mock can be wrong about visibly.
