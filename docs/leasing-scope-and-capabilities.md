---
description: "Leasing: lending scope, deploy guards, execution class, deploying to a Leased adapter and node capabilities"
applies_to: "**/Services/TenantLendingScopeResolver.cs, **/Services/PipelineExecutionClassService.cs, **/Services/AdapterNodeCapabilityService.cs"
---

## Leasing: lending scope and execution class (increments 2 + 4)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §4 and §6.

### Lending scope — `ITenantLendingScopeResolver`

Resolves which tenants an `AdapterPool` may lend to. `Services/TenantLendingScopeResolver.cs`,
singleton, 30 s cache (the walk opens an admin session per descendant and must not run per work
item).

🔴 **It does not call `GET {tenantId}/v1/tenants/descendants`.** This service has no
`Sdk.ServiceClient` reference and a background scheduler has no caller token to forward. It
re-implements the same breadth-first `ITenantContext` walk in process.

🔴 **Copying `GetDescendants` verbatim is necessary but NOT sufficient.** Its `visited` set stops the
walk from looping; it does **not** stop an ancestor from appearing in the result. With a corrupted
registry (parent `P` lists child `C`, and `C` also lists `P`), the walk from `C` adds `P` — never
visited, so nothing rejects it. For a listing endpoint that is tolerable; here the same set decides
whether one tenant may execute work **inside** another, so it would be an upward privilege
escalation. The resolver subtracts the lender's ancestor chain, walked via `ParentTenantId` — a path
independent of the child records a cycle corrupts. `ARegistryCycleCannotMakeAnAncestorBorrowable`
fails, **and nothing else does**, if that subtraction is removed.

Also: an unresolvable lender lends to **nobody** (never a wider scope), a root tenant has **no**
siblings, and `LendingAllowedTenantIds` intersects — it can never widen past the subtree.

### Deploy guards (`DeploymentSiteService.EnsureLeasingConfigurationIsValidAsync`)

Same enforcement rationale as the `LifecycleMode` block: all of this is plain CK author
configuration with no service-layer hook, so the deploy is the net. `Leased` requires
`OnDemandCapable` (reusing `IWorkloadOnDemandCapabilityService`, never a duplicated trigger list);
`LentFromTenantId`/`LentFromPoolRtId` are validated as a pair and against the resolver;
`LentFrom*` set without `Leased` is an error, because a value that silently does nothing reads like
the workload borrows a process when it runs its own; an `AdapterPool` may never itself be `Leased`.

Read surface: `GET {tenantId}/v1/adapter/lending?adapterPoolRtId=…` → `AdapterLendingScopeDto`.

🔴 **It runs FIRST in `EnsureWorkloadIsHelmDeployableAsync`, and the order is the fix.** It
used to sit at the bottom of that method, behind the chart-name and Helm-repository checks — and a
canonical leased adapter has **no chart name on purpose** (the borrower sample says so outright: chart
fields "would make it look deployable"). So every deploy of one stopped at *"the 'Chart Name' field is
empty … set a Helm chart name"*, advice that is wrong for a workload which must never have one, and
this gate — including the trigger half — was unreachable for exactly the shape it judges: an
`FromHttpRequest@2` pipeline could be attached to a leased adapter without a word and then never run.
Proved by the message changing shape: the same adapter with the same pipelines, with nothing altered
but `ChartName`, answered with the intended refusal instead.

Once leasing itself is sound, a leased workload with no chart is no longer an error to repair but the
normal shape, so it answers `WorkloadLeasedHasNothingToDeploy` — "it owns no process of its own, its
pipelines run on a member of the adapter pool it borrows from, deploy the POOL in the lending tenant
instead" — rather than sending the operator to fill in a field that must stay empty.

**The second holder of the same gate had the same problem and the same cause.**
`DeployDataFlowAsync` had no leased branch at all: everything in its loop needs the adapter in the
cache, i.e. a live SignalR connection, which a leased adapter never has, so it answered *"The adapter
pod must be deployed and online before its pipeline configuration can be pushed"* — an instruction
that cannot be followed — and `ValidateAndPersistPipelineAsync`'s per-pipeline arm never ran either.
It now takes the same route the single-pipeline path has had since leasing
(`DeployLeasedPipelineAsync`: validate, persist, no push), placed **before** the wake gate for
the reason given there — a borrower must not be able to start the lender's capacity by pressing Deploy.

⚠️ **Deliberately unchanged: the capability evaluation ignores `System/Enabled`.** A disabled pipeline
still blocks the deploy and is still named in the refusal. That is shared with the OnDemand
evaluation (`EvaluateAsync` enumerates the same unfiltered pipeline set), so changing it for leasing
alone would let the two modes drift — and it would silently weaken a gate that production adapters
currently rely on. Note the data-flow *deploy loop* does skip disabled pipelines, so the two read the
pipeline set differently; that asymmetry is known and is not what the descendant-scope change touched.

Tests: `Services/DeploymentSiteServiceTests/DeployWorkloadLeasedGateOrderTests` — 🔴 a separate suite
from `DeployWorkloadLifecycleValidationTests`, which covers the gate's CONTENT and cannot catch this:
its arrange helper gives every adapter a chart name, so it always entered through the door that was
shut.

### Execution class — `IPipelineExecutionClassService`

The trigger node declares its class via `[NodeExecutionClass]` (SDK), the reflection descriptor scan
picks it up, and it travels on `NodeDescriptorDto.ExecutionClass` — appended and defaulted, the same
wire-compat trick `RequiresRunningProcess` uses.

🔴 **Persisted in the same update as the definition it was derived from**
(`SetPipelineDefinitionAsync`), so the class and the YAML can never disagree. This is *not* where
`OnDemandCapable` is written — that is per-workload, after deploy, best-effort, and is not a
pipeline-save hook at all. The redeploy path (unchanged YAML, possibly different adapter
descriptors) uses the single-field `SetPipelineExecutionClassAsync`; rewriting the definition there
would break the "deploy must not persist a definition it was not given" invariant, which a test pins.

🔴 **`TryGetTriggerNodes`, not `TryGetAllNodes`.** The latter flattens `triggers:` and
`transformations:` — correct for on-demand capability, wrong here: the class describes how the work
*arrived*, so only a trigger may declare it.

⚠️ **Known wart, adopted deliberately.** Node descriptors are never persisted and are null for any
adapter that has not registered in this process, so `KnownInteractiveTriggerNames` mirrors
`KnownProcessBoundTriggerNames`. Bounded: two entries, a descriptor always wins, and drift degrades
to `Batch`, never to a wrong `Interactive`.

⚠️ **Pre-existing defect of the process-bound trigger list found while doing this:** `octo-adapter-loxone`'s
`LoxonePollTrigger@1` is process-bound but carries no `[NodeRequiresRunningProcess]` and is **missing
from `KnownProcessBoundTriggerNames`** — a workload whose only trigger is that node is classified
on-demand capable and would be hibernated. `FromLoxoneStateChange` is listed; this one was missed.
Needs its own work item.

⚠️ A pipeline edited to a different trigger changes class **on save**, so every surface must read the
current value rather than cache it — the same caveat `OnDemandCapable` carries.

## Deploying a pipeline to a `Leased` adapter

🔴 **`DeployPipelineAsync` has two named halves, and only the first is universal.**

| Half | Method | Runs for |
|---|---|---|
| validate + persist | `ValidateAndPersistPipelineAsync` | **every** adapter |
| push (project config, send over SignalR, wait for the ack) | inline in `DeployPipelineAsync` | dedicated adapters only |

`DeployLeasedPipelineAsync` is the leased entry point: shared half → `DeploymentState` →
capability refresh → audit event. Anything added to the deploy verb belongs in the shared half
unless it is genuinely about pushing bytes to a pod.

**Why this exists.** The method used to wrap its whole body in
`adapterTenant.AdapterById.TryGetValue(...)`, which is only populated for adapters holding an open
`/{tenantId}/adapterHub` connection. A `Leased` adapter never has one, so every leased deploy
answered 404 — and, far worse, **none of the four gates ever ran** on that path
(schema validation, process-bound triggers, mandatory service account,
elevation authorization). The pipeline was then executed by a borrowed process that had checked
none of them.

**Three deliberate differences on the leased path:**

- **No idle wake gate.** It was already a no-op, but only because `Leased != OnDemand` — a
  coincidence, not a decision. Now an explicit branch: a leased adapter has no workload of its own,
  and the pool is a *different tenant's* workload. A borrower must not be able to scale the lender
  by pressing Deploy.
- **`EnsurePipelineIsOnDemandCompatible` fires on `Leased` too**, with its own message factory
  `AdapterServiceException.PipelineNotLeasable` (different remedy: an OnDemand workload can go back
  to AlwaysOn, a leased one has no process to switch on). `DeploymentSiteService` enforces the same rule at
  *workload* deploy over the whole pipeline set; without this per-pipeline arm a new process-bound
  pipeline on an already deployed leased adapter would only be caught at the next workload deploy.
- **`SetPipelineDeploymentStateAsync` writes `Deployed` directly, never `Pending`.** `Pending` means
  "a push is in flight"; there is none, so the state would never leave it. Persisting IS the
  deployment here — the next lease runs exactly what was written. The status message names the
  difference.

### `IAdapterNodeCapabilityService` — whose descriptors answer

```csharp
AdapterNodeCapabilities Resolve(string tenantId, RtEntityId adapterRtEntityId, RtAdapter? adapter);
```

One seam for three deploy-time questions that each used to reach into `AdapterById` on their own:
schema validation, process-bound classification, and the execution class.

- dedicated → the adapter's cached `NodeDescriptors` / `PipelineSchemaJson`;
- `Leased` → `IAdapterPoolConnectionManager.TryGetPoolCapabilities(LentFromTenantId, LentFromPoolRtId)`.

🔴 **A leased adapter never falls back to its own adapter-cache entry.** An adapter switched from
`AlwaysOn` to `Leased` can still have a stale entry describing a process that no longer exists;
"nothing known" degrades to the name-based fallback (wrong-but-conservative), stale descriptors
would be wrong-and-confident.

**Which member answers:** draining members last (during a rollout they are the outgoing version),
then ordered by member id — deterministic, because a pipeline's persisted execution class must not
depend on dictionary enumeration order.

⚠️ **Per controller instance.** A SignalR connection lives on one pod, so with >1 replica the pod
handling the deploy may hold no member of the pool and answers "nothing known". Same limitation as
the member listing and the scale-up evaluation.

⚠️ **Still blind:** `WorkloadOnDemandCapabilityService.EvaluateAsync` reads only `AdapterById`, so
for a leased adapter it classifies with `descriptors = null` (name-based fallback) both in
`DeploymentSiteService.EnsureLeasingConfigurationIsValidAsync` and in the persisted display value.
`DeployDataFlowAsync` has no leased branch at all — it still falls through the `AdapterById` lookup.

### Pool members report node descriptors

`PoolMemberRegistrationDto.NodeNames` (a `string` list that no member ever filled and no controller
ever read) was **replaced** by `NodeDescriptors` (`IReadOnlyList<NodeDescriptorDto>`) plus
`PipelineSchemaJson` — the same values a dedicated adapter sends on
`RegisterAdapterWithSchemaAsync`, built by the shared
`AdapterNodeDescriptorProjection` in `octo-communication-sdk` rather than a second projection.

🔴 **Source-breaking in `octo-sdk`, wire-compatible in both directions** (SignalR's JSON protocol
ignores unknown members and defaults absent ones, and nothing ever populated `NodeNames`). Skew rule
as for every hub contract: `octo-sdk` publishes before `octo-communication-sdk` and this repo.
