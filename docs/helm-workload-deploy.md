---
description: "Helm workload deploy: chart versions, cloud pool deploy tracking, pending-workload reconcile, operator reverse-sync, chart management"
applies_to: **/Services/Workload*.cs, **/Hubs/OperatorHub*.cs, **/Controllers/WorkloadController.cs
---

### Empty ChartVersion ("use latest")

`WorkloadController.UpdateChartVersion` accepts a blank value as the explicit
opt-in for "deploy the newest chart in the configured Helm repository"; non-empty
values must still parse as SemVer. The audit-event message renders `(latest)` for
the empty sentinel so CI/CD inspection stays readable.

`DeploymentSiteService.EnsureWorkloadIsHelmDeployableAsync` /
`BuildWorkloadDeployedDtoAsync` / `IsWorkloadHelmDeployableAsync` only require
`ChartName` and a linked `HelmRepositoryConfiguration` with a non-empty
`RepositoryUrl` — `ChartVersion` is optional. The `WorkloadDeployedDto` sent to
the operator coalesces a null `ChartVersion` to `""`; the operator's `HelmRunner`
then omits the `--version` argument when blank and helm picks the newest tag in
the repo.

This is the rollout contract for `System.Communication.MainLatest` — dev/test
tenants ship with empty `ChartVersion` and the CD pipeline overwrites it on every
main-CI run. The same path works for any workload whose operator wants to track a
channel instead of pinning a specific version.


### Cloud Pool Deploy Tracking (for the PreDeleteTenant cascade)

The `OperatorConnectionManager` keeps an in-memory map
`tenantId → Set<poolName>` of Cloud pools that have been notified to operators
as deployed but not yet undeployed. `NotifyPoolDeployedAsync` adds an entry,
`NotifyPoolUndeployedAsync` removes one.

`DeploymentSiteService.UndeployAllCloudPoolsAsync` reads from this map (via
`GetDeployedPoolsForTenant`) rather than the tenant repository. Reason:
`TenantManagementConsumer.ConsumeAsync(PreDeleteTenant)` fires in parallel
with `PreUpdatePreDeleteTenantConsumer` (in `octo-common-services`), which
unloads the CK-cache for the tenant. A repository-based lookup races with
that unload and throws `CommunicationRepositoryException: Failed to get
pools` — and the operator is never told to clean up, leaving the
`DeploymentSite` CR and broker secret orphaned in the cluster.

Caveat: the map is process-local, so it survives only as long as the
controller pod. If the controller restarts between deploy and tenant delete,
the cascade has nothing to undeploy. Restart-survival is closed by the
operator-side reverse-sync (see "Operator Reverse-Sync" below) — a Cloud
operator reconnecting after either pod's restart calls
`ReportDeployedStateAsync` with the set of pools / workloads it currently
manages, and the controller rebuilds the tracking through
`TrackDeployedPool` / `TrackDeployedWorkload`.

Tests:
- `Hubs/OperatorConnectionManagerTests` — tracking add/remove, tenant
  isolation, bucket cleanup when empty.
- `Services/PoolServiceTests/UndeployAllCloudPoolsAsyncTests` — including a
  regression test that pins `ICommunicationRepository.GetDeploymentSitesAsync` is
  never called from this path.

### Helm Workload Deploy (Phase 2)

The Communication Operator no longer deploys Adapter pods directly — it
runs `helm upgrade --install` for every Adapter and Application managed
by a Cloud pool. CK model (3.15.0) provides the type hierarchy:
`DeployableEntity → DeployableWorkload → Adapter | Application`.

**`IWorkloadEncryptionService`** (`Services/WorkloadEncryptionService.cs`)
provides AES-256-GCM encryption with a single instance-wide
`InstanceSecretKey` configured via
`OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY` (Base64-encoded
32-byte key). Ciphertext is `enc:v1:<base64(nonce||tag||ciphertext)>` —
`Decrypt` is a no-op when no sentinel is present, so a single CK
attribute can carry either a plaintext or an encrypted value. This same
service is also used for `HelmRepositoryConfiguration.Password` and any
future at-rest-encrypted attribute.

**`DeploymentSiteService.DeployPoolAsync`** (Cloud pools) now:
1. Sets `DeploymentState = Deployed` on the pool.
2. Notifies the operator via `IOperatorConnectionManager.NotifyPoolDeployedAsync`.
3. Enumerates the pool's managed workloads through
   `ICommunicationRepository.GetWorkloadsForPoolAsync` (polymorphic load of
   `RtDeployableWorkload` via the `Manages` inbound association).
4. For each workload: resolves its `HelmRepositoryConfiguration` via
   `GetHelmRepositoryForWorkloadAsync`, decrypts secret-flagged
   `ValueOverride.Value` entries and the repository password, then fires
   `NotifyWorkloadDeployedAsync` with the assembled `WorkloadDeployedDto`.
   The workload's `ReceivesClusterSecrets` attribute (inherited from
   `RtDeployableWorkload`, so both Adapters and Applications carry it)
   is copied onto the DTO so the operator can decide whether to inject
   cluster credentials (MongoDB, CrateDB passwords) as secret-flagged
   value overrides at deploy time. The attribute defaults to false; the
   operator's secret-injection path runs only when set explicitly. The
   RabbitMQ broker password is NOT gated by this flag — it is injected
   unconditionally because every workload needs the controller-to-workload
   command bus.

**`DeploymentSiteService.UndeployPoolAsync`** mirrors deploy but in reverse order:
workloads first (so the operator can `helm uninstall` while the pool
namespace still exists), then the pool itself.

**Tenant-delete cascade** (`UndeployAllCloudPoolsAsync`) reads tracked
workloads from `OperatorConnectionManager.GetDeployedWorkloadsForTenant`
and fires `NotifyWorkloadUndeployedAsync` for each, then notifies the
pool undeploys — same in-memory-tracking pattern as for pools, same
rationale (avoids racing with `PreUpdatePreDeleteTenantConsumer`'s
cache unload).

**`OperatorConnectionManager`** carries the workload tracking table:
`tenantId → Map<{poolName}|{workloadName}, WorkloadUndeployedDto>`.
`NotifyWorkloadDeployedAsync` adds, `NotifyWorkloadUndeployedAsync`
removes. Restart-survival is closed by the operator-side reverse-sync
(see "Operator Reverse-Sync" below) — a Cloud operator reconnecting
after either pod's restart calls `ReportDeployedStateAsync`, which
restores both `DeploymentState=Deployed` and the per-tenant tracking
maps so the `PreDeleteTenant` cascade still has the entries it needs.

**Public ingress per workload.** `RtDeployableWorkload` carries two
typed attributes on the base type so Adapter and Application share them:
`IngressEnabled` (bool, default false) and `Hostname` (optional string).
`BuildWorkloadDeployedDtoAsync` copies them onto `WorkloadDeployedDto`
(normalising blank Hostname to null). `EnsureWorkloadIsHelmDeployableAsync`
rejects `IngressEnabled=true` + empty Hostname at Deploy time
(`DeploymentSiteServiceException.WorkloadIngressEnabledButHostnameEmpty`) — the chart
templates build host rules from `publicUri`, and an empty host would fail
k8s admission mid-helm-rollout. The operator's
`WorkloadContextValuesBuilder` then projects `ingress.enabled=true` plus
top-level `publicUri=https://<Hostname>` only when both conditions hold,
layering on top of the cluster-wide `ingress.*` defaults (className,
cert-manager cluster-issuer, TLS) from operator config; those defaults
are not overridable per workload by design. The chart's
`templates/ingress.yaml` builds the tenant-scoped path (`/{tenantId}`) for
adapter charts itself, so the operator only needs to supply hostname +
enabled — no chart change required.

**Workload event routing.** `NotifyWorkloadDeployedAsync` and
`NotifyWorkloadUndeployedAsync` route only to the SignalR connection(s)
that have registered the target `(tenantId, poolName)` via
`RegisterPoolForConnection` — looked up through the
`GetConnectionsForPool` helper. Pool-level events (`DeploymentSiteDeployedAsync`,
`PreUpdateTenantAsync`) still broadcast to every connected operator,
because the central operator with `AutoManagePools=true` decides
whether to create CRs based on the broadcast; edge operators just
ignore it. Workload events used to broadcast too — that meant a
central operator and an edge operator connected to the same controller
both received every workload-deploy event. The central operator would
happily helm-install the chart into its own namespace and report
success, overwriting the edge operator's `success=false` on the runtime
entity, leaving a stray release on the wrong cluster while the Studio
showed `DeploymentState=Deployed`. The routing fix scopes workload
events to the one operator that actually owns the target pool.

**Pending workload notifications (AB#4371).** When `GetConnectionsForPool`
returns no owner, the workload deploy/undeploy notification is **queued**,
not dropped. Dropping it caused the prod-1 incident where an undeploy fired
while the pool was transiently orphaned (the operator's `RegisterDeploymentSiteAsync`
had been rejected during a parallel-startup CkCache race and the operator
never retried — fixed operator-side by its registration retry loop): the
helm release kept running forever while the entity said Undeployed.
`OperatorConnectionManager` keeps a per-`(tenant, poolRtId)` map, last-wins
per workload rtId — an undeploy queued after a deploy of the same workload
supersedes it (and vice versa), so a stale deploy can never resurrect a
release. `OperatorHub.RegisterDeploymentSiteAsync` calls
`FlushPendingWorkloadNotificationsAsync` right after the pool's state write
succeeds; a replay whose send fails is re-queued for the pool's next
registration. The queue is in-memory like the rest of the tracking maps —
a controller restart clears it, and the operator reverse-sync plus the next
user-triggered deploy/undeploy re-establish state. Note the tracking maps
(`_deployedWorkloadsByTenant` etc.) are updated before routing, so the
tenant-delete cascade sees consistent state whether or not the notification
was queued. Tests: `Hubs/OperatorConnectionManagerTests` (pending-queue
section).

**Operator-mode enforcement at registration.** `IOperatorHub.RegisterOperatorAsync(bool? autoManageDeploymentSites)`
carries the calling operator's mode (true = central / Cloud-only,
false = edge / Edge-only, null = legacy build pre-dating the
parameter). `OperatorConnectionManager` stores it per connection via
`SetOperatorMode` / `GetOperatorMode`. `OperatorHub.RegisterDeploymentSiteAsync`
then looks up `RtPool.Environment` and rejects with a typed
`HubException` + writes an Error event via `ICommunicationEventService`
when an edge operator tries to claim a Cloud pool (or vice versa). A
legacy operator without a declared mode is allowed through but logged
+ audit-recorded as an Information event so the trail still shows the
registration. This is the controller-side complement to the
operator's `AutoManagePools` gate (which prevents the edge operator
from materializing CRs in the first place); together they keep
workload-deploy events from routing to the wrong cluster even if a
stale CR materialises on the wrong side. Tests in
`Hubs/OperatorHubTests/RegisterPoolAsyncTests` cover the four matrix
cells (central+Cloud, edge+Edge, central+Edge, edge+Cloud), legacy
fall-through, and the pool-not-found path.

**Edge vs. Cloud at the workload layer.** `DeployPoolAsync` rejects Edge
pools (the central operator does not create the CR / broker secret on the
edge cluster), but `DeployWorkloadAsync` and `UndeployWorkloadAsync` are
Environment-agnostic. The edge operator runs `OperatorHubService.WorkloadDeployedAsync`
through the same `WorkloadReconciler.DeployAsync` helm path as central —
only the pool-level CR/secret side effect is central-only (`AutoManagePools`
on the operator). The Recompute / Undeploy resting-state rules therefore
consider only missing Helm fields when deciding Disabled vs. Undeployed
for a workload, never the parent pool's Environment.

Phase-3 plan in `octo-communication-operator/docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md`
covers the operator-side helm CLI integration.

Tests:
- `Services/WorkloadEncryptionServiceTests` — round-trip, sentinel-skip,
  tamper detection, key-mismatch, missing/invalid key handling.
- `Hubs/OperatorConnectionManagerTests` — workload tracking add/remove,
  tenant isolation, bucket cleanup when empty.
- `Services/PoolServiceTests/DeployPoolAsyncTests` — Cloud/Edge branches,
  workload fan-out, undeploy ordering, scoping workloads to the right pool.
- `Services/PoolServiceTests/UndeployAllCloudPoolsAsyncTests` — extended
  with workload-cascade tests.

### Pending-Workload Reconcile on Pool Registration (AB#4894)

A workload deploy notification is fire-and-forget SignalR; one sent while the operator pod was
being replaced (e.g. an operator CD mid-rollout) lands on the dying connection and is lost —
the entity stays `Pending` forever, and neither the AB#4371 pending queue (the pool HAD a
registered owner at send time) nor the reverse-sync (restores state, never re-dispatches)
covers it. `OperatorHub.RegisterDeploymentSiteAsync` therefore calls
`DeploymentSiteService.ReconcilePendingWorkloadsAsync` after the AB#4371 flush: every workload of the
pool still in `DeploymentState=Pending` gets its deploy re-dispatched through the normal
`DeployWorkloadAsync` path. Best effort — lookup or per-workload failures are logged and never
fail the registration. Tests: `Services/PoolServiceTests/ReconcilePendingWorkloadsAsyncTests`.

**The re-dispatch is idempotent on the operator side (AB#6471).** The controller cannot tell a lost dispatch
from one the operator is still running or has finished with the report lost into a dead connection, so the
re-dispatch of a *genuinely in-flight* deploy is NOT safe by itself: the operator's SignalR queue is serial only
per connection, and a deploy started on the old connection keeps running across the reconnect. On test-2 (CD run
52427, 2026-10-10) that produced a second Helm revision per workload (mrotest v39/v40, energytest v38/v39,
meshtest v104/v105, handelsdemo v52/v53) and `Error` reports for releases Helm had deployed. The fix lives in the
operator (`WorkloadReconciler`: single-flight per release, desired-state fingerprint stamped as helm
`--description`, a reconciliation whose release already runs that state does no helm run and reports success — see
the operator's CLAUDE.md "Idempotent Dispatch After a Controller Restart"). No hub-contract change; an operator
that pre-dates it keeps the old behaviour (duplicate revision, possible `Error`), so edge operators should be rolled
before relying on this on a pool that is re-registered often.

**A reconcile is not a release decision (AB#4955).** The re-dispatch above fires on events that
have nothing to do with releasing software — an operator restart, a blueprint re-apply, a CK-model
update, `EnableCommunication` — because all of them re-register the tenant's pools via
`PreUpdateTenantAsync`. A workload with an empty `ChartVersion` means "newest in the repository",
resolved by the operator at `helm upgrade` time, so those events silently moved six prod-1
accounting workloads from chart 1.0.71 to 1.0.72 with nobody deploying them. Two things close it:

- `DeployWorkloadAsync(tenantId, workloadRtId, isReconciliation)` puts
  `WorkloadDeployedDto.IsReconciliation` on the wire; only `ReconcilePendingWorkloadsAsync` passes
  `true`. The operator then keeps the chart version it already has installed instead of resolving
  the newest one again (see the operator's CLAUDE.md → "Reconciliation Keeps the Installed Chart
  Version"). A user-triggered Deploy stays `false`, so an empty `ChartVersion` keeps meaning
  "newest" — the contract `System.Communication.MainLatest` depends on.
- The re-dispatch of an **unpinned** workload additionally writes a Warning event. The flag is
  additive, so an operator that pre-dates it still resolves anew; the warning is what makes that
  visible until the whole fleet is current. A pinned workload gets an Information event instead —
  it comes back on exactly the version it was running.

### Operator Reverse-Sync

Closes the restart-survival gap on the controller-side in-memory tracking
maps and on `DeploymentState` drift. The flow:

1. Operator pod restarts (or controller pod restarts).
2. Operator reconnects and calls `RegisterOperatorAsync(autoManageDeploymentSites=true)`
   — controller returns the currently-tracked Cloud pools (forward sync).
3. Operator follows up with `ReportDeployedStateAsync(pools)` — for every
   pool / workload it currently has a healthy helm release for, it sends an
   `OperatorDeployedPoolReportDto` carrying `{TenantId, PoolRtId, PoolName,
   WorkloadRtIds[]}`.
4. Controller's `OperatorHub.ReportDeployedStateAsync` rejects the call with
   a typed `HubException` when the operator's declared mode is anything
   other than Cloud (edge or legacy/unknown) and writes an error audit event
   before throwing — defense in depth, prevents an edge cluster from
   reviving central-cluster state.
5. `DeploymentSiteService.RestoreDeployedStateAsync` runs the per-pool work:
   - Loads `RtPool` by rtId; skips when missing.
   - Per-pool `Environment != Cloud` guard skips Edge pools silently —
     a second line of defense against a Cloud operator reporting cross-mode
     entities.
   - Writes `DeploymentState=Deployed` **only when the current state would
     change** (avoids no-op audit events).
   - Calls `OperatorConnectionManager.TrackDeployedPool` + `RegisterPoolForConnection`
     so undeploy fan-out + `OnDisconnected` offline-write keep working on the
     new connection.
   - Same restore-only-when-changed rule for each `WorkloadRtId` in the
     report; uses `SetWorkloadDeploymentStateAsync` (polymorphic over Adapter /
     Application) + `TrackDeployedWorkload`.
6. Empty report is a valid no-op (operator owns nothing yet).

**Why not extend `RegisterOperatorAsync` to take the list?** Keeping the
two contracts separate lets the operator stage the calls — fast register +
synchronous deploy-pool list first, then the larger workload-aware reverse-sync
catches up. It also keeps the SDK contract additive: existing operator builds
that don't know about `ReportDeployedStateAsync` keep working (they just won't
self-heal the tracking, which is the prior behaviour).

Tests:
- `Hubs/OperatorHubTests/ReportDeployedStateAsyncTests` — Cloud delegates,
  edge / legacy reject with HubException + audit, empty list still delegates.
- `Services/PoolServiceTests/RestoreDeployedStateAsyncTests` — Pending →
  Deployed restore, already-Deployed write-skip but track, Edge silently
  skipped, unknown pool silently skipped, workload restore + tracking,
  malformed rtId silently skipped.


## Workload Chart Management (Phase 2 — Epic 3054 / #4052)

`TenantApi/v1/Controllers/WorkloadController.cs` exposes two endpoints used
by the CI/CD rollout flow described in
`docs/concepts/cicd-workload-deployment.md`:

| Method | Path | Purpose |
|---|---|---|
| `GET`   | `{tenantId}/v1/workload?chartName=…` | Returns `WorkloadSummaryDto[]` — every `RtDeployableWorkload` (Adapter or Application) whose `ChartName` matches. Empty array means "this tenant has no exposure to the chart", which CI scripts use as the silent-skip signal. |
| `PATCH` | `{tenantId}/v1/workload/{workloadRtId}/chart-version` body `{ "chartVersion": "1.2.3" }` | Validates the version is a non-empty SemVer (`MAJOR.MINOR.PATCH[-prerelease][+build]`), updates the entity through `ICommunicationRepository.UpdateWorkloadChartVersionAsync`, and writes an `Information` event via `ICommunicationEventService` with the source tag `(source: CI/CD)`. |

Auth: read-only / read-write tenant communication policies (same as
`DeploymentSiteController`).

**Deploy is intentionally a separate call.** `WorkloadController` writes the
chart version into MongoDB but never triggers a Helm rollout. The CI pipeline
calls `POST /pool/workloads/deploy?workloadRtId=…` (existing) after the PATCH
when it wants the operator to pick up the change. Splitting the two lets
operators stage version writes across many tenants before flipping the deploy
switch.

**Repository surface** (`ICommunicationRepository`):
- `GetWorkloadsByChartNameAsync(tenantId, chartName)` — polymorphic
  `RtDeployableWorkload` lookup by `ChartName` field filter.
- `UpdateWorkloadChartVersionAsync(tenantId, workloadRtId, newChartVersion)`
  — load-mutate-replace; returns the previous version so the audit event
  can include the before/after.
