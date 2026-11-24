---
description: "Adapter connection lifecycle: cache invalidation, pool states, shutdown race guard, reconnect races, config push on registration"
applies_to: **/Hubs/AdapterHub*.cs, **/Caches/Adapters/**, **/Services/AdapterConnectionTracker.cs
---

### Adapter CK Cache Invalidation

Adapters cache the tenant's CK model in-process (engine `CkCacheService`, populated
load-once per tenant). After a CK model import (`ImportCk`) or `ClearCache`, that cache
must be invalidated or pipeline nodes (`CreateUpdateInfo@1` / `ApplyChanges@2`) keep
validating against the old model until the adapter process restarts.

Flow:

1. Asset-repo publishes `PreUpdateTenant` / `PosUpdateTenant` (same `CorrelationId`)
   around the tenant update — both for CK imports (`TenantContext.ImportCkModelAsync`)
   and for `ClearCache` (`TenantsController.ClearCache`).
2. `TenantManagementConsumer` pairs the two messages; when the pair completes (i.e. the
   update is finished) it calls `IAdapterService.CkModelChangedAsync(tenantId)` **before**
   the enabled-gated restart relay (`ExecutePreTenantUpdate`/`ExecutePosTenantUpdate`).
   The call is deliberately NOT gated on `IConfigurationService.IsEnabledAsync` and its
   failure never blocks the restart relay (own try/catch + error event).
3. `AdapterService.CkModelChangedAsync` → `AdapterHubCallbacks.CkModelChangedAsync`,
   which **broadcasts** `IAdapterHubCallbacks.CkModelChangedAsync(tenantId)` to every
   adapter connection (`Clients.All`) instead of routing through the adapter cache. The
   cache is wiped during a tenant pre-update, and an adapter whose re-registration failed
   stays connected but uncached — exactly the stale-cache case; adapters filter by
   tenant themselves.
4. Adapter side (`octo-communication-sdk` `AdapterExecutionService`): the callback is a
   no-op for foreign tenants; for the own tenant it calls the new
   `IAdapterService.CkModelChangedAsync` (default interface method, no-op) — the mesh
   adapter overrides it and unloads the CK cache (`MeshAdapterService`), which is lazily
   reloaded on the next pipeline execution. No restart, pipelines stay registered.

Old adapter builds without the hub handler just log an unbound-method warning — the
`PreUpdateTenantAsync` restart relay remains their (gated) fallback.

**Update scope.** `PreUpdateTenant`/`PosUpdateTenant` carry an optional
`TenantUpdateScope` (default `Full`; older publishers deserialize to `Full`). When a completed
pair is `CacheOnly` — e.g. the nightly `AttributeValueAggregatorJob`, which only rewrites
`AutoCompleteValues` on CK attributes — the consumer broadcasts the CK cache flush but
**skips the adapter restart relay** entirely. The fleet-wide midnight restart this relay used
to cause was the trigger window of the tenant-update scope. A mixed pair is treated as `Full` (defensive).
Tests: `CacheOnlyPair_NotifiesCkModelChangedButSkipsRestartRelay`,
`MixedScopePair_FullWins_RunsRestartRelay`.

**Pairing state is `static` on purpose.** `AddBroadcastEventConsumer` registers consumers
**scoped**, so MassTransit creates a new `TenantManagementConsumer` per message — an
instance-level pair dictionary can never match Pre with Pos, and the paired branch
(restart relay + CK flush) silently never runs. This was the second root cause of
the dead restart relay in production. Any consumer that keeps cross-message
state must hold it in a static field or a singleton service, never an instance field.

**The "old message" filter compares against an eagerly captured start.**
`ControllerStartTime` is created as the first statement of `Program` and registered as a
singleton; consumers inject it. It replaced `Constants.StartTime`, a lazily initialised
`static readonly … = DateTime.UtcNow` (beforefieldinit) that only the consumers read — the
first consumed message initialised it and was therefore always dropped as "old". Never
reintroduce a lazily initialised static for this.

Tests: `Consumers/TenantManagementConsumerTests` (flush on pairing, flush despite
disabled tenant, flush failure doesn't block relay, no flush on unpaired Pre,
cross-instance pairing, first pair after start processed) and
`Services/AdapterServiceTests/CkModelChangedAsyncTests`.

### Pool Communication State Transitions

The `CommunicationState` of an `RtPool` (`Unregistered` / `Online` / `Offline`)
is written from two paths and the **write order vs. cache mutation matters**:

| Trigger | Code path | State written | Notes |
|---|---|---|---|
| Operator's SignalR `OnConnectedAsync` | `PoolHub.OnConnectedAsync` → `DeploymentSiteService.SetCommunicationStateOnlineAsync(tenantId, poolName, connectionId)` | `Online` | Adds the pool to `_poolCache` if missing. |
| Operator's `RegisterPoolOperatorAsync` invocation | `PoolHub.RegisterPoolOperatorAsync` → `DeploymentSiteService.RegisterPoolOperatorAsync` → `SetCommunicationStateOnlineAsync(tenantId, poolRtId)` | `Online` | Workloads are deployed via the `WorkloadDeployedAsync` flow on the `/operatorHub`, not via the pool-hub adapter list (which no longer exists). |
| Operator's `UnregisterPoolOperatorAsync` invocation (graceful undeploy) | `PoolHub.UnregisterPoolOperatorAsync` → `DeploymentSiteService.UnregisterPoolOperatorAsync` | `Unregistered` | **Must write the state before `PoolTenant.RemovePool`** — otherwise the `OnDisconnectedAsync` that follows finds nothing in the cache and silently no-ops. |
| Operator's SignalR connection drops without an `UnregisterPoolOperatorAsync` (crash, network) | `OperatorHub.OnDisconnectedAsync` → `DeploymentSiteService.SetCommunicationStateOfflineAsync(tenantId, poolName, disconnectingConnectionId)` | `Offline` | The hub must pass `poolName`, never `Context.ConnectionId`, as the lookup key. The third arg is the **disconnecting** connection id — the service compares it with the cache's current `Pool.ConnectionId` and only writes Offline if they still match. A newer connection that has replaced the disconnecting one (e.g. the operator auto-reconnected after a controller restart and the previous connection's handler is firing late) is treated as a stale disconnect and the call no-ops. Mirrors `AdapterService.SetAdapterCommunicationStateOfflineAsync`'s stale-disconnect guard. |

Tests for this state machine live in
`tests/CommunicationControllerService.Tests/Services/PoolServiceTests/`:
- `UnregisterPoolOperatorAsyncTests` pins the write-before-remove ordering.
- `SetCommunicationStateOfflineAsyncTests` pins (a) the happy path (matching
  connection ids → Offline written), (b) the stale-disconnect guard (cached
  connection id != disconnecting id → no-op, prevents the regression where
  the previous connection's late `OnDisconnectedAsync` overwrote Online state
  written by the new connection), and (c) that the lookup key is a real
  pool name, not a SignalR connection id.

### Rolling-Upgrade Race Guard (`IShutdownState`)

The intra-process stale-disconnect guard above only works **within one
controller pod**. During a Kubernetes rolling upgrade two pods overlap for
a few seconds, and the old pod's `OperatorHub.OnDisconnectedAsync` /
`AdapterHub.OnDisconnectedAsync` handlers fire as the operator and adapters
reconnect to the new pod. Observed sequence on a test environment:

1. New controller pod starts, `RegisterDeploymentSiteAsync` from the reconnected
   operator writes `Online` at T+0.
2. Old controller pod (still mid-shutdown) finally processes its dropped
   SignalR connection; its `OnDisconnectedAsync` calls
   `SetCommunicationStateOfflineAsync` with the OLD connection id.
3. The OLD pod's cache still has that connection id, so the stale-disconnect
   guard does **not** reject — both pods think they're the authority.
4. Old pod writes `Offline` at T+1s. `CommunicationRepository.SetPoolCommunicationStateAsync`
   uses `AttributeNewerThanGuard` to reject writes with timestamps OLDER
   than the current value; T+1s is newer than T+0, so the write succeeds.
5. UI shows the pool Offline until something else triggers an Online write
   (e.g. a second operator claiming the same pool, or an operator restart).

The fix is `IShutdownState` (`Services/IShutdownState.cs`,
`Services/HostApplicationShutdownState.cs`), a tiny singleton wrapper around
`IHostApplicationLifetime.ApplicationStopping.IsCancellationRequested`.
Both `OperatorHub.OnDisconnectedAsync` and `AdapterHub.OnDisconnectedAsync`
check it at the top of the handler and bail out before any
`SetCommunicationStateOfflineAsync` call — the surviving pod is the
authoritative state holder once shutdown starts. Local cache cleanup
(`OperatorConnectionManager.RemoveOperator`) still runs so any straggler
hub invocations on this pod don't see a stale connection.

The graceful `UnregisterPoolOperatorAsync` path is **not** gated by
`IShutdownState`: it represents an explicit user/operator decision to take
the pool offline (e.g. tenant undeploy) and must persist that state
regardless of pod lifecycle.

Tests:
- `Hubs/OperatorHubTests/OnDisconnectedAsyncTests` — `NormalDisconnect_WritesOfflineForEveryOrphanedPool`
  pins the existing happy path; `ShuttingDown_SkipsOfflineWritesButStillRemovesOperator`
  pins the shutdown-guard regression.
- `Hubs/AdapterHubTests/OnDisconnectedAsyncTests/ShuttingDown_SkipsOfflineWriteAndInterruptedMark`
  pins the equivalent guard on the adapter hub. The guard runs **before**
  `GetTenantId()` / `GetAdapterRtEntityId()` so the shutdown path stays valid
  even if the `HttpContext` has already been torn down (and so the test
  doesn't need to mock one).

### Adapter Reconnect Remove Race (completed)

The first fix of the reconcile-on-registration problem (`f43e2bc`) refreshed the cached `ConnectionId` in
`RegisterAdapterInternalAsync` and added stale-connection early-return guards to
`UnregisterAsync` / `SetAdapterCommunicationStateOfflineAsync`. Those guards only
cover the case where the adapter has **already** reconnected *before* the
stale handler runs. They do **not** cover the reconnect that lands *during* the
handler's own `await`s: an adapter's graceful stop calls `UnRegisterAdapterAsync`
on the OLD connection (`AdapterExecutionService.StopAsync`) and then reconnects.
On the controller, `UnregisterAsync` passes the entry guard (the old connection
is still current at that instant), does its DB downgrades (`await`s), and only
then removed the adapter — but by then the new connection may have re-registered
(`AddAdapter` / `UpdateConnectionId`). The old **unconditional** `RemoveAdapter`
then deleted the freshly-registered live adapter, so every later deploy threw
`AdapterServiceException.AdapterNotLoaded` ("no live SignalR connection") until a
pod restart. `SetAdapterCommunicationStateOfflineAsync` had the same TOCTOU
between its guard and the unconditional `RemoveConnectionId`.

Fix: `AdapterTenant` now serializes its connection-lifecycle mutations
(`AddAdapter` / `UpdateConnectionId` / `RemoveAdapter` / `RemoveConnectionId`)
under a single `_connectionLock`, and exposes **atomic compare-and-remove**
primitives `RemoveAdapterIfConnection(rtId, connectionId)` /
`RemoveConnectionIdIfConnection(rtId, connectionId)` that only remove when the
cached `ConnectionId` still equals the passed one. `UnregisterAsync` and
`SetAdapterCommunicationStateOfflineAsync` call these instead of the unconditional
variants, so a stale unregister/disconnect can never clobber a reconnected
connection. (`PublishConfiguration` is a no-op today, so holding the lock across
it is safe.) Note the DB `CommunicationState`/pipeline-`Pending` writes a stale
unregister makes before the conditional remove are a narrow, cosmetic residual —
the adapter stays live and deployable, and `OnConnectedAsync`'s Online write
reconciles the state.

Tests: `Caches/Adapters/AdapterTenantTests` (compare-and-remove happy path +
newer-connection no-op for both primitives) and
`Services/AdapterServiceTests/UnregisterAsyncTests.UnregisterAsync_ReconnectDuringUnregister_DoesNotRemoveFreshConnection`
(the reconnect-mid-await interleaving that the old code failed).

### Reconcile Config Push on Registration

The two fixes above only cover the *connection-cache* races (stale ConnectionId,
TOCTOU remove). They do **not** guarantee the freshly (re-)registered adapter
actually ends up running its deployed pipelines. Recurrence #2 (production /
borrowerdev, 2026-08-06): a coordinated rollout restarted the Communication
Controller (07:59) and then the mesh adapter (08:09) within minutes; the adapter
came up `Online` with **zero pipeline routes registered** — every
`FromHttpRequest` endpoint (`/exportHandover`, `/processDocuments`,
`/uploadDocuments`, …) returned **404** — while the controller still reported the
pipelines as `Deployed`. The accounting-app Handover export surfaced it as
"kein Paket". Only a manual `UndeployWorkload`→`DeployWorkload` recreate restored
the routes.

Root cause: config reaches an adapter **two** ways — (1) the return value of
`RegisterAdapterAsync` (the only delivery on the connect path), and (2) the active
push `AdapterHubCallbacks.AdapterConfigurationUpdatedAsync` (used by the *deploy*
paths). `RegisterAdapterInternalAsync` only ever *returned* the config DTO; it
never actively pushed and never re-drove the deployed pipelines onto the new
connection. So any register whose return value the adapter failed to fully apply
(or that resolved to a stale/empty set during rollout churn) left the adapter
routeless with **nothing to reconcile it** — the controller believed everything
was `Deployed`, so no deploy was ever retried.

Fix: `AdapterService.ReconcileAdapterConfigurationAsync` — after the connection is
(re-)cached in `RegisterAdapterInternalAsync`, the controller actively re-pushes
the adapter's deployed configuration onto the freshly registered connection
(when it has ≥1 pipeline). This is **best-effort** (a push failure is logged, never
fails the registration — the return value still carries the config) and uses the
**raw, non-waiting** `AdapterConfigurationUpdatedAsync` send, *not*
`SendConfigurationAndWaitForResultAsync`, so the register RPC is never blocked for
the 120s deploy-ack window. The adapter's ack
(`UpdateConfigurationStateAsync`) then transitions the pipelines to `Deployed`,
which also clears the long-standing "stuck `Pending` after restart" drift (a
registration delivered purely via the return value was never acked). The re-push
is idempotent — the SDK's `RegisterPipelineCoreAsync` replaces stale registrations
— so the double delivery (return value + push) is safe.

Tests: `Services/AdapterServiceTests/RegisterAdapterTests`
(`RegisterAdapter_RePushesDeployedConfiguration_OnRegistration` pins the re-push
with the deployed pipeline set; `RegisterAdapter_NoPipelines_DoesNotRePush` pins
the empty-config no-op). The `AdapterServiceTestsBase` mock already simulates the
adapter's deploy ack, so the existing register tests exercise the ack path too.


### Registration vs. tenant pre/post-update — the deaf adapter

Reproduced on a test environment: two `ClearCache` 8.6 s apart left a dedicated adapter connected, "registered"
and deaf — metrics endpoint 404, every push lost, pod `1/1`, readiness green, watchdog disarmed.

**Cause (lost update).** `RegisterAdapterInternalAsync` took the tenant's `AdapterTenant`, then read the
adapter configuration from the repository (seconds under load) and wrote the adapter into **that**
instance. `PreUpdateTenantAsync` (`RemoveTenant`) / `PosUpdateTenantAsync` (`AddOrUpdateTenant` → a
**new, empty** instance) ran under `_semaphore`; the registration did not. A pre/post pair inside the
read window left the adapter in an orphaned instance. The pair's `PreUpdateTenant` callback never
reached it (only cached adapters are notified), and nothing could tell the adapter: the registration
had returned success, and `RecordMetricsSample` dropped the samples of an unknown adapter at DEBUG.

**Fix A — the commit is serialised with the cache flush.** A per-tenant lock
(`_tenantCacheLocks`, case-insensitive) is taken by `PreUpdateTenantAsync` around notify-and-flush,
by `PosUpdateTenantAsync` around the re-initialisation, and by the registration around its **commit**
only — never across the repository read, and per tenant so registrations of different tenants never
queue behind each other. The registration remembers which instance it read for; under the lock it
writes only if that instance is still current. Otherwise: tenant flushed and not back yet →
`TenantNotEnabled` (the adapter retries); instance replaced → read again, at most
`RegistrationCacheRaceMaxAttempts` (3) times, then `RegistrationRaceLost`. After the reconcile push,
`EnsureRegistrationStillCached` fails a registration whose adapter is gone from the cache
(`RegistrationLost`) instead of reporting success. An adapter cached under **another** connection is
left alone — that is a second process with the same rtId (see the dual-adapter trap), and failing it
would make both steal the registration from each other forever.

**Fix B — the controller finds an orphan and asks it to register again.** `_registeredConnections`
records every connection with an accepted registration; it is forgotten on unregister, on disconnect,
and for every adapter `PreUpdateTenantAsync` notifies (those restart anyway). The periodic metrics
sample (`RecordMetricsSample(tenantId, connectionId, sample)`, every 10 s by default) of a connection
that is still recorded, whose tenant is cached but whose adapter is not, starts a grace period
(`OrphanedRegistrationGracePeriod`, 30 s); past it the outcome is `MetricsSampleOutcome.RegistrationLost`
— once per connection — and `AdapterHub.ReportAdapterMetricsAsync` sends
`IAdapterHubCallbacks.PreUpdateTenantAsync` to **that** connection plus a Warning event. That callback
is the existing "restart and register again" signal every adapter build handles: no contract change,
and an **old** adapter heals exactly like a new one. 🔴 A `HubException` (the AP-I5 pool-member
pattern) does not work here: the adapter sends metrics with `SendAsync`, so nothing thrown on the
server ever reaches it. A new adapter at an old controller behaves as today.

Pool members are not affected by the race: their registration (`AdapterPoolHub.RegisterPoolMemberAsync`)
is a synchronous in-memory write into the tenant-free pool registry, which no tenant update touches,
and AP-I5 already rejects heartbeats of unregistered members.

Tests: `Services/AdapterServiceTests/RegistrationTenantUpdateRaceTests` (real `AdapterCache`, the
repository read parked on a `TaskCompletionSource` while a pre/post pair runs — deterministic; four of
its seven cases fail against the pre-fix commit), `Services/AdapterServiceTests/OrphanedRegistrationDetectionTests`
(grace, once-only, every regular way out of the cache stays silent), `Hubs/AdapterHubTests/ReportAdapterMetricsAsyncTests`.
