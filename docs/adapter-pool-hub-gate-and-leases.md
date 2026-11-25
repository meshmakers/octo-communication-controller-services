---
description: Adapter pool hub gate, member recovery, leases surviving a restart, contract test and the gate decision counter
applies_to: "**/Hubs/AdapterPool*.cs, **/Services/Lease*.cs"
---

#### The adapter **pool** hub — a third gate, and why it is not one of the other two

`/adapterPoolHub` (`Hubs/AdapterPoolHub`) is the management channel of **adapter pool members**:
processes that belong to **no tenant** and are handed one per lease (shared adapter leasing, see
`docs/concepts/shared-adapter-leasing.md` §4). Mounted next to `/operatorHub`, i.e. with no tenant in
the route — which is the whole reason it exists as a separate hub.

🔴 **Neither existing gate fits, and the reasons are worth knowing before anyone "simplifies" this.**
`/operatorHub`'s gate applies `SystemCommunicationApiPolicy` — system authority over every tenant.
A pool member runs pipelines; it does not administer the estate, and giving the whole pool fleet
system authority to save a file is the wrong trade (concept §4 rejects it explicitly).
`/{tenantId}/adapterHub`'s gate has the right *policy* but binds the connection to a **route** tenant,
and this route deliberately has none. So: the adapter gate's policy
(`TenantCommunicationApiReadWritePolicy`), bound to the tenant **in the token**.

The binding is completed in **two places**, on purpose:

1. `AdapterPoolHubAuthorizationFilter` resolves the principal at connect time and stores its
   `tenant_id` on the connection (`Context.Items`, key `AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey`).
   It fails closed when there is no tenant to store. The filter cannot do more: it runs before any hub
   method, so no pool has been declared yet.
2. `AdapterPoolHub.RegisterPoolMemberAsync` refuses a registration whose declared `PoolTenantId` is a
   different tenant. **No parent/ancestor allowance**, same stance as the other two hub gates — a member registered
   into another tenant's pool would receive leases carrying a *third* tenant's service-account secret.

Staged exactly like the other two: `OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE`, `LogOnly` default,
`Enforce` per environment without a release. The three sections are deliberately distinct so they can
be armed independently — pinned by `AdapterPoolHubWiringTests.TheThreeHubGates_HaveDistinctSections`.

🔴 **Authorization for a *borrower* never comes from this connection.** It arrives on the lease as the
borrower's own `PipelineServiceAccount` credential (concept §8 Q6) and dies with the lease.
That is what keeps a pool member from being a standing cross-tenant credential.

`Services/LeaseService` grants and releases. **Lending is consensual in both directions**: the pool's
`SharingMode` + allow-list say who *may* borrow (via `ITenantLendingScopeResolver`), and the borrowing
adapter's own `LentFrom` edge to its borrower-local `LentAdapterPool` mirror (read as `LentFromReference`) says
from whom it *does*. Either half alone is not
enough — without the borrower half a lender could push executions into any descendant that never asked
for them, under an identity that descendant never intended to hand out. The borrower's credential is
read **after** both checks, so a request naming an adapter it has no business naming never reaches the
code that decrypts a client secret.

🔴 **There is no queue in this increment, deliberately.** A lease request that finds no idle member is
*refused with a reason*, not parked — scheduling (round-robin across tenants, interactive before batch
within a tenant's turn, the per-tenant cap) is increment 7, and half of it built here would be a second
scheduling policy nobody intended to write.

`POST {tenantId}/v1/adapterPool/{adapterPoolRtId}/lease` and `GET …/members`
(`TenantApi/v1/Controllers/AdapterPoolController`) are a **test and diagnostic surface**, not the
production path. They exist so the whole mechanism is exercisable end to end on a real two-tenant pair
one increment before the scheduler. The member list is **per controller instance** — a member's SignalR
connection lives on one pod — which is the same property `IOperatorConnectionManager` has.

🔴 **A client secret now travels over the hub as well as over the deploy path.** The guard
`DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget` is mirrored for the lease path in
`Services/LeaseServiceTests/LeaseSecretLogTargetTests` — same shape (NLog `MemoryTarget`, real path,
assertions on the **rendered** output, neither the value nor an eight-character prefix), plus a second
test that rendering a `LeaseDto` **as an object** does not reveal it. `LeaseDto` overrides `ToString`
for exactly that reason: a record's generated one prints every property, so the first
`logger.LogDebug("… {Lease}", lease)` would leak the secret without anybody noticing.

🔴 **Every test that swaps `NLog.LogManager.Configuration` carries
`[NotInParallel(nameof(NLog.LogManager))]`**. The configuration is process-wide and each of
these probes *replaces* it, so two of them in different classes steal each other's lines — and the "the
run logged something" assertion that keeps such a probe from being vacuous is exactly the one that then
fails, for a reason that has nothing to do with the secret. It happened the first time a sixth class
opened one. The key names the shared resource, not the test, the same way
`[NotInParallel(nameof(MeterListener))]` does for the metrics suites; the four classes that used their
own class name as the key were changed to it. A probe that asserts *absence* of output should filter
the target to its own subject as well — concurrent tests write into it.

#### Member recovery on the registry side

The member half lives in comm-sdk (`AdapterPoolMemberService`, see `octo-communication-sdk/docs/pool-member-recovery.md`); it re-registers whenever it believes the controller holds no registration for it, and — since
the lease-resume change — registers **with** a lease that is still running (`ResumePoolMemberAsync`, below), deferring
only against a controller that does not support that. Three registry properties make that safe:

- **Re-registration on the same connection keeps `ActiveLease` and `IsDraining`.** An invoke that timed
  out on the member but landed here is enough to trigger one; dropping a lease granted in between would
  offer a busy process as free, and the member must refuse the next grant — failing that execution.
- **A registration on a new connection supersedes the same member id's registration on any other
  connection of the same pool** (`RemoveSupersededMembers`). The stale entry would otherwise linger
  until this pod notices the old connection is gone, and it is the claim's first pick (idle, least
  recently seen) — every lease handed to it goes down a dead connection. A superseded entry that held a
  lease goes through `HandleMemberDisconnectedAsync` (same shutdown guard as the disconnect path); the
  later disconnect of the old connection finds nothing. Member ids are unique per process by
  construction (pod name, the chart renders no `MemberId`); a WARN names both connections in case not.
- **A heartbeat on a connection without a registration throws `HubException`** instead of being
  swallowed. It is the one moment a member can learn it is not registered while its connection is up.

Tests: `Hubs/AdapterPoolHubTests/MemberRecoveryTests`.

#### A lease survives a controller restart and a dropped connection

Leases live only in this process's memory (`AdapterPoolConnectionManager`). Two failures followed from
that, both reproduced on a test environment on 2026-10-07:

- **Controller process restart mid-lease (F1).** The new process knew neither member nor lease; the
  member's release on its new connection was dropped **without a log line**; the execution stayed
  `Running` and the stuck reaper failed it 31 minutes later as *"orphaned by adapter restart"* — the
  work had completed two minutes after the restart.
- **Dropped connection, controller alive (double execution).** `HandleMemberDisconnectedAsync`
  interrupted and re-queued at once while the member computed on and reconnected seconds later; the
  retry ran the work a second time and the original's release was dropped.

What changed — the persisted execution (`LeaseGrantedAt`, `LeasedOnMemberId`, `LeasedFrom*`, no
`LeaseReleasedAt`) is the durable half of a lease; no new storage, no CK bump:

| Piece | Where |
|---|---|
| `IAdapterPoolHub.ResumePoolMemberAsync` — the member registers **with** its running lease (`PoolMemberRegistrationDto.ActiveLease`); the result says `ActiveLeaseAdopted` | `Hubs/AdapterPoolHub.RegisterCoreAsync`, `LeaseService.ResumeLeaseAsync` |
| Adoption, in order: the lease the superseded registration held → a lease held back after the disconnect → the persisted execution (`Running`, `LeasedOnMemberId` = member, same pool, no release stamped). Not provable ⇒ the member is still recorded **busy** with a lease that names no execution, so no second lease lands on it and its release touches nothing | `LeaseService.ResumeLeaseAsync` |
| A release this connection does not hold: matched by **lease id** against held-back leases and against the member's previous connection (`FindMemberHoldingLease`), otherwise against the persisted execution named by `LeaseResultDto.ExecutionId`/`TenantId`/`MemberId`. Never applied to an execution that moved on. Logged in every outcome | `LeaseService.ResolveUnheldReleaseAsync` |
| Reconnect grace: a disconnect mid-lease **holds the lease back** for `LeaseMemberReconnectGraceSeconds` (default 90, 0 = old behaviour) instead of re-queuing. Ends by resumption, late release, an idle re-registration of the same member id (re-queued at once), or the grace (`SweepLostLeasesAsync`, first thing in every scheduling round) | `LeaseService.HandleMemberDisconnectedAsync`, `LeaseSchedulerService.RunSchedulingRoundAsync` |
| The reaper names a lost lease as such (`CommunicationRepository.LeaseLostMessage`) instead of "adapter restart" | `CommunicationRepository.FailStuckExecutionsAsync` |

🔴 **Why a separate hub method, not a field on the registration.** A controller that pre-dates it
ignores unknown members, so a lease announced on `RegisterPoolMemberAsync` would register the busy
member as idle and the next grant would be refused (failing that borrower's execution). The new method
makes such a controller answer "unknown hub method", and the member falls back to deferring its
registration — the behaviour before the lease-resume change. `RegisterPoolMemberAsync` therefore never honours
`ActiveLease`.

**Proof, and its limits.** A lease id is a random 128-bit value sent to one member, so the two in-memory
matches are a capability proof. The persisted match has only the member id (pod name) to go on, which is
why it also requires a running execution without a stamped release, and why a late release on a
connection is judged by the hub's staged tenant binding (`connectionTenantId`, refused only under
`Enforce`). Persisting the lease id would make it a capability proof too (CK minor bump) — open
decision, see the F1 report.

⚠️ **Per controller instance.** The held-back leases live in this process. With more than one replica a
member can resume on pod B while pod A's grace expires and re-queues: at-least-once, as before. A
persisted lease record (or an "adopted" marker on the execution) is the multi-replica answer.

Skew: old member + new controller — late releases without `ExecutionId` are matched by lease id only
(held-back or previous connection), otherwise ignored as before, now with a WARN. New member + old
controller — resumption answered as unknown method ⇒ deferral; pending releases are re-sent and ignored
as before.

Tests: `Services/LeaseServiceTests/LeaseSurvivesControllerRestartTests`,
`Hubs/AdapterPoolHubTests/ResumePoolMemberAsyncTests`, `Services/LeaseSchedulerServiceTests/LostLeaseSweepTests`,
integration `FailStuckExecutionsAsync_NamesALostLeaseAsSuch_AndKeepsTheAdapterMessageForTheRest`.

Tests: `Hubs/AdapterPoolHubTests/` (registration + the tenant-binding matrix in both modes, the
`IShutdownState` refusal, lease routing to a member, a second lease never landing on a claimed member,
release, stale release, disconnect mid-lease, draining, heartbeat),
`Hubs/AdapterPoolHubAuthorizationFilterTests.cs` (the matrix for this gate, plus that the
connection tenant is recorded in **both** modes), `Services/LeaseServiceTests/` (grant, the consent
matrix, the credential, TTL, the no-idle-member refusal, a failed push undoing the claim, release and
disconnect) and `Configuration/AdapterPoolHubWiringTests.cs` (mount, filter, section binding, the route
carrying no tenant, and that the **adapter** hub keeps its own gate and its route tenant).

#### 🔴 Controller → client sends are not type-checked — the contract test is

All three hubs are untyped (`IHubContext<THub>`), so `SendAsync(nameof(I…HubCallbacks.X), args…)`
compiles with any argument list, while the clients (octo-sdk `AdapterHubClient`, `AdapterPoolHubClient`,
`OperatorHubClient`) register `On<T1,…,Tn>` from the same interface. A send whose arguments do not bind
is **dropped on the client with no error on either side**. `LeaseService.DrainMemberAsync` sent
`DrainAsync` without its `reason` for the whole life of the leasing feature: no controller drain (idle shrink, lease
TTL) ever reached a member, the member stayed Ready and never exited, and the controller logged
"Told pool member … to drain" (N8, a test environment 2026-10-08).

`Hubs/HubCallbackContractTests` holds every send to the interface: runtime tests drive the real senders
(`AdapterHubCallbacks`, `OperatorConnectionManager` incl. the replay that picks its method name at
runtime; `LeaseService` in `Services/LeaseServiceTests/LeaseHubCallbackContractTests`) and check argument
count and types by reflection (`Helper/HubCallbackContract`); a source sweep counts the arguments of
**every** `SendAsync(nameof(I…HubCallbacks.X), …)` under `src/` and refuses a hub send whose name is not
taken from an interface (justified exceptions are listed in `NotAHubCallbackSend`). Adding a send means
nothing to do if it uses `nameof`; a new callback interface must be added to `CallbackInterfaces`.

#### Decision counter of the three hub gates

All three gates (`/{tenantId}/adapterHub`, `/operatorHub`, `/adapterPoolHub`) count every decision on `octo.communication.hub.authorization.decisions` (counter,
`{decision}`, meter `Meshmakers.Octo.Communication`) — `Hubs/HubAuthorizationMetrics`, registered as
a singleton (`AddHubAuthorizationMetrics()` in `Program.cs`) created through `IMeterFactory`. The zero
baseline for every hub × mode × outcome × reason series (30: adapter 14, operator 6, adapterPoolHub 10) is
written by `HubAuthorizationMetricsBaselineService` (`IHostedService`), **not** by the constructor and never
by code between `builder.Build()` and the host start: the OpenTelemetry `MeterProvider` is built at host start
and an `Add(0)` without a listener is dropped (on 3.5.23 no zero series, and so no `adapterPoolHub`,
ever reached Dash0). The service resolves `MeterProvider` first, then calls `EmitBaseline()`. The baseline
matters because PromQL's `increase()` does not count the first point of a new cumulative series — without it a
single anonymous connection after a restart would read as "no increase". Proof query for a rollout (must return
adapter 14, operator 6, adapterPoolHub 10, all with value 0 on a fresh pod):
`count by (octo_hub_name) (max by (octo_hub_name, octo_hub_authorization_mode, octo_hub_authorization_outcome, octo_hub_authorization_reason) ({otel_metric_name="octo.communication.hub.authorization.decisions"}))`.
Test: `Hubs/HubAuthorizationMetricsBaselineExportTests` (real `MeterProvider` + capturing exporter in the real
startup order; fails if the baseline is emitted before the provider listens). Attributes, all closed sets:

| Attribute | Values |
|---|---|
| `octo.hub.name` | `adapter`, `operator`, `adapterPoolHub` |
| `octo.hub.authorization.mode` | `log_only`, `enforce` |
| `octo.hub.authorization.outcome` | `allowed`, `would_refuse` (LogOnly), `refused` (Enforce) |
| `octo.hub.authorization.reason` | `authorized`, `cross_tenant_client` (allowed via the allow-list), `unauthenticated`, `missing_scope`, `no_route_tenant`, `no_tenant_claim`, `tenant_mismatch` |

It is the go / no-go signal for arming `Enforce` per cluster (`would_refuse` must stay at zero); the
warning log line names the caller and, for the adapter hub, the tenants — it now ends with
`(reason code <reason>)`, the same value as the metric attribute. **No tenant attribute and no
per-tenant opt-in gate**, unlike the workload/pipeline instruments: it is installation security
posture, the operator hub is not tenant-scoped, and the series count stays fixed (a few dozen). The
filters resolve the metrics through `HubAuthorizationMetrics.TryResolve` and `Record` swallows listener
failures, so the metric can cost a measurement, never a connection. Rollout procedure: `docs/runbooks/hub-authorization-enforce-rollout.md`.

Tests: `Hubs/HubAuthorizationMetricsTests` (including the pool hub: allowed, would_refuse, refused, registration tenant mismatch; every reason × outcome through a real `MeterListener`
scoped to the test's own `IMeterFactory`, exactly four attributes, connection unaffected without the
registration, registration pinned at the `Program.cs` source).

## Rules that bind new code

- **Secrets stay out of output.** No client secret or token in a log line, a REST or method result, an event or a `ToString` (`LeaseDto` overrides it). The credential reaches only the workload that uses it: through the lease to the pool member, the Helm secret value and the adapter's pipeline configuration. Tests: `LeaseSecretLogTargetTests`, `RotateServiceAccountSecretMethodRedactionTests`.
- **Hub callbacks use `nameof`.** Every hub send takes its method name from `nameof(I...HubCallbacks.X)`; a new callback interface is added to `CallbackInterfaces` in `HubCallbackContractTests`.
