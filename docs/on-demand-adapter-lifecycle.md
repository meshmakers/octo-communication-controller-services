---
description: "On-demand adapter lifecycle (scale-to-zero): lifecycle metrics and the HTTP activator"
applies_to: **/Services/*Lifecycle*.cs, **/BackgroundServices/WorkloadLifecycleWatchdog*.cs, **/Middleware/**, **/Consumers/LifecycleWakeConsumer.cs
---

## On-Demand Adapter Lifecycle — Scale-to-Zero

Rarely used adapter workloads scale to 0 replicas when idle and are woken automatically on
demand. Design doc: `docs/concepts/on-demand-adapter-lifecycle.md`.

**CK model.** `DeployableWorkload` carries `LifecycleMode`
(AlwaysOn=0 default | OnDemand=1 | Auto=2 reserved, rejected until implemented) and
`IdleTimeoutMinutes` (default 30) as author configuration, plus `LifecycleState`
(Running/Draining/Hibernated/Waking) and `LastActivityAt` as `isRuntimeState` attributes.
The existing state fields are deliberately untouched: `CommunicationState=Offline` stays
factually correct while hibernated, `DeploymentState=Deployed` stays correct (the helm
release still exists) — consumers interpret them *through* `LifecycleState`.

**Activation is runtime configuration, not a deployment switch.** Two gates must both be on:
1. Per-tenant config record `communicationLifecycle` (`CommunicationLifecycleConfiguration
   { ScaleToZeroEnabled }`, default false) in the tenant KV store — same store as the
   enabled flag. Read through `ILifecycleConfigurationService` (30 s TTL cache; a write
   invalidates this pod immediately). REST: `GET/PUT {tenantId}/v1/communication/lifecycle`;
   octo-cli: `GetCommunicationLifecycle` / `SetCommunicationLifecycle -sze true|false`.
   Setting false is the per-tenant emergency stop.
2. Per-workload `LifecycleMode=OnDemand`.

**State machine (owned by `IWorkloadLifecycleService` / `WorkloadLifecycleService`).**
`Running → Draining` (idle watchdog) `→ Hibernated` (operator scale-0 ack via
`OperatorHub.ReportWorkloadScaleStatusAsync`) `→ Waking` (any demand signal) `→ Running`
(readiness = **`ConfigurationState=Configured`**, NOT Online; hooked in
`AdapterService.UpdateConfigurationStateAsync` → `NotifyWorkloadConfiguredAsync`, which also
releases the wake waiters). A failed scale-0 reverts Draining→Running; a failed scale-1
fail-fasts active waiters; a wake that never reaches Configured within
`LifecycleWakeBudgetSeconds` (default 60) reverts to Hibernated and throws
`WorkloadLifecycleServiceException` (deployment stays scaled up for diagnosis). The wake
wait registry is per-pod in-memory and deliberately separate from
`AdapterService._pendingDeployments` (single-TCS last-writer-wins slot).

**Scale mechanics.** `RequestScaleAsync` routes a `ScaleWorkloadDto` through
`IOperatorConnectionManager.NotifyWorkloadScaleAsync` (pool-scoped routing; when no operator
owns the pool the notification is queued under a scale-specific pending key
`{workloadRtId}::scale` so it never supersedes a queued deploy/undeploy — pending-queue rules).
The operator patches `{"spec":{"replicas":N}}` on the Deployments matching
`app.kubernetes.io/instance={release}` (no helm) and acks via
`ReportWorkloadScaleStatusAsync`. Redeploys must not resurrect: `BuildWorkloadDeployedDtoAsync`
sets `WorkloadDeployedDto.Hibernated` for Hibernated/Draining workloads and the operator pins
`--set replicaCount=0`.

**Wake gates.** Demand signals all funnel into
`EnsureWorkloadRunning[ForPipeline]Async` (fast no-op when the tenant gate is off, the
workload is AlwaysOn, or the workload is Running — Running just stamps `LastActivityAt`):
- Execute pipeline: `TriggerManagementService.StartExecutePipelineAsync`, **before** the
  queue send — the execute queue is non-durable/auto-delete, a send to an absent queue is
  silently dropped.
- Config/deploy pushes: `AdapterService.DeployAdapterConfigurationAsync`,
  `DeployPipelineAsync`, `DeployDataFlowAsync` (wake-first, before the cache lookup that
  would throw `AdapterNotLoaded`). 🔴 **Except a `Leased` adapter**:
  `DeployPipelineAsync` reads the workload first and skips the gate for one, because a leased
  adapter has no workload of its own and the pool belongs to another tenant.
- Manual wake API: `POST {tenantId}/v1/adapter/{workloadRtId}/wake` (Studio "wake now",
  app pre-warm).
- Cron co-wake: `UpdateScheduleAsync` registers, for every cron trigger whose pipeline runs
  on an OnDemand adapter, a companion recurring send (same cron, same scheduleGroup) of a
  `LifecycleWakeMessage` to the **durable** queue `PipelineQueueNames.LifecycleWakeQueue`
  (`octo::com-controller::lifecycle-wake`), consumed by `LifecycleWakeConsumer`
  (registered via `AddRoutedEventConsumer` — NOT `AddCommandConsumer`, whose endpoints are
  temporary). The pipeline's own trigger message meanwhile buffers durably on its
  per-pipeline trigger queue. Co-wake schedules are registered independently of the tenant
  gate (the consumer-side gate no-ops when the feature is off) so flipping the flag needs no
  trigger redeploy.
- **Schedule keys are stable.** Every `UpdateScheduleAsync` first removes the whole
  group `pipelineTrigger-{tenant}` (`RemoveRecurringJobsByScheduleGroup`, bot services) and then
  re-registers each tick under a deterministic schedule id (`{trigger}-pipeline|lease|wake-{pipeline}`),
  i.e. one Hangfire job key `{id}-{group}` per tick, however many tenant updates run. Doubled cron
  ticks after a tenant update (a test environment, 2026-10-07) were NOT a second key here: the Hangfire
  scheduler queue was shared by all instances on the broker and stored schedules in another
  instance's Hangfire database (fixed in octo-distributedEventHub). Pin:
  `UpdateScheduleAsync_RepeatedTenantUpdates_RegisterTheLeaseTickUnderOneStableJobKey`.

**Idle watchdog.** `WorkloadLifecycleWatchdogBackgroundService`
(`LifecycleWatchdogIntervalMinutes`, default 5, also the startup grace; structure mirrors
`AdapterOfflineReconciliationBackgroundService`). Per scale-to-zero tenant and OnDemand
Adapter workload in Running+Deployed: idle metric = max(`LastActivityAt`, per-pipeline
`RtPipelineStatistics.LastExecutionAt` — statistics survive the execution fold, raw
`CompletedAt` does not); busy guards = running executions
(`GetRunningExecutionsForAdapterAsync`) and an in-flight wake (`HasActiveWake`). No observed
activity at all counts as idle-since-forever (that fleet is the feature's target). Idle >
`IdleTimeoutMinutes` → Draining + scale-0 request. The watchdog also reconciles stale
`Waking` states (controller restart lost the in-memory waiters): Configured → Running;
stuck > 2× wake budget → Hibernated + error event. Applications are skipped (no pipeline
activity signal yet).

**Hibernation stays out of the audit trail.** A scale-to-zero disconnects the
workload on purpose, so the offline paths ask
`IWorkloadLifecycleService.IsIntentionallyDownAsync` (true for `Draining`/`Hibernated`;
fast-path false when the tenant has scale-to-zero off, never throws) before they report
anything:

- `AdapterService.SetAdapterCommunicationStateOfflineAsync` skips the "is now offline"
  information event.
- `AdapterService.ReconcileOrphanedOnlineAdaptersAsync` still corrects a stale `Online` — a
  workload that looks healthy while hibernated is worse than the log line — but drops the
  "had no live connection" event and logs at Info instead of Warn.
- `PipelineExecutionService.MarkExecutionsAsInterruptedAsync` keeps marking executions, but
  when the adapter is hibernating it reports a **warning** instead of the routine information
  event: the watchdog only drains a workload with no running executions, so finding any here
  means the drain guarantee did not hold, and unlike an ordinary disconnect the platform is
  what cut the work short.

The **state writes are deliberately unchanged** — `CommunicationState=Offline` is factually
true while hibernated, and consumers are supposed to read it through `LifecycleState`. What
changes is only how loudly it is reported. Tests:
`Services/AdapterServiceTests/HibernationAuditSuppressionTests`, the two hibernation cases in
`Services/PipelineExecutionServiceTests/MarkInterruptedTests`.

### Lifecycle metrics

`WorkloadLifecycleMetrics` (static, mirroring `MongoCommandObservability` in the MongoDB
engine) emits four instruments on the meter **`Meshmakers.Octo.Communication`**, which
octo-common-services' `ObservabilityBuilder` registers with the OpenTelemetry MeterProvider —
without that registration the measurements are recorded and dropped.

| Instrument | Kind | Purpose |
|---|---|---|
| `octo.workload.wake.count` | counter | Wakes, tagged `octo.wake.outcome` = `configured` / `timeout` |
| `octo.workload.wake.duration` | histogram (s) | Scale-up request → `ConfigurationState=Configured`; the latency a request pays for a wake |
| `octo.workload.hibernation.count` | counter | Completed hibernations (operator acked scale-0) |
| `octo.workload.hibernated` | observable gauge | 1 while hibernated or draining, 0 while running — averaged over time this is the hibernation ratio |

Tags on every instrument: `octo.tenant.id`, `octo.workload.rt_id`, `octo.workload.name`.
Cardinality is bounded by the number of workloads (dozens per cluster).

🔴 **Gated by the per-tenant opt-in since 2026-09-30.** These four instruments shipped
unconditional; they are not any more. Nothing is published for a tenant without
`System/TenantModeConfiguration.PublishWorkloadObservability` — the counter and the histogram are gated
where they are recorded, the gauges inside their collection callbacks, and a tenant that drops out is
dropped from the state map by `ForgetTenant`. Details and reasoning: "The per-tenant observability
switch".

Three decisions worth keeping:

- **The wake is timed by the caller that started it**, in the `Hibernated`/`Draining` branch of
  `EnsureWorkloadRunningAsync` — not inside `WaitForConfiguredAsync`, which is also entered by
  callers joining an in-flight wake. Timing there would count one wake per waiter and inflate
  both the count and the percentiles.
- **A timed-out wake is counted but never recorded as a duration.** The budget is a cut-off,
  not an observation; mixing it in pulls every percentile towards the timeout.
- **The gauge map is in-memory and republished by the idle watchdog** from each swept
  workload's persisted state (`ObserveState`), so a controller restart heals within one sweep
  instead of reporting every workload as running forever. `UndeployWorkloadAsync` calls
  `Forget` — an undeployed workload left in the map would keep publishing its last value.

Tests: `Services/WorkloadLifecycleMetricsTests` (through a real `MeterListener`, so the
assertions are about the names and tags the exporter actually publishes; each test uses a
unique tenant because the instruments are process-wide and the suite runs concurrently).

### HTTP Activator — wake on request

Routes and authorization live inside the adapter (`HttpRequestService` /
`FromHttpRequest@2`), so nothing in front of it can pre-authorize a call — and with the
workload scaled to zero the ingress can only answer 502/503. `WorkloadActivatorMiddleware`
holds such a request through the wake and then forwards it.

**Wiring is an ingress annotation, not a route.** Adapter Ingresses carry
`nginx.ingress.kubernetes.io/default-backend` naming this service; nginx uses that backend
exactly when the primary Service has no ready endpoint. That single condition is the whole
mechanism — there is no state to flip on hibernate and no window where the two disagree,
and steady-state traffic never passes through the controller. It also catches the
non-lifecycle cases (ImagePullBackOff, crash loop) and answers them with a named 503 rather
than nginx's default page. The companion annotation
`nginx.ingress.kubernetes.io/proxy-read-timeout` must exceed the wake budget; the cluster
default of 60 s would cut the hold short. Both are projected onto every workload by the
operator's cluster-wide `OPERATOR__INGRESS__ANNOTATIONS__n__*`.

Enabled per instance by `CommunicationControllerOptions.ActivatorEnabled` (chart:
`services.communication.activatorEnabled`, default off). The flag alone is inert — the
annotation is what routes traffic.

Request path:

1. `IWorkloadHostnameIndex` resolves the inbound `Host` header to a workload. The index is
   built in the background (`WorkloadHostnameIndexBackgroundService`) over every enabled
   tenant's ingress-enabled workloads, with hostname templates resolved exactly as the deploy
   path resolves them, because the Ingress carries the resolved value. A miss falls through
   untouched — one dictionary lookup, and it is what keeps the controller's own API
   unaffected. The middleware therefore runs **before** authentication and routing: these
   requests belong to the adapter's URL space, so neither this service's auth policies nor
   its route table apply.
2. `EnsureWorkloadRunningAsync` — the same wake gate the execute path uses, so concurrent
   callers share one wake.
3. Forward to `ActivatorWorkloadAddressTemplate` with `{release}` replaced by the workload's
   helm release name. `WorkloadHostnameIndex.ReleaseName` mirrors the operator's
   `K8sNaming.DnsName`; it is duplicated rather than shared (no common library) and pinned by
   `WorkloadHostnameIndexTests.ReleaseName_MatchesTheOperatorsRule`. The default template
   carries no namespace, so the pod's own search domain resolves it in the controller's
   namespace — which is where the operator deploys workloads.
4. A refused connection is retried for `ActivatorForwardRetrySeconds` (default 30). The wake
   completes on `ConfigurationState=Configured` — the adapter has registered over SignalR —
   while the Service endpoint only appears once the readiness probe passes and kube-proxy picks
   it up; until then the connection is refused. Measured on a test environment, under four seconds was not
   enough and produced a 503 for a workload that was already running. Each attempt builds a
   **fresh** `HttpRequestMessage`; reusing one throws `InvalidOperationException` ("the request
   message was already sent"), which is exactly how the first live request failed.
   **Bodies within `MaxBufferedBodyBytes` (32 MB) are buffered once and replayed per attempt**
   (`ByteArrayContent` — a `StreamContent` over the request stream would be disposed with the
   first message). Without that, a body meant exactly one attempt, and the first request after
   hibernation is precisely the one that wakes the workload and lands in the endpoint gap:
   browser uploads failed deterministically while bodyless probes sailed through (field
   report). Oversized or chunked bodies keep the single attempt — silently forwarding a
   truncated body would be worse than a 503.
   A 503 response carrying the `X-Octo-Activator` marker is the **loop guard answering the
   forwarded request** — the response-shaped twin of the refused connection, occurring where
   the forward path re-enters through the ingress (a host-run controller cannot reach
   ClusterIPs) — and gets the same retry treatment instead of being copied to the caller.
5. Failure to wake, or a workload that stays unreachable, answers 503 with `Retry-After` set
   to the wake budget and the `X-Octo-Activator` marker. A forwarded request that comes back
   here is recognised by that header and answered 503 instead of forwarded again.
   **The 503 reflects the request's `Origin`** (plus `Vary: Origin`): the routes and their CORS
   policy live inside the adapter — the thing that is unavailable — so nothing can make the
   real policy call, and without a reflected origin the browser hides status and body entirely
   and surfaces a bare network error ("0 Unknown Error"), which misdirected every diagnosis of
   this path. An error-only response that names workload and tenant the caller addressed leaks
   nothing.
   **The 503 names its cause.** Header `X-Activator-Reason` and the log line carry the
   same class, and the plain-English body matches it; status code, `Retry-After` and the hop marker
   are unchanged. `wake-timeout` = the wake did not finish within the wake budget (or the operator
   failed to scale up) — the only case whose body talks about waking; `no-ready-endpoint` = the
   forwarded request came back (loop guard); `connect-failed` = the forward was refused,
   unreachable or failed unexpectedly. The last two tell the reader to check the pod status (image
   pull, crash loop, readiness) of an always-on workload instead of blaming the wake mechanism.

The middleware makes **no authorization decision** and rewrites nothing but the loop-guard
header: the adapter must see what the client sent.

Tests: `Services/WorkloadHostnameIndexTests`.

**Known limitations / follow-ups:** in-process `FromPolling`/`FromMicrosoftGraphEmail`
triggers never idle and silently stop at 0 replicas — such pipelines must move to cron
`PipelineTrigger`s before their workload goes OnDemand (precondition of the idle gate); the
OnDemandCapable trigger-classification validation is not yet implemented (rejecting
`LifecycleMode=OnDemand` on process-bound workloads, lifecycle design §5); the Studio badges of
are still open, as is its Dash0 alerting review; the
`octo-eda-adapter` chart still needs the `terminationGracePeriodSeconds` fix that
`octo-mesh-adapter` got.

Repository surface: `SetWorkloadLifecycleStateAsync` / `SetWorkloadLastActivityAsync`
(polymorphic load-then-update over Adapter/Application — an `EntityUpdateInfo` needs the
concrete CK type).

Tests: `Services/WorkloadLifecycleServiceTests/*`, `Services/LifecycleConfigurationServiceTests`,
`Hubs/OperatorHubTests/ReportWorkloadScaleStatusAsyncTests`, the scale section in
`Hubs/OperatorConnectionManagerTests`, `BackgroundServices/WorkloadLifecycleWatchdogTests`,
plus gate assertions in the AdapterService / TriggerManagementService test folders.
