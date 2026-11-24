---
description: Per-tenant observability switch and the OTel metrics for workload states, including the alerting contract
applies_to: "**/Services/Workload*Metrics.cs, **/Services/WorkloadObservabilityOptIn.cs, **/BackgroundServices/WorkloadStateMetrics*.cs"
---

## The per-tenant observability switch

🔴 **One flag decides whether a tenant's workload and pipeline observability is published at all.**
That flag is `System/TenantModeConfiguration.PublishWorkloadObservability` (`System-2.3.0`, optional
Boolean, default `false`). A tenant that has not opted in produces **no** series from any of the
families below — the emitters skip it **silently**, which is a deliberate contract: that is the state
of almost every tenant, and a log line per tenant per interval is how a log becomes unreadable.

| Metric family | Emitter | How the flag reaches it |
|---|---|---|
| `octo.workload.deployment_state`, `.communication_state`, `.configuration_state`, `.sweep.age`, `.sweep.entities` | `Services/WorkloadStateMetrics` | The sweep does not read a non-opted-in tenant's entities at all |
| `octo.pipeline.execution.count` | `Services/PipelineExecutionMetrics` | Gated at the recording site (`RecordExecutionOutcome(s)`) |
| `octo.pipeline.execution.age`, `.failures`, `.successes` | `Services/PipelineExecutionMetrics` | Gated inside the gauge collection callbacks |
| `octo.workload.wake.count`, `.wake.duration`, `.hibernation.count` | `Services/WorkloadLifecycleMetrics` | Gated at the recording sites |
| `octo.workload.hibernated`, `.offline_unexpected` | `Services/WorkloadLifecycleMetrics` | Gated inside the gauge collection callbacks |

Until 2026-09-30 only the first row honoured the flag. The other two families predate it
(`WorkloadLifecycleMetrics`, `PipelineExecutionMetrics`) and were unconditional —
The test environment carried **175 pipeline series and 20 lifecycle series while no tenant had opted in**. Two
generations of gating means a tenant that opted out still pays for most of it, so there is now exactly
one switch.

### One reader, one cached verdict

`ICommunicationRepository.IsWorkloadObservabilityEnabledAsync` is the **only** code in the process
that reads the attribute, and `WorkloadStateMetricsBackgroundService` is its **only** caller — once per
enabled tenant per sweep (`WorkloadStateMetricsIntervalMinutes`, default 5 min). It pushes the verdict
into `Services/WorkloadObservabilityOptIn`, a static registry the emitters read **synchronously**.

🔴 **Neither of the two added emitters can `await` anything at the point it needs the answer**, which
is the whole reason for the registry rather than a repository call at the gate:

- five of the instruments are `ObservableGauge` callbacks — synchronous by contract, run on the
  exporter's collection thread, once per scrape per series. A Mongo round trip in there stalls the
  export.
- `octo.pipeline.execution.count` is recorded on the completion path (batches of a hundred
  executions), the wake/hibernation instruments on lifecycle paths a request waits behind. A tenant
  configuration read per execution is a query per execution.

So the shape is the one `LifecycleConfigurationService` already uses for the scale-to-zero gate — a
per-tenant cache with a TTL — with one difference: the refresh is **pushed** by the sweep that already
reads the flag instead of pulled lazily on a miss, because a lazy pull needs an `await` in exactly the
two places that cannot have one. The opt-in refresh runs **before** the sweep's startup grace: the
grace exists so a fresh pod does not publish a burst of `Offline` severities, an argument about the
state sweep alone, while the pipeline and lifecycle instruments are event-driven and every outcome in
the grace window would otherwise be dropped for an opted-in tenant.

**TTL = 3 × the sweep interval** (`WorkloadStateMetricsBackgroundService.OptInValidityFor`). A healthy
sweep refreshes every verdict every interval, so it never expires in normal operation; it only bites
when the single reader has stopped. At that point the process can no longer confirm whether a tenant is
opted in, and publishing on an unconfirmable verdict is the same mistake as treating an unreadable
opt-in as an opt-in. Unknown, opted out, or expired all read as **not opted in** — the gate fails
closed. The dead sweep itself stays visible through `octo.workload.sweep.age`.

**An unreadable opt-in never fails the caller.** The repository answers `false` for an absent
attribute, an absent configuration entity and a failed read alike, logging at Debug; the refresh loop
additionally catches per tenant, so one broken tenant configuration cannot cost every other tenant its
observability.

### Opting out stops series, it does not freeze them

This is why the gauge gates sit in the **collection callbacks** and not at the `Observe…` call sites
that feed the state maps:

- The series **stop at the next scrape** instead of staying on the wire at their last value until the
  pod restarts — a frozen `execution.age` is indistinguishable from a stalled pipeline.
- The state maps keep being maintained for every tenant, so a tenant that opts **in** reports its true
  state at the next scrape instead of nothing until its next transition (lifecycle) or the next
  statistics sweep (pipelines).
- For pipelines it also keeps the two concerns apart: the statistics sweep must run for every enabled
  tenant regardless — it maintains the persisted `RtPipelineStatistics`, which is not observability —
  so only its publication is gated, never the work.

A counter has no collection callback (recording *is* exporting), so those are gated where they are
recorded. The cost: a wake, hibernation or execution outcome occurring before the first opt-in refresh
of a freshly started pod is not counted. Counters restart at zero on a restart anyway, so a rate loses
at most that window.

On top of the gates, a tenant that falls out of the opted-in set is **forgotten** in all three
families (`WorkloadStateMetrics.ForgetTenant`, `PipelineExecutionMetrics.ForgetTenant`,
`WorkloadLifecycleMetrics.ForgetTenant`) by `RefreshOptInsAsync`, the only place that sees the
transition — switched off, deleted, or opted back out. Without it the entries would sit in the maps for
the lifetime of the process and a tenant that opted back in weeks later would briefly report the state
it had when it left.

Tests: `Services/PipelineExecutionMetricsTests` and `Services/WorkloadLifecycleMetricsTests` (a tenant
that did not opt in publishes nothing at all; opting out stops the gauges it had published; an expired
verdict reads as opted out; `ForgetTenant` drops a tenant's whole state; for lifecycle also that a
tenant opting in later reports the state it already had),
`Services/PipelineExecutionServiceTests/ExecutionMetricsWiringTests` (the fold sweep still does its
persistence work for a non-opted-in tenant but publishes nothing) and
`BackgroundServices/WorkloadStateMetricsBackgroundServiceTests` (the sweep's verdict opens the gate for
the other two families, an opt-out forgets their state as well, and an opt-in read that throws leaves
the tenant opted out without costing the next tenant in the list its observability).


## OTel metrics for workload states (item 3)

`GetAdapters` and `GetPools` have always returned three states — `DeploymentState`,
`CommunicationState`, `ConfigurationState` — and none of them was ever spoken as a metric, so an
adapter stuck at a deployment error, a pool that quietly fell offline, or the **configuration error**
were visible in Studio and nowhere a check rule could reach.
`WorkloadStateMetrics` + `WorkloadStateMetricsBackgroundService` publish them.

🔴 **`WorkloadLifecycleMetrics` keeps its own class and its own instruments.** Its two gauges
(`octo.workload.hibernated`, `octo.workload.offline_unexpected`) are live — verified on a test environment on
2026-09-30, 20 series each, all zero — and their names and meanings are load-bearing. The state
instruments live in a **separate** static class on the **same meter**, because the two families differ
in every dimension but one: the workload lifecycle metrics are event-driven, written at the moment of a transition, and covers
workloads only; this one is sweep-driven, derived from persisted state, and covers pools as well.

The dimension they no longer differ in is the **opt-in**. When this section was first written the
lifecycle family was ungated and the gate lived only here, on the argument that folding it into a class
holding two live series is how one of them gets switched off by accident. That argument lost: two
generations of gating means a tenant that opted out still pays for most of its observability. Since
2026-09-30 all three families answer to the one flag — see "The per-tenant observability switch" above for where each gate sits and why the verdict is a cached synchronous flag rather than
a repository read.

(Historical note worth keeping: those two series were missing for months and three investigations
each landed on a different wrong cause — a stale build, no lifecycle transitions, no on-demand
workloads. The real cause: **service** metrics had no OTLP exporter and never left the
process. Since octo-common-services `db5dd04` they flow. There is no instrumentation problem left —
only instruments that do not exist yet, which is what this section adds.)

### The metric contract

| Instrument | Kind | Applies to | Meaning |
|---|---|---|---|
| `octo.workload.deployment_state` | observable gauge `{severity}` | adapter, application, pool | Severity of `DeploymentState` |
| `octo.workload.communication_state` | observable gauge `{severity}` | adapter, pool | Severity of `CommunicationState` |
| `octo.workload.configuration_state` | observable gauge `{severity}` | adapter, pool | Severity of `ConfigurationState` |
| `octo.workload.sweep.age` | observable gauge `s` | per tenant | Seconds since the last **completed** sweep |
| `octo.workload.sweep.entities` | observable gauge `{entity}` | per tenant | Entities the last completed sweep reported |

Tags on the three state gauges: `octo.tenant.id`, `octo.workload.rt_id`, `octo.workload.name`,
`octo.workload.kind` (`adapter` / `application` / `pool`), `octo.workload.state` (the state's own
name, lower-cased). The sweep gauges carry `octo.tenant.id` only.

🔴 **An Application has no communication or configuration state.** The CK model puts those two
attributes on `Adapter` and on `Pool`, **not** on the shared `DeployableWorkload` base — an
Application neither registers over SignalR nor receives a pipeline configuration. It therefore
publishes `octo.workload.deployment_state` and nothing else; a fabricated zero would make
"configured" mean two different things. A **pool** is not a workload in the operator's model either
(it is the layer above), but it carries all three states and the same opt-in covers it, so it shares
the family and is told apart by `octo.workload.kind` rather than by a parallel `octo.pool.*`
contract nobody would remember to alert on.

🔴 **`ConfigurationState` has four values, not two** — `Unconfigured` / `Pending` / `Configured` /
**`Error`**. `Error` is the configuration error state.

### The value is the severity

`0` = healthy, `1` = warning, `2` = critical (`WorkloadStateMetrics.Healthy` / `Warning` /
`Critical`, pinned by a test because the check rules are thresholds on them).

A rule written against the raw enum ordinals could not work: `DeploymentState=4` is **Disabled**,
which is healthy, while `=3` is an outage — so the rule would have to encode the enum and would
silently mean something else the day a value is inserted. Same shape as the existing
`octo.ck.library.state` (value = severity, `octo.ck.state` = name), so the check rules stay one
family.

| Gauge | `0` healthy | `1` warning | `2` critical |
|---|---|---|---|
| `deployment_state` | `Undeployed`, `Deployed`, `Disabled` | `Pending` | **`Error`** |
| `communication_state` | `Online`; **anything** while the entity is not expected to run or is intentionally down | `Unregistered` | **`Offline`** |
| `configuration_state` | `Configured`; incomplete states while intentionally down; anything while not expected to run | `Unconfigured`, `Pending` | **`Error`** |

Two suppressions carry the whole design, and both are judged **where the knowledge lives** — a check
rule cannot join a lifecycle state it never receives, which is the same argument
`octo.workload.offline_unexpected` was built on:

- **Not expected to run** = `DeploymentState` is neither `Deployed` nor `Pending`. Deliberately
  narrower than `ActiveDeployment.IsActive` (which also counts `Error`, because a failed helm release
  may still hold resources): an entity whose deploy errored is not expected to be online, and its
  offline-ness is a consequence of the deployment error, not a second independent fault. So a failed
  deploy fires **once**, on `deployment_state`.
- **Intentionally down** = `LifecycleState` is `Hibernated`, `Draining` or `Waking`. "Offline"
  stopped meaning "broken" the day scale-to-zero shipped, and going offline **resets**
  `ConfigurationState` to `Unconfigured` by design
  (`AdapterService.SetAdapterCommunicationStateOfflineAsync`) — without this every on-demand adapter
  would sit at a permanent warning and the alert would be muted within a week. `ConfigurationState=Error`
  is the one thing hibernation does **not** suppress: the watchdog only drains a workload that is
  otherwise fine, so an error found there is real.

In every suppressed case the series is still **published**, still carrying the true state name in
`octo.workload.state` — suppressed severity, never silence.

### Healthy publishes a zero, and there is a heartbeat

Every entity the sweep sees reports on every pass. For workloads that is the more useful answer than
the fault-only `octo.ck.library.state`: "this adapter exists and is fine" is a fact an operator wants,
and it makes a missing series mean exactly one thing.

That alone is not enough, which is why the two `octo.workload.sweep.*` gauges exist. A tenant with no
entities would otherwise publish nothing, and — the failure that went unnoticed for four hours on
2026-09-29 — a sweep loop that dies inside a living process would look exactly like a healthy fleet.
`sweep.age` is computed against the wall clock in the gauge callback, so it keeps rising when the loop
stops; `sweep.entities` separates "opted in, nothing to report" from "reporting". `CompleteSweep` is
called **only** after a tenant's sweep ran all the way through — a half-failed sweep leaves the
previous values in place and lets the age rise, which is the honest signal.

### Opt-in

Per tenant, via `System/TenantModeConfiguration.PublishWorkloadObservability`
(`System-2.3.0`, optional Boolean, default `false`). Absent entity, absent attribute, `false`, or a
failed read ⇒ the tenant is **skipped silently**: no log line per tenant per sweep, because that is
the state of almost every tenant and a per-sweep info line for each is how a log becomes unreadable.

The same flag governs the pipeline and lifecycle families, and this sweep is the **only reader of it in
the process** — it publishes the verdict into `WorkloadObservabilityOptIn` for the emitters that cannot
await a repository. See "The per-tenant observability switch" above.

🔴 **The flag is read untyped** (`RtEntity.GetAttributeValueOrStandard<bool>("PublishWorkloadObservability")`,
via `ICommunicationRepository.IsWorkloadObservabilityEnabledAsync`). The generated
`Meshmakers.Octo.ConstructionKit.Models.System` package this service compiles against floats to the
published `major.minor` line and lags the engine repository by a train — at the time of writing the
restored package is `System-2.2.2`, which has no such property, so a typed access **would not
compile**, and once it did it would pin the controller's build to that publish forever. The attribute
dictionary comes straight from Mongo and carries the value the moment a tenant's model is migrated.
Same reasoning as `Repository/SystemIdentityCkIds`.

### Where the numbers come from

`WorkloadStateMetricsBackgroundService` — interval loop with a startup grace, structure mirroring
`AdapterOfflineReconciliationBackgroundService`. Per enabled tenant: check the opt-in, then
`GetWorkloadsAsync` (adapters + applications, polymorphic) and `GetPoolsAsync`, then `CompleteSweep`.
The startup grace matters: a fresh pod has no SignalR connections yet, so sweeping immediately would
publish a burst of `Offline` severities that resolve a minute later.

Config: `CommunicationControllerOptions.WorkloadStateMetricsIntervalMinutes` (default 5) is the
cadence, the startup grace, the resolution of every gauge, and the floor under any `for:` duration a
check rule uses.

Derived from persisted state on purpose: the values survive a controller restart instead of starting
from nothing, and they exist for an entity that has not changed state in weeks — exactly the entity
whose stuck `Error` has to be alertable.

### Cardinality

Three series per adapter and per pool, one per application, two per tenant. Bounded by
operator-authored entities (tens per tenant): ~70 series for the test environment's ~20 workloads plus its pools,
a few hundred on prod.

Deliberately **not** one gauge per aspect with the state as the distinguishing tag and a constant
value of 1 — that multiplies by the size of each enum (5 + 3 + 4 = **12** series per adapter instead
of 3) and turns every rule into a label match that has to be revisited whenever an enum grows.
`octo.workload.state` is the one label that changes with the state, so a transition retires the old
series and starts a new one; an instant query may see both inside the staleness window, hence the
`max by (…)` in the rules below. Nothing carries a status message, chart version or pool membership:
unbounded or effectively so, and none of it changes an alerting decision.

Pruning: `CompleteSweep(tenantId, observedRtIds)` drops every entity of that tenant the sweep did not
see, and a tenant that falls out of `GetEnabledTenantIds()` or opts back out is dropped by
`ForgetTenant` (the diff lives in `WorkloadStateMetricsBackgroundService._lastPublishedTenantIds`).
Without either, a deleted adapter's severity would keep reporting and alert about something that no
longer exists — an alert nobody can fix, because there is nothing left to repair.

### Alerting contract (PromQL, Dash0)

Metric names keep their dots as the value of `otel_metric_name`; only label keys become underscored.

```promql
# CRITICAL — a workload or pool failed to deploy
max by (octo_tenant_id, octo_workload_rt_id, octo_workload_name, octo_workload_kind, octo_workload_state)
  ({otel_metric_name="octo.workload.deployment_state", otel_metric_type="gauge"}) >= 2

# CRITICAL — an adapter or pool is offline for a reason nobody chose
max by (octo_tenant_id, octo_workload_rt_id, octo_workload_name, octo_workload_kind, octo_workload_state)
  ({otel_metric_name="octo.workload.communication_state", otel_metric_type="gauge"}) >= 2

# CRITICAL — configuration error (the configuration error state)
max by (octo_tenant_id, octo_workload_rt_id, octo_workload_name, octo_workload_kind, octo_workload_state)
  ({otel_metric_name="octo.workload.configuration_state", otel_metric_type="gauge"}) >= 2

# WARNING — stuck in a transient state; needs a `for:` well above one sweep interval (5 min), e.g. 20m
max by (octo_tenant_id, octo_workload_rt_id, octo_workload_name, octo_workload_kind, octo_workload_state)
  ({otel_metric_name="octo.workload.deployment_state", otel_metric_type="gauge"}) == 1

# LIVENESS — the emitter died while the process lived (the 2026-09-29 failure).
# Threshold = a few sweep intervals; 1800 s covers the default 5-minute cadence with room to spare.
max by (octo_tenant_id) ({otel_metric_name="octo.workload.sweep.age", otel_metric_type="gauge"}) > 1800
```

One thing the rules cannot express: an **entirely absent** metric family means either "the pod is
gone" or "no tenant has opted in". Distinguish them with an `absent()` rule scoped to the tenants
that are known to be opted in, or accept that the first opted-in tenant's `sweep.age` series is the
liveness anchor.

Tests: `Services/WorkloadStateMetricsTests` (through a real `MeterListener`, so the assertions are
about the names, values and tags the exporter actually publishes; unique tenant per test because the
instruments are process-wide and the suite runs concurrently — healthy zeros, every severity mapping,
both suppressions incl. the hibernated-with-config-error exception, the application's missing gauges,
the heartbeat, per-tenant-scoped pruning, and literal pins on the meter name and the severity values)
and `BackgroundServices/WorkloadStateMetricsBackgroundServiceTests` (opt-out publishes nothing and is
not even read, all three kinds in one pass, a tenant dropping out is forgotten, a failing read does
not stamp the heartbeat and does not fail the sweep). Integration:
`Repository/WorkloadObservabilityOptInTests` — the one thing a unit test cannot answer, namely that
the hard-coded attribute name really is the name the value round-trips under in MongoDB, and that an
absent attribute or an absent configuration entity reads as `false` instead of throwing. It also
established empirically that writing the attribute is **not** rejected by the CK validation of a
tenant whose model predates it (written with `SetAttributeRawValue`), which is what makes the untyped
read forward-compatible.
