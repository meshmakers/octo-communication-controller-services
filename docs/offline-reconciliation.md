---
description: Offline reconciliation of adapters and deployment sites, and the offline-write rule
applies_to: **/BackgroundServices/*OfflineReconciliation*.cs
---

### Adapter Offline Reconciliation

The rolling-upgrade race guard in `AdapterHub.OnDisconnectedAsync` skips the Offline
write during the controller pod's own shutdown, on the premise that the adapter
reconnects to the surviving pod (which writes Online). If it never reconnects
(adapter crash / lasting partition that coincides with the controller restart), the
DB entity is stuck at a stale `Online` with no live SignalR connection — config
pushes target a dead connection and Studio shows the adapter green. SignalR's own
client-timeout only covers disconnects while the controller is **healthy**; the
shutdown-window orphan has nothing to reconcile it.

`AdapterOfflineReconciliationBackgroundService` closes the gap: after a startup
grace it periodically asks `AdapterService.ReconcileOrphanedOnlineAdaptersAsync` to
mark every adapter persisted `Online` but without a live connection as `Offline`.

**Why not reuse the config `IAdapterCache` as the liveness signal?** Because
`PreUpdateTenantAsync` flushes that cache on every tenant update while the SignalR
connections stay alive (see `PreUpdateTenantAsync` / `PosUpdateTenantAsync`) — a
connected adapter is therefore legitimately absent from the config cache, so a
cache-miss cannot mean "disconnected". This is the exact trap that made the old
"Offline-if-not-in-cache" loop reset every adapter's state; do not reintroduce it.
There is also no periodic adapter heartbeat, so `CommunicationStateTimestamp`
staleness is not a liveness signal either (a healthy adapter connected for days keeps
its connect-time timestamp).

The fix introduces a dedicated `IAdapterConnectionTracker` — a per-pod registry keyed
by `(tenantId, adapterRtEntityId) → connectionId`, populated in
`SetAdapterCommunicationStateOnlineAsync` and cleared (compare-and-remove) in
`SetAdapterCommunicationStateOfflineAsync`, and **never** touched by a tenant
pre/post-update. So a tracker-miss reliably means "no live SignalR connection on this
pod". The controller runs single-replica in steady state (no SignalR backplane, the
cross-node cache publish is a no-op), so this pod's tracker is the authoritative
liveness view; the only overlap is the brief rolling-upgrade window, which the
startup grace covers (adapters reconnect to the new pod, repopulating its tracker,
before the first sweep runs). `ReconcileOrphanedOnlineAdaptersAsync` re-checks the
tracker immediately before each write, and the repository's `AttributeNewerThanGuard`
on the state timestamp rejects a stale Offline that raced past a concurrent Online.

Config: `CommunicationControllerOptions.AdapterOfflineReconciliationIntervalMinutes`
(default 5) is the sweep cadence and the length of the quiet period after the last adapter
connection (`Services/ReconciliationGrace`, cap 3 intervals from the start). A fixed delay from the
controller start was too short on a test environment (cloud adapters reconnected 72 s after it) and wrote
live adapters Offline.

Tests: `Services/AdapterConnectionTrackerTests` (track / compare-and-remove /
stale-disconnect-keeps-live / reconnect-then-stale-disconnect) and
`Services/AdapterServiceTests/ReconcileOrphanedOnlineAdaptersAsyncTests` (orphaned
Online → Offline, live connection kept, non-Online skipped, missing CkTypeId skipped,
mixed fleet, and the real Online-write-populates-tracker path).

### Offline-write rule: prove it is gone — and heal a wrong Offline

**A pod writes Offline only for connections it can prove are gone, never for connections it merely does
not hold.** "Single replica" is not "single pod": when a node is partitioned (NotReady, no SIGTERM, production
2026-10-19) the Deployment starts a replacement while the old pod keeps running and sweeping. The adapters
reconnect to the replacement and write Online there; the old pod's tracker no longer holds them, so its sweep
(startup grace long over, `IShutdownState` never set) wrote Offline with the newer timestamp, which wins at
the `AttributeNewerThanGuard` — and nothing ever wrote Online again, because Online is only written once
per new connection. A tracker / connection-manager miss means "not mine", not "gone".

- **Proof for the Offline sweep** (`AdapterService.ReconcileOrphanedOnlineAdaptersAsync`,
  `DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync`): besides the miss, the persisted
  `CommunicationStateTimestamp` must be older than this process's start (no timestamp counts as old). An
  Online written before this process started has a writer that is gone, and every adapter that came back to
  this pod has since written a newer one (behaviour, grace and tests unchanged). An Online
  written while this pod was running belongs to another pod and is left alone (Debug log). No owner field in
  the model is needed. Limit: an Online written by a previous pod *after* this process started whose
  adapter then never reconnects anywhere cannot be proven from here and stays Online until it connects again.
- **Self-heal** (`ReassertOnlineForLiveConnectionsAsync` / `ReassertOnlineForClaimedDeploymentSitesAsync`,
  called on **every** tick of the two sweep services, also inside the startup grace): the pod that
  *holds* a live connection (tracker) / claim (operator connection manager) for an entity persisted
  `Offline` writes Online again, re-checks the connection after the write and falls back to Offline if it
  closed meanwhile. Only `Offline` is healed (`Unregistered` is deliberate). Holding the connection is the
  proof, so a late Offline from a healed partition (e.g. the old pod's own disconnect handler) is corrected
  within one sweep interval. The wrong Offline also reset `ConfigurationState` to `Unconfigured` (alert
  "configuration is unconfigured"), so the heal restores it too: the last Configured/Error (+ message) this pod
  saw the adapter report (`_lastReportedConfigurationState`, set in `UpdateConfigurationStateAsync`, forgotten
  when the connection really closes); without one (acked on another pod) it re-sends the deployed
  configuration over the live connection (`AdapterConfigurationUpdatedAsync`) and the adapter's ack sets
  the state. Best effort, never stops the sweep.
- Do not weaken either half: without the proof two pods flap Offline/Online every interval, without the
  heal one wrong write stays until the adapter reconnects for another reason.

Tests: `Services/AdapterServiceTests/TwoControllerPodsTests` (two services/trackers over one shared store:
A's sweep keeps B's Online, a wrong Offline is healed, a connection closed while healing ends Offline,
pre-start Online still goes Offline, heal inside the startup grace), plus the deployment-site cases in
`ReconcileOrphanedOnlineDeploymentSitesAsyncTests`.

The workload-state metric service (`WorkloadStateMetricsBackgroundService`) no longer waits the whole
interval after a start: its first snapshot follows once no adapter has connected and no operator
registered for 30 s (`IsStartupSettled`), at the latest after the interval. Before, the workload-state
alerts were blind for 5 minutes after every controller restart (they resolved because no data arrived).

### Deployment Site Offline Reconciliation

Same problem, same fix as for adapters, for `DeploymentSite` entities. Operator ownership of a site
lives only in memory (`OperatorConnectionManager._poolsByConnection`), the `CommunicationState` in
MongoDB. `OperatorHub.OnDisconnectedAsync` / `UnregisterOperatorAsync` reset what a *connection*
claimed — which does not help when the controller pod restarted (the old pod skips the Offline write
while shutting down, the new pod never saw the claim) and the operator never claims the site again
(CR finalizer removed by hand, edge device unreachable, operator reconnected without the site).
Studio then showed the site `Online` while every workload notification for it was queued with
"No operator currently owns deployment site ...".

`DeploymentSiteOfflineReconciliationBackgroundService` (after the quiet period below, then periodically,
for every enabled tenant) calls `DeploymentSiteService.ReconcileOrphanedOnlineDeploymentSitesAsync`:
every site persisted `Online` for which `IOperatorConnectionManager.GetConnectionsForDeploymentSite`
is empty is written `Offline` (information event on the site). Ownership is re-checked right before
and right after each write (the `AttributeNewerThanGuard` only rejects older timestamps, so a claim that
raced the Offline write makes the sweep restore Online); the sweep stops as soon as the host is shutting
down, also in the middle of a sweep. A site that is claimed later turns `Online` through the normal
`RegisterDeploymentSiteAsync` path, so the sweep never needs to undo anything.

Cloud sites after a controller restart: the central operator reconnects, `RegisterOperatorAsync`
returns the deployed Cloud sites, the operator re-creates/keeps its CRs and re-claims them. **Do not
assume this happens "within seconds"**: on a test environment (2026-10-10) both operators connected 6 minutes after
the controller start (SignalR reconnect backoff) and the claims of 19 tenants took another 70 s. The first
version of this sweep used a fixed 5 minute delay from the start and therefore wrote every Cloud site
(and, through the adapter sweep, every Cloud Mesh Adapter) Offline shortly before its owner claimed it
again — and the persisted-state metric `octo.workload.communication_state` published that as a critical
`OctoMeshWorkloadCommunicationDegraded` for up to one metric sweep interval (the first five tenants of
the sweep order, because the metric sweep trails the reconcile sweep by seconds). Nothing was lost: the
claims' Online writes landed, the metric just follows the persisted state with up to one interval
(+ export and alert evaluation) delay.

**Grace = quiet period, not a fixed delay (`Services/ReconciliationGrace`).** Both sweeps (sites and
adapters) judge only when `now - start >= 3 intervals` (also if nobody ever registered: then the owners are
really gone and the sites end up Offline) **or** when the last operator registration
(`IOperatorConnectionManager.LastOperatorRegisteredAt`) / adapter connection
(`IAdapterConnectionTracker.LastConnectedAt`) is at least one interval old. While owners are still
arriving the sweep keeps waiting; the 3-interval cap stops a flapping owner from blocking it. Rejected:
"unowned for N consecutive sweeps" — adds a per-site counter and a full extra interval of latency for
every truly ownerless site, and still fails whenever the owners need longer than N intervals.

`RegisterOperatorAsync` returns the deployed Cloud sites only to central and legacy operators; an
edge operator (`AutoManageDeploymentSites=false`) gets an empty list — it never acts on them, and the
list carries tenant ids and site rtIds of other tenants.

Tests: `Services/DeploymentSiteServiceTests/ReconcileOrphanedOnlineDeploymentSitesAsyncTests`,
`BackgroundServices/DeploymentSiteOfflineReconciliationTests` (the test-environment timeline: operator registers
372 s after the start, ticks before the quiet period is over do not write),
`BackgroundServices/AdapterOfflineReconciliationTests`, `Services/ReconciliationGraceTests`,
`Hubs/OperatorConnectionManagerTests`, `Hubs/OperatorHubTests/RegisterOperatorAsyncTests`.
