---
description: "Shared adapter leasing: the System.Communication 4.0.0 major bump, migration, bump cascade and traps"
applies_to: src/SystemCommunicationCkModel/ConstructionKit/migrations/**
---

## Shared Adapter Leasing — CK model 4.0.0

A parent tenant runs an adapter pool its descendants borrow: a pool member is **leased** to a
tenant for the duration of one work item, then released. Design doc:
`docs/concepts/shared-adapter-leasing.md`; the increment plan, the measured bump cascade and
the places where the design does not survive contact with the code:
`docs/concepts/shared-adapter-leasing-implementation.md`.

**Only the CK model has landed so far (4.0.0).** Nothing reads these values yet — validation,
the pool workload, the lease protocol and the scheduler are increments 2–9 of the plan.

### 🔴 This is a MAJOR bump: 3.35.0 → 4.0.0, with a migration

3.36.0 (the first, additive cut) is **withdrawn**. The major is forced by one rename:

| Element | Where | Meaning |
|---|---|---|
| **`Pool` → `DeploymentSite`** | `types/deploymentSite.yaml` (was `pool.yaml`) | it is a deployment *location* (`Default Cloud`, `edge-site-1`), never a pool of anything — and the name collided head-on with the new `AdapterPool`. Ships **with** leasing so the estate takes one migration, not two (concept §8, Q16 + Q18). |
| **`Manages`/`ManagedBy` → `Hosts`/`HostedBy`** | `associations/hosts.yaml` (was `manages.yaml`) | a deployment location does not *manage* anything — the Communication Operator does. Renamed in the **same** migration as the type, so the estate migrates once. `Deploys`/`DeployedTo` was rejected: the site does not deploy either, it is deployed *to*. |
| `AdapterPool` | `types/adapterPool.yaml` | a pool of interchangeable adapter processes: `MinReplicas`/`MaxReplicas`, per-member sizing, queue-driven scale-up, lending scope. **One workload with a replica range**, not N `Adapter` entities. |
| `LifecycleMode` `3 Leased` | `enums/lifecycleMode.yaml` | the **borrower's** mode and only ever that: no process of its own. `2 Auto` stays reserved. |
| `PipelineExecutionStatus` `5 Queued` | `enums/pipelineExecutionStatus.yaml` | appended, never inserted — the numeric keys are persisted on every entity. |
| `PipelineExecutionClass` (`Interactive`/`Batch`) | `enums/pipelineExecutionClass.yaml` | on `Pipeline.ExecutionClass`, resolved from the trigger node **on save** and persisted, like `OnDemandCapable`. Keys ordered so ascending == scheduling order; default `Batch`. |
| `AdapterSharingMode`, `LendingAllowedTenantIds`, `LendingMaxConcurrentLeasesPerTenant` | `attributes/adapterLeasing.yaml` → **`AdapterPool`** | lender side. Moved off `Adapter` in the rework: lending is a property of the thing that owns processes. The allow-list **intersects** the resolved subtree and can never widen it. |
| `LentFromTenantId`, `LentFromPoolRtId` | `attributes/adapterLeasing.yaml` → `Adapter` | borrower side, set as a pair. Names the **pool**, not a member — membership is elastic. |
| `QueuedAt`, `LeaseGrantedAt`, `LeaseReleasedAt`, `LeaseWaitMs` | `attributes/attributes.yaml` → `PipelineExecution` | four timestamps, all runtime state. `LeaseGrantedAt..LeaseReleasedAt` ≠ `StartedAt..CompletedAt`: the difference is the per-lease warm-up the pool exists to amortise, and it is also the **billing** span (concept §4b), so it must be stamped on the TTL-expiry and crash paths too. |
| `LeasedFromTenantId`, `LeasedFromPoolRtId`, `LeasedOnMemberId` | `attributes/adapterLeasing.yaml` → `PipelineExecution` | which pool and member served it. |
| **`StartedAt` relaxed to optional** | `types/pipelineExecution.yaml` | a `Queued` execution has no start time (concept §8, Q5). |
| New index on `QueuedAt` | `types/pipelineExecution.yaml` | the queue read is `(status = Queued) ordered by QueuedAt`; it cannot ride the `StartedAt` index. |
| `migrations/3.35.0-to-4.0.0.yaml` + two meta entries | `migrations/` | `ChangeCkType Pool → DeploymentSite`, post-validation at `severity: Error`. |

🔴 **The borrower half and the execution's member reference are attributes, not associations,
and they have to be.** A CK association resolves inside one tenant database; the pool lives in
the lending tenant's and both the borrowing `Adapter` and its `PipelineExecution` live in the
borrower's. Concept §5's "an association from the execution to the assigned member" describes
an edge the engine has no database to store. There is therefore no referential integrity
behind these values — an unresolvable pool is detected at **lease** time, not at write time.

🔴 **The role rename moves THREE identifiers with disjoint blast radii.** `id` drives the
persisted `roleId` in seed data *and* the generated constant
`SystemCommunicationCkIds.RtCkManagesRoleId` → `RtCkHostsRoleId`; `inboundName` drives the
GraphQL field `manages` and `…_ManagesUnion*`; `outboundName` drives `managedBy` and
`…_ManagedByUnion*`. Only the `id` half has persisted data behind it. Measured blast radius:
**20 source files, 38 occurrences** — C# is *one* file (`CommunicationRepository.cs`, 4 call
sites); the operator, `octo-cli`, the MCP server and every C# test are **zero**; 17 of the 38
are hand-written Studio TypeScript. Separately, **59 generated/published artifacts must not be
hand-edited**, including the published catalog JSONs, which keep `Pool`/`Manages` forever.

🔴 **The role rename required a NEW CK engine transform.** `RtAssociation.AssociationRoleId` is
persisted on every edge and none of the seven existing transforms touches it, so renaming the
role without one orphans every deployment-site↔workload edge **silently** — the navigation just
returns nothing. `RenameAssociationRole` was added to `octo-construction-kit-engine`
(+ `-mongodb`); see `octo-construction-kit-engine/docs/ck-model-migrations.md` there. **It must ship before the CK model**:
the CK compiler validates migration YAML against the schema in the *installed* engine package,
so until it is published the model fails with
`Schema validation failed at '/steps/1/transform/type' … "RenameAssociationRole"`.
(`ChangeCkType` already rewrites `originCkTypeId`/`targetCkTypeId` on edges, so the *type* half
of the migration needed nothing new — verified in `TenantRepository`, not assumed.)

🔴 **`rtWellKnownName` is renamed from `CommunicationPool` to `DeploymentSite` (step 3).** It is
identity, not display text: the service-managed blueprints re-apply against it. Renaming it in the
seed alone would create a *second* deployment site on every provisioned tenant, so the seed change
and the stored-data change ship together in this migration. The step selects the type and the old
name, so it renames only the seeded site (not sites an operator created by hand) and a second run
matches nothing.

### Bump cascade — measured 2026-09-14, not estimated

Scope matters here. In **this `dev` checkout**, **65 files** carry a `System.Communication`
version pin (61 source + 4 `.octo` artifacts), of which **46 source files are blueprint files**.
Estate-wide (all checkouts, including repos not cloned here) it is **203 blocking pins + 7
open-ended**. 🔴 The ~70 pins in the **published catalog** (`meshmakers.github.io` and the local
mirrors under `.octo/local-catalog/.../System.Communication/3/`) are immutable history — they
must keep saying `Pool`/`Manages` and must never be edited. 56 of the 61 hard-block at `<4.0`; the other 5
are open-ended (`[3.6,)`, `[3.12,)`, `[3.13,)`, all in `demo-energy-iq/data/`, which is on
branch `main`) and **silently absorb 4.0.0** — the more dangerous half.

- 🔴 **Blueprint `ckModelDependencies` floors are NOT "satisfiability floors only" under a major bump.** `[3.22.0,4.0)` *excludes* 4.0.0: the blueprint becomes unsatisfiable. Both embedded blueprints are therefore raised to `[4.0,5.0)` and bumped `1.5.0` → **`2.0.0`**, and their seed now creates a `DeploymentSite`. The same applies to the 8 other `blueprint.yaml` files and 36 seed-data files in `octo-construction-kit`, `octo-office-integration` and the accounting app.
- 🔴 **Both dependent CK models need MAJOR bumps, not minor ones.** `ck-semver-rules.md` classifies "dependency switched to a new major" as Major. `System.Ai-3.7.0` → **4.0.0** (which in turn breaks `System.Ai.Default`'s `System.Ai-[3.1.2,4.0)` pin); `Loxone-4.3.1` → **5.0.0** with its range widened off `[3.31,3.32)`.
- 🔴 **Loxone must move WITH the bump.** The exact pin `[3.31,3.32)` resolves to 3.31.0 and is unaffected by 3.x bumps — which is what makes it dangerous. `CkModelId` is name+version and `CkModelMigrationService` refuses to migrate across names, so a tenant holds exactly **one** version of `System.Communication`: a production tenant running Loxone cannot take 4.0.0 at all while the pin stands. Third repeat of the same pin incident.

### 🔴 Two traps specific to this bump

**A major bump renames the generated C# namespace.** `…Generated.System.Communication.v3` →
`.v4` breaks **123** `.cs` files (122 here, 1 in `octo-ai-services`) — far more than the 26
that the `RtPool` → `RtDeploymentSite` rename itself touches. Also renamed:
`AddCkModelSystemCommunicationV3()` → `…V4()`, and — because the blueprint went to 2.0.0 —
`AddBlueprintSystemCommunication{Release,MainLatest}V1()` → `…V2()`.
`octo-sdk`'s `CommunicationCkTypeIds.Pool = "System.Communication/Pool"` is a `const string`,
so it compiles fine and is simply **wrong at runtime** — grep for it explicitly.

**A stale build artifact makes the rename look free.** The CK output directory holds one
compiled model per major, and the source generator emits `Rt*` classes for *every* file it
finds there. A leftover `ck-system.communication-3.yaml` next to the new `-4.yaml` makes it
emit `RtPool` **and** `RtDeploymentSite`: the repo compiles clean, all 917 tests pass, and
`RtPool` silently refers to a type the installed model no longer defines. Deleting
`bin/DebugL` + `obj/DebugL` turned "0 errors" into **506**. Never trust a green *incremental*
build of a major CK bump — validate after `git clean -xfd`.

**Migration entry points.** `CkModelMigrationService.FindBridgedMigrationPathAsync` picks
candidate entry points as "every `fromVersion` **greater than** the installed version", so the
entry hangs off `3.35.0` (not off the 3.1.1 chain end, which would drag modern tenants back
through four obsolete DataFlow scripts). A tenant on the **withdrawn 3.36.0** sits above every
entry point, falls through to the post-chain schema-only bridge and would **skip the rename
silently** — 3.36.0 is in `dev/.octo/local-catalog`, so `migration-meta.yaml` carries a second,
deletable `3.36.0 → 4.0.0` entry pointing at the same idempotent script.

🔴 **G3 (2026-10-07): the same hole for every main release since the major was cut.** main kept
shipping 3.x (3.36.0 is a real main release too, then 3.37.0 … 3.40.0, announced 3.41.0/3.42.0), and
a tenant above the highest `fromVersion` has no path at all. `migration-meta.yaml` therefore lists
**every** 3.x ≥ 3.35.0 that main released or announced (3.37.1 and 3.39.1 included — never in the
private catalog, but on local tenants), all pointing at the one script. **Whoever bumps
System.Communication 3.x on main before the 0.2 lane lands there must add the new version here.**
Since engine `d035715` (`CkMigrationMajorVersionGuard`) a missing entry no longer slips through: the
post-chain bridge is refused across a major, the upgrade fails, and the MigrationHistory stays at the
3.x version until a model with the entry is deployed.

**2026-10-08, cutover W1: 3.41.0 → 4.6.0, and no 3.42.0 entry on purpose.** main's
published 3.41.0 is the status-history release WITHOUT SECRET (`LastSuccessfulStatusAt` /
`ConsecutiveStatusFailures`, `FoldedBefore`); **4.6.0** carries exactly that content into the
4.x line, so a 3.41.0 tenant must meet 4.6.0 or later (ChangeCkType keeps the stored values; 4.5.0
would not declare them). **3.42.0 is deliberately NOT listed:** it was the planned 3.x cut of the
SECRET switch and was **never released** (the published 3.x line ends at 3.41.0) — the switch
ships on the 4.x line instead, as the next free 4.x minor after 4.7.0 (4.7.0 is the ownership
change), so there is no published 3.42.0 to check an entry against. A 3.42.0 that exists on a local tenant is refused by the
major-version guard and rebuilt.

**4.8.0 (SECRET phase 3) adds no migration entry either.** The credential
attributes (`Password`, `PrivateKey`, `PrivateKeyPassphrase`, `ClientSecret`, `ApiKey`, `BotToken`,
`AdminPassword`) become `valueType: Secret` (String → Secret is a MINOR per `CkSemVerClassifier`), all
slots optional, and `ValueOverride` gains the optional Secret member `SecretValue` (`recordKey: Path`).
Stored clear text stays readable (readers accept legacy strings in a Secret slot); legacy placeholders
and empty strings become "not set" through the engine's post-migration hook
(`CkModelMigrationService.NormalizeSecretPlaceholdersAsync`, runs after every successful migration incl.
the schema-only bridge). A 3.41.0 main tenant reaches 4.8.0 through the 3.41.0 entry point plus that
bridge — pinned by `TenantOn3410_CredentialsStayUsable_AsSecretsOn480`. Every read of such an attribute
goes through `IWorkloadEncryptionService` (`Reveal` / `RevealOrNull` / `DescribeSecret`), never an
`as string` cast: on 4.8.0 the raw value is an `RtSecretValue`. The lane-only reader that did cast —
`LeaseService.ResolveBorrowerCredentialAsync` — now reveals like the deploy path
(`Services/LeaseServiceTests/LeaseBorrowerSecretRevealTests`). `CallerAccessToken` stays a String
(`enc:v1`; since 4.12.0 it is no longer written, see "The queued invoker is sealed"). 4.8.0 keeps 4.7.0's `ownership: Secret` on `Adapter.AdapterConfiguration` unchanged —
that attribute stays a `String`. Concept: `docs/concepts/secret-attributes.md`.

Tests: integration `Migrations/SystemCommunication4MigrationTests` — the published 3.40.0 and
3.41.0 models (embedded resources; other versions are the 3.40.0 re-labelled) plus the blueprint's 3.x seed, upgraded through the real
`ImportCkModelAsync` for every listed version: path contains the script, a dry run fails the
`no-legacy-pools` post-validation on the 3.x data, the real upgrade leaves no `Pool`, keeps the
rtIds, renames the seeded well-known name (`CommunicationPool` -> `DeploymentSite`) only, resolves both adapters' sites over `Hosts`, and the
post-validation then passes. A version above the last entry (3.99.0) is refused and stays on 3.x
across a second start. With the pre-G3 meta, exactly the eight cases 3.37.0 … 3.42.0 fail.

### Out of scope of the rename, deliberately

The REST route `{tenantId}/v1/deploymentsite` (from `DeploymentSiteController`) is not renamed.
(The seeded entity's `rtWellKnownName` **was** renamed, see step 3 above. The operator CRD, the operator code, the Helm value names
`operator.{autoManageDeploymentSites,deploymentSiteNamespace,defaultDeploymentSiteName}` and the SignalR wire **were**
renamed together with the model: kind `CommunicationPool` -> `DeploymentSite`, deleted and reinstalled, so the operator
and the controller ship together.) Also unchanged: the `octo-cli` `GetPools`/`DeployPool`/`UndeployPool`
commands and the MCP `get_pools`/`undeploy_pool` tools. The GraphQL type
`SystemDeploymentSite`, however, is derived from the CkTypeId and **renames itself** on
publish — ~66 frontend files across the Studio, `octo-frontend-libraries` and the accounting app
break without anyone editing them, so codegen must be re-run in the same train.
