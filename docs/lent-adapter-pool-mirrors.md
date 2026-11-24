---
description: "Lent adapter pool mirrors: the borrower's view of leasable pools, sync service, re-link and detail view"
applies_to: "**/Services/*PoolMirror*.cs, **/Services/Lent*.cs"
---

## Lent adapter pool mirrors — the borrower's view of what it may lease

Concept: `docs/concepts/lent-adapter-pool-mirror.md`.

A borrower could not see which adapter pools it may use: the `AdapterPool` entity lives in the
**lender's** tenant database, GraphQL is tenant-scoped, and a CK association cannot cross that
boundary. The leasing design worked around it with two plain strings on the borrowing `RtAdapter`
(`LentFromTenantId` / `LentFromAdapterPoolRtId`) — no navigation, no filtering, no display name,
nothing for backup to carry.

**CK 4.0.0 (still unpublished, so this rides along rather than needing a migration).** A new type
`LentAdapterPool` is a borrower-local mirror of one lent pool; the association
`Adapter --LentFrom--> LentAdapterPool` (`ZeroOrOne` outbound, `N` inbound) **replaces** the two
attributes, which are gone. The cross-tenant part is held once per pool in the mirror's `Lender`
record (`LenderTenantId` + `LenderAdapterPoolRtId`, single-valued `valueType: Record`) instead of
twice on every leased adapter. Every attribute on the mirror is `isRuntimeState: true` — the
controller provisions it, nobody authors it.

🔴 **The mirror is NEVER authoritative.** A tenant can edit entities in its own database, so if the
lease decision ever read the mirror a borrower could point it wherever it liked. It does not: every
decision re-reads the **lender's own** `AdapterPool` and calls `MayLendAsync` against that pool's
sharing mode and allow-list. Editing a mirror changes what a borrower SEES, never what it may use.
Nothing in `LeaseService`, `LeaseSchedulerService` or `AdapterService` may read this type to make a
decision — `TheLendingDecision_UsesTheLendersScopeAndNotTheMirrors` is the test that holds it.

🔴 **The association is the truth, which puts the sync service on the critical path.** No mirror, no
lease. That was accepted deliberately (concept §7.1) because 4.0.0 is unpublished and there is no
installed base to protect — but it changes what a sync bug costs: an outage, not a display error.

### Resolving the borrower half

`LentFromReference.FromMirror(...)` is the one projection from a mirror to the
`(LenderTenantId, AdapterPoolRtId)` pair every consumer wants. Two repository reads feed it:
`GetLentAdapterPoolForAdapterAsync` and the batched `GetLentAdapterPoolsForAdaptersAsync`.

🔴 **Never cached.** Re-pointing an adapter at a different pool must take effect at once, and a
stale answer would send a lease request to the wrong lender. (`TenantLendingScopeResolver`'s 30 s
cache holds the lending *scope* — a different question with a different blast radius.) The batched
form exists for `LeaseSchedulerService.GetTopologyAsync`, which walks every leased adapter of every
enabled tenant on the topology refresh cadence: one query per tenant, not one per borrower.

The six resolution points: `AdapterNodeCapabilityService` (now `ResolveAsync` — a leased adapter's
lender is a repository read, so the seam had to become asynchronous, and with it
`IPipelineExecutionClassService.ResolveForAdapterAsync`), `DeploymentSiteService`'s leasing deploy
guard, `LeaseService.CheckBorrowerDeclaration`, `LeaseSchedulerService.GetTopologyAsync`,
`TriggerManagementService`'s enqueue metrics, and `AdapterService`'s leased deploy log line (which
now takes the lender from `capabilities.Source` rather than reading it a second time).

### The sync service

`IAdapterPoolMirrorProvisioningService` / `AdapterPoolMirrorProvisioningService`, following
`ClientMirrorProvisioningService` in octo-identity-services — with one deliberate difference:

🔴 **There is no tracking row.** Client mirroring records a `ClientMirror` in the parent to remember
what it provisioned. Here the mirror's own `Lender` record is the key and the desired set is
recomputed from the tenant tree on every run, so a hand-deleted mirror comes back, a mirror the
controller failed to remove goes on the next pass, and no second piece of state can drift from the
first. The cost is reading every candidate lender's pools per run — bounded by the tenant's
ancestry.

🔴 **A run walks the stored mirrors as well as the tree candidates** (`ResolveLendersToWalkAsync`).
That is what makes **revocation** work: a lender that has dropped out of the candidate set — the
borrower was re-parented, the tree changed — is no longer returned by the walk, so a run driven by
candidates alone would never look at its mirrors again and they would sit there forever naming a
pool the borrower may no longer use.
`AMirrorOfALenderThatIsNoLongerACandidate_IsStillRemoved` fails, and nothing else does, if that
half goes away.

🔴 **An unreadable lender is unknown, never empty** (`CarryForwardMirrorsOf`). Its mirrors are
carried forward untouched. Treating a tenant that is mid-update as "lends nothing" would delete the
borrower's mirrors, and with the association leading that breaks every adapter borrowing from it.
Same stance for an unreadable **borrower**: without the stored set the run cannot decide removals,
and a run that only ever adds turns a narrowed lending scope into a mirror that never goes away —
so it does nothing at all.

🔴 **Removal never cascades into the borrower's adapters** (concept §6). An adapter left pointing at
a mirror that has gone is a defined, reported state; deleting a tenant's adapters because a lender
narrowed its scope would be a far worse failure than a blocked deploy.

Triggers — all idempotent, so the startup re-check and a backfill are one code path:

| trigger | where | covers |
|---|---|---|
| tenant load / `Enable` / `PosUpdateTenant` | `DefaultConfigurationCreatorService.EnsureLentAdapterPoolMirrorsAsync` | the backfill, a new or re-parented tenant, a hand-deleted mirror, and `clearCache` as the manual convergence lever |
| `AdapterPool` deploy / undeploy | `DeploymentSiteService.FanOutAdapterPoolMirrorsAsync` | `DeploymentState` — the one mirrored field that changes without a tenant load, and the one that explains a queue which never drains |
| `POST {tenantId}/v1/adapterPool/mirrors/refresh` | borrower-side, read-write | a lender-side rename / sharing-mode / allow-list change, written through the asset repository and therefore reaching no hook here |
| `POST {tenantId}/v1/adapterPool/mirrors/publish` | lender-side, read-write | the same, pushed instead of pulled |

Both hooks are best effort and never fail what they hang off: a mirror refresh must not fail a
deploy the operator has already been told about, and a tenant that cannot reconcile must still load
— but the tenant-start failure is written to the tenant's **event log** as an Error, because with
the association leading it is not merely a later refusal.

**What the mirror carries**: identity, the `Lender` record, the lending pool's `SharingMode`,
`MinReplicas`/`MaxReplicas` and `DeploymentState`. Deliberately **not** chart name, version or
values — lender-side deployment detail that changes on every rollout and would invite a borrower to
reason about a release it has no say over. Pool members and the lease queue are not mirrored at all
(concept §8).

### 🔴 The re-link: the second half of the migration note

`migration-meta.yaml` records why no CK migration can convert the two removed attributes into the
association — the target is a mirror the controller provisions at **run time**, so its RtId does not
exist while a migration runs — and ends with "the relationship is re-established by the mirror sync
**plus a re-link of the adapter**". Until the re-link change the second half had no implementation: the sync
provisioned the mirror, and nothing linked the adapter to it.

Measured on the local kind cluster, tenant `borrowerdev`, on 2026-09-24:

| | |
|---|---|
| the mirror | correct — `LentAdapterPool 6aaecb0e6f3d1e1d08ede037`, `Lender` = `accounting` / `49240000000000000000aa01`, DEPLOYED |
| the leased adapter `49240000000000000000bb01` | `lentFrom.totalCount = 0` |
| `POST {tenant}/v1/adapterPool/mirrors/refresh` | `{"isNoOp": true}` — **correctly**: the mirror was right, and the backfill had no opinion about the adapter's edge |
| the adapter's own health | `statusMessage` null, `lastDeploymentError` null, `LifecycleMode` Leased |
| the repair | a hand-written GraphQL mutation |

**It is scriptable because the old data is still there.** The attribute removal rode the engine's
post-chain schema-only bridge, which upgrades the schema and does **not** delete attribute values.

🔴 **The stored key is `lentFromPoolRtId`, NOT `lentFromAdapterPoolRtId`** — the `Pool` →
`AdapterPool` rename happened later in the same 4.x line and the data carries the older spelling.
And adapters live in the **polymorphic** collection `RtEntity_SystemCommunicationDeployableEntity`;
there is no `RtEntity_SystemCommunicationAdapter`.

`AdapterPoolMirrorProvisioningService.RelinkLeasedAdaptersAsync` runs **between the upsert and the
removal sweep** — the mirror an adapter is linked to has to exist already, and a mirror the removal
pass is about to delete must never gain an edge. For each `LifecycleMode = Leased` adapter with no
`LentFrom` edge it reads the leftovers and, when they match the `Lender` record of a mirror that is
in the desired set, writes the edge through
`ICommunicationRepository.TryLinkAdapterToLentAdapterPoolMirrorAsync`.

**How the leftovers are read.** `GetAttributeValueOrDefault(...)`, never a generated property —
there is none any more. It works because the engine's attribute dictionary is **model-agnostic on
both write and read**: `RtAttributeDictionarySerializer` camel-cases every key it writes and
Pascal-cases every key it finds, without consulting the CK model, and `EntityRuleEngine` only ever
validates *declared* mandatory attributes. So `attributes.lentFromPoolRtId` comes back as
`LentFromPoolRtId` on an entity whose type no longer declares it. No projection is involved in
`GetWorkloadsAsync`, so nothing filters it out.

Properties, each with a test:

- **Idempotent.** An adapter that already has an edge is skipped here *and* in the repository, which
  re-reads the current edge inside its own transaction. A second run reports zero.
- 🔴 **It never invents an edge.** Leftovers absent, blank, half present, or naming a pair no mirror
  carries ⇒ the adapter is left alone. A leased adapter pointing at a pool nobody lent it is worse
  than one pointing at nothing: the latter is already a loud, named refusal at the enqueue (`LeaseRefusalReason.BorrowerNamesNoPool`), while the former would send lease requests to a lender
  that never agreed. An adapter whose edge points at a *different* mirror is not re-pointed either —
  the sweep fills a gap, it does not re-decide a pointer somebody set deliberately.
- 🔴 **It makes no lending decision** (concept §4). The mirrors it matches against were just decided,
  one by one, by `MayLendAsync` against each **lender's own** pool — that is why the desired set is
  passed in rather than re-read. All it restores is a pointer the borrower already had, to a pool it
  is already entitled to.
- 🔴 **The leftover values are not deleted.** They are harmless dead data — nothing but this sweep
  reads them — and the only remaining evidence of what the adapter borrowed. Deleting them would
  make the repair unrepeatable, so it could only ever be right *after* the edge exists, and even then
  it buys nothing but a write: the next reconcile finds the edge and never looks at them again. If
  they are ever to go it should be one deliberate, reported cleanup pass over an estate where every
  borrower is provably re-linked, not a side effect of something that runs on every tenant load.
- **Best effort.** One adapter's failure is logged and the sweep continues; an unreadable workload
  set gives up on the re-link only, leaving the mirror reconcile and tenant startup untouched.
- **Cheap when there is nothing to do.** No mirror ⇒ no possible match ⇒ it returns before touching
  the database, which is the case for the overwhelming majority of tenants. A tenant that does hold
  mirrors pays one `GetWorkloadsAsync` — one query per tenant, the same read the lease-topology sweep
  already makes every 60 s — and only a tenant that also has a leased adapter pays a second, batched
  edge read.

`AdapterPoolMirrorSyncResult.AdaptersRelinked` counts it and **counts towards `IsNoOp`**: the run
that surfaced this defect answered `isNoOp: true` while a borrower sat unlinked, and a repair that
reports nothing is how the gap stayed invisible. The count is surfaced on both refresh endpoints and
in the tenant-load log line, and each re-link is written once to the tenant's **event log** — it is a
one-time repair per adapter, and the operator who sees a borrower start working again should be able
to find out why without reading pod logs.

### The borrower's read-only detail view

`GET {borrowerTenantId}/v1/adapterPool/lent/{lenderTenantId}/{adapterPoolRtId}` →
`LentAdapterPoolDetailsDto`. The other half of the decision above: the fields that are deliberately
**not** mirrored are still worth seeing, so they are read **live** from the lender per request and
never written down. That keeps the mirror small and, more importantly, keeps it from becoming a
second, stale copy of the pool's shape.

🔴 **The route tenant is the BORROWER here** — unlike every other verb on `AdapterPoolController`,
where it is the lender.

🔴 **The mirror is not consulted and is not trusted.** A tenant can edit entities in its own
database, so a mirror pointed at an arbitrary pool must not become a way to read it: the pool is
resolved from the route values and `MayLendAsync` runs against the **lender's own** sharing mode and
allow-list, exactly as the lease path does. A pool that does not lend here answers **404 with the
same message** as a pool that does not exist — distinguishing the two would turn the endpoint into a
pool enumerator, and `AdapterPoolControllerLentDetailsTests` pins that the two messages stay equal.

What it adds over the mirror: `ChartName` / `ChartVersion`, the four `PoolMember*` CPU and memory
values, `ScaleUpPolicy` + its two thresholds, `StatusMessage`, and this tenant's own
`MaxConcurrentLeasesPerTenant`. The lending **allow-list** is deliberately absent — it names the
lender's other customers, and the one answer that concerns the caller is that it is reading the page.

**Members** come back as counts only (`Connected` / `Busy` / `Draining`), never as member ids: a
member id plus its active-lease tenant would tell one borrower which of the lender's other customers
is running right now. ⚠️ They are per controller **instance**, the same property
`IOperatorConnectionManager` has, and `IsPartialView` carries that on the wire so a surface renders
the caveat instead of remembering it.

**The queue** comes back split — the caller's own entries in full, everyone else's as counts
(`OtherTenantsWaitingCount`, `OtherTenantsLeasedCount`, `OtherTenantsInRotation`). A pool's queue
spans tenant databases, so serving the lender's whole queue would hand one borrower the execution
ids, pipeline names and tenant ids of the lender's other customers. `OtherTenantsInRotation` is the
number that actually governs the wait, because the pool serves tenants round-robin — and there is
still no global rank, for the reason `AdapterPoolQueueEntryDto` gives. The split lives in one
function, `AdapterPoolController.BuildBorrowerQueueView`, rather than in two filters at two call
sites: every entry either lands in `MyEntries` or contributes only to a count, so a later field
cannot slip through a third path.

`LentAdapterPoolDetailsDto` is declared in the controller's `Models/`, **not** in
`Communication.Contracts` — the same interim arrangement `AdapterPoolMemberDto` and
`ServiceAccountHealthDto` live under. The only consumer is the Refinery Studio over plain HTTP, and
putting it in the contracts package would make every change to that screen a package lift plus a
version cascade through every service taking the package. Move it there together with
`AdapterPoolMemberDto` when a typed .NET client needs it.

**The demo blueprint seeds a mirror, and normally nothing should.** `AdapterPoolBorrowerDemo` needs
a fixed rtId for its `LentFrom` edge and a controller-generated one has none. It is safe because the
upsert keys on the `Lender` record: the first reconcile **adopts** the seeded entity rather than
adding a second mirror, and if the named lender does not lend there it **removes** it — leaving a
refused deploy with a named reason, which is the loud failure the sample wants.

Tests: `Services/AdapterPoolMirrorProvisioningServiceTests/` (borrower reconcile incl. the
revocation and unreadable-lender rules, lender fan-out incl. the one-reconcile-per-borrower and
failing-borrower rules, and `RelinkLeasedAdaptersTests` for every re-link decision rule — edge
present, edge elsewhere, leftovers missing / half present / blank / matching nothing / matching a
doomed mirror, several mirrors, non-Leased modes, the idempotent second run, the event-log line, the
best-effort paths and the two cheapness assertions), integration
`Repository/LentAdapterPoolRelinkTests` (🔴 **the half a mock cannot show**: the orphaned values
survive a round trip through a model that no longer declares them, the stored key really is
`lentFromPoolRtId`, one reconcile writes the edge and the second changes nothing, and leftovers
naming a pool that does not lend here leave the adapter unlinked), the four fan-out cases in
`Services/DeploymentSiteServiceTests/AdapterPoolDeploymentTests`, and
`Controllers/AdapterPoolControllerLentDetailsTests` (the detail endpoint: the two 404 paths say the
same thing, the scope resolver is not consulted for an unresolvable pool, members are counted and
never named, and the queue split incl. its tenant-id casing).
