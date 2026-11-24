---
description: "Leasing: AdapterPool deployment, scaling, the idle watchdog exclusion, member sizing and the activator index"
applies_to: **/Services/AdapterPool*.cs, **/Controllers/AdapterPoolController.cs
---

## Leasing: `AdapterPool` deployment, scaling and the watchdog exclusion (AB#4924 increment 5)

An `AdapterPool` is **one workload with a replica range**, not N `Adapter` entities. Nothing new
was invented for it: it rides the existing `DeployableWorkload` path (one workload ↔ one helm
release ↔ one `RtDeployableWorkload`) and the AB#4917 `ScaleWorkloadDto` verb. KEDA stays rejected
for the same reasons as on the on-demand path.

| Piece | Where |
|---|---|
| `WorkloadTypeDto` mapping (one place, four call sites) | `Services/WorkloadWireMapping.cs` |
| Pool `DeploymentState` writer | `CommunicationRepository.SetAdapterPoolDeploymentStateAsync`, `DeploymentSiteService.SetWorkloadDeploymentStateAsync` |
| `replicaCount` + per-member sizing as chart values | `DeploymentSiteService.AppendAdapterPoolMemberOverrides` |
| Scale verb with the `MinReplicas` floor | `DeploymentSiteService.ScaleAdapterPoolAsync` → `WorkloadLifecycleService.ClampToAdapterPoolRange` |
| `POST {tenantId}/v1/deploymentsite/workloads/adapter-pool/scale` | `TenantApi/v1/Controllers/DeploymentSiteController.cs` |
| Idle-watchdog exclusion | `BackgroundServices/WorkloadLifecycleWatchdogBackgroundService.SweepTenantAsync` |
| Activator index scoping | `Services/WorkloadHostnameIndex.RefreshAsync` |

Deploy and undeploy need no new endpoints — `POST pool/workloads/deploy` / `…/undeploy` are typed on
`RtDeployableWorkload` and a pool is one. What they *did* need is a `DeploymentState` writer: the
`switch` in `SetWorkloadDeploymentStateAsync` had arms for `RtAdapter` and `RtApplication` only, so a
pool deployed, stayed at its default state, never showed as deployed in Studio, and then failed
Undeploy with "already not deployed". The same hole existed in
`CommunicationRepository.UpdateWorkloadPolymorphicAsync`, where every lifecycle write against a pool
threw `WorkloadNotFound`.

### 🔴 The idle watchdog must not see a pool

`WorkloadLifecycleWatchdogBackgroundService` decides idleness from the workload's **own** pipelines'
`LastExecutionAt`. A pool has none: every pipeline it runs belongs to a borrower in a different
tenant database, which that query cannot see and a CK association cannot reach. Left alone the
watchdog reads a perfectly busy pool as "no pipelines, no activity, idle since forever" and drains
it to zero, taking every borrower's work with it — and the failure looks exactly like a correctly
working idle timeout. `SweepTenantAsync` therefore skips `RtAdapterPool` explicitly, before the
`LifecycleMode` filter, at every mode.

Two things about that guard are worth knowing before anyone tidies it away:

- For a **Running** pool it is currently redundant with the `workload is not RtAdapter` check
  further down, which exists to skip Applications and catches pools as a side effect. A side effect
  is not a safeguard for a cross-tenant outage, and the day the watchdog is extended to every
  `DeployableWorkload` that side effect disappears.
- For a pool stuck in **`Waking`** it is *not* redundant. `ReconcileWakingAsync` runs **before** the
  `RtAdapter` check and writes `LifecycleState = Hibernated` plus an error event about a wake that
  never existed. That path was reachable. `AdapterPoolStuckInWaking_IsNotRevertedByTheWatchdog` is
  the test that holds it, and removing the guard alone fails that test and no other.

The pool owns its members' lifecycle instead (concept §4a): `MinReplicas` is the floor,
`IdleTimeoutMinutes` and queue pressure decide what happens above it (the queue arrives in
increment 7).

🔴 **Where the pool's own `IdleTimeoutMinutes` lives, since AB#5256:**
`LeaseSchedulerService.TryShrinkIdlePoolAsync`, on the scheduler's per-pool round — **not** here, and
the exclusion above stays exactly as it is. The round already holds the queue depth of every borrower,
the member list and the pool entity at one instant, which is the knowledge the watchdog structurally
cannot have. Until then the attribute was dead configuration and a pool only ever grew: measured on
the local kind cluster, a queue-driven scale-up to two members on 2026-09-23 was still two members
days later with an empty queue. See the increment-7 section below for the condition and its traps.

### `MinReplicas` is enforced twice, on purpose

The watchdog exclusion is a *filter* — it protects against one caller. `RequestScaleAsync` clamps
every pool scale request into `MinReplicas..MaxReplicas` regardless of who asked, because a guard
that only covers the caller you thought of is the guard that is missing during the incident.
`MinReplicas = 0` is a legitimate, deliberate choice (a scale-to-zero pool where every burst pays
one cold start), so the floor is the declared value and never a hardcoded 1.

### What a pool member is allowed to pick up

`ReceivesClusterSecrets` is forced **false** for a pool on the way to the wire, whatever the entity
says. Those are the cluster's *shared* Mongo / CrateDB credentials — one user, every tenant's data
behind it. A pool member executes work for tenants other than the one that owns it, and the lease is
the mechanism that grants it exactly one tenant at a time; a standing credential to all of them makes
that mechanism decorative. The operator refuses the same thing independently in
`WorkloadReconciler.AppendClusterSecrets`: two gates, one on each side of the wire, because either
alone is one edit away from silence.

What a member *does* get is the RabbitMQ command bus, the TLS trust anchor, and its own per-release
secret in the platform namespace — none of which carries tenant authority. Tenant-scoped access
arrives with a lease and leaves with it (increment 6).

### 🔴 Sizing a member: one `ExecuteCSharp@1` run costs ~330 MiB, permanently (AB#5330)

Measured on the local kind cluster on 2026-09-23, read from the pod's own
`/sys/fs/cgroup/memory.current`:

| | member memory |
|---|---|
| freshly started | **124 MiB** |
| four leased runs **without** C# | 126 → 130 → 132 → **134 MiB** (~2 MiB per run) |
| the **first** `ExecuteCSharp@1` run (eight lines) | 131 → **461 MiB** |
| the same script again | +23, then +2 MiB (cached) |
| a **second, different** C# script on that member | **OOMKilled, exit 137** |

The +330 MiB is the Roslyn compiler platform, loaded once per process and never released, so a member
that has compiled C# once sits permanently at 460–490 MiB. Against the 512Mi the sample used to seed
that is about 20 MiB of headroom, and the next lease it accepts is enough. `AdapterPoolDemo` now seeds
**1Gi**, and `PoolMemberMemoryLimit`'s own description carries the number (CK model 4.4.1, a patch — a
description is documentational under `ck-semver-rules.md`, and the bump is what makes it reach a
tenant at all).

🔴 **Two things make this worse on a pool than on a dedicated adapter.** A member is shared across
tenants, so each kill interrupts work belonging to whichever borrower happens to hold the lease — it
happened twice in that session, to two different borrowers. And the controller's account of it is
*"member '…' disconnected while holding this execution's lease"*: it reports a **disconnect**, because
a disconnect is all it can see. The truth is in
`containerStatuses[0].lastState.terminated.reason = OOMKilled`, so that is the first thing to read
when a member "drops", not the controller log.

The leasing machinery does catch it — `InterruptAndRequeueAsync` re-queued the work and the retry
completed on the other member — but the work is **restarted, not resumed**, so a side-effecting
pipeline gets at-least-once semantics out of it.

### The activator index skips pools

`WorkloadHostnameIndex` publishes an address built from the release name alone
(`ActivatorWorkloadAddressTemplate` defaults to `http://{release}`), which resolves in the
**controller's** namespace. A pool runs in the platform namespace, so an entry for one would point
the activator at a Service that is not there — or at a same-named one that is. A pool is reached by
being leased, never by an inbound request, so it is skipped before the hostname map is built. This
is the only place in the controller that still assumes one namespace for everything.
