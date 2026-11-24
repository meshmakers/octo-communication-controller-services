---
description: "Service-managed blueprints System.Communication.*: seed data, versions, update and re-apply behaviour"
applies_to: src/SystemCommunicationCkModel/Blueprints/**, **/Consumers/BlueprintAppliedConsumer.cs
---

### Service-Managed Blueprints (System.Communication.*)

`SystemCommunicationCkModel` ships two embedded blueprints that seed a tenant with a
default Cloud `Pool` + managed `Adapter`. The variants are picked per cluster via
`requires.octo.environment`, sharing the same rtIds so a tenant moving channels
(staging→prod hand-over) keeps its entities. The pattern mirrors the embedded
CK-model packaging — see `octo-construction-kit-engine/docs/blueprints.md` for the
engine side.

```
src/SystemCommunicationCkModel/
├── ConstructionKit/                          # CK model YAML
└── Blueprints/                               # System.* = service-managed
    ├── System.Communication.Release/         # requires.octo.environment: [staging, production]
    │   ├── blueprint.yaml                    #   blueprintId: System.Communication.Release-1.0.0
    │   └── seed-data/entities.yaml           #   ChartVersion: "${octo.version}"
    └── System.Communication.MainLatest/      # requires.octo.environment: [dev, test]
        ├── blueprint.yaml                    #   blueprintId: System.Communication.MainLatest-1.0.0
        └── seed-data/entities.yaml           #   ChartVersion: ""
```

The folder name carries only the blueprint **Name**; the version lives exclusively
in the manifest's `blueprintId`. Bumping the version is a manifest-only edit — no
folder rename required. The `BlueprintEmbed` MSBuild task validates that the folder
name equals `blueprintId.Name`.

| Variant                                | ChartVersion seed                | Use case |
|----------------------------------------|----------------------------------|----------|
| `System.Communication.Release`         | `"${octo.version}"`              | staging / production — matches the release-channel chart 1:1 (both derived from the same r-tag), so the first Deploy on a freshly attached tenant lands a working chart pull. |
| `System.Communication.MainLatest`      | `""` (empty)                     | dev / test — the operator's `HelmRunner` omits `--version` when blank and helm picks the newest chart from the dev channel. CD pipeline `deploy-adapter-chart-octo-mesh-adapter-*` then writes a concrete `0.1.<yyMMDDxxx>` version on the next main-CI rollout. |

Flow:

1. `<BlueprintFolder>` in `SystemCommunicationCkModel.csproj` triggers the `BlueprintEmbed` MSBuild task. The task validates each `blueprint.yaml`, embeds the files as manifest resources, and emits `obj/.../blueprints-cache.json`. The `BlueprintSourceGenerator` generates one DI extension per blueprint (`AddBlueprintSystemCommunicationReleaseV1`, `AddBlueprintSystemCommunicationMainLatestV1`) plus their `IBlueprintEmbeddedSource` implementations.
2. `Program.cs` registers every embedded source via the generated `AddBlueprint…V1()` extensions. The engine's `EmbeddedResourceBlueprintCatalog` (auto-registered by `AddConstructionKit()`) discovers them.
3. `DefaultConfigurationCreatorService.ImportCkModelAsync` (Enable path) and `StartTenantAsync` (every tenant load) both call `ApplyServiceManagedBlueprintsAsync`, which iterates every embedded blueprint whose name starts with `ServiceManagedBlueprintPrefix = "System.Communication."` (trailing dot anchors the match), picks the newest version per name, and invokes `IBlueprintService.ApplyBlueprintAsync`. Each blueprint's `requires:` decides whether it actually runs on the given tenant — non-matching ones return `BlueprintApplicationResult.Skipped` (success no-op, no install row, no seed). The blueprint runner is idempotent at the same version, so the cost of the per-startup call is near-zero unless the embedded version bumped.
4. Bumping any variant's `blueprintId` in `blueprint.yaml` (e.g. `System.Communication.Release-1.0.0` → `…-1.0.1`, no folder rename required) and shipping a new NuGet rolls every applicable tenant forward on the next service restart — no per-tenant migration script required.

The `System.` name prefix tells Refinery Studio (`BlueprintIdExtensions.IsServiceManaged`) to hide Install / Re-apply controls — the service owns these blueprints and admins shouldn't fight them.

> **Migration note**: tenants previously seeded with `System.Communication-1.0.2` keep that installation row as an orphan after the split — the new blueprints overwrite the Pool/Adapter entities by shared rtId (functional behaviour preserved), but the old install row still shows up in Studio. A one-time `mutation { blueprints { uninstall(blueprintName: "System.Communication", cascade: false) { success entitiesDeleted } } }` removes it per tenant.

The optional `HelloCommunication-1.0.0` demo blueprint lives in `samples/Blueprints/` (not embedded). It's admin-installable: drop the folder into a `LocalFileSystemBlueprintCatalog` root or publish it via a GitHub catalog. Its `blueprintDependencies` reference `System.Communication-[1.0,2.0)` so it can be applied on top of the service-managed baseline.

`samples/Blueprints/SecretDefaultDemo/` is the secret-default sample: two blueprints (`SecretDefaultDemo` with
the Application, whose `SecretDefaults` entry the operator adds after the install, `SecretDefaultDemoCaptcha` with a tenant override and a `VerifyCaptcha@1`
route), a small CK model and the `verify/` commands. See "Secret defaults".

#### Runtime-State Attributes (do NOT reset on blueprint re-apply)

`System.Communication-3.22.0` (paired with blueprints
`System.Communication.Release-1.5.0` and `System.Communication.MainLatest-1.4.0`)
marks the volatile state attributes on `Adapter` / `Pool` as
`isRuntimeState: true` in `attributes.yaml`. The blueprint engine reads
that marker (see `octo-construction-kit-engine/docs/runtime-state-preservation.md`) and preserves the existing runtime value when
the blueprint is re-applied for a version bump — instead of overwriting
it with whatever the seed YAML happens to declare as the
fresh-tenant default.

| Attribute | Why it's runtime state |
|---|---|
| `DeploymentState` | Driven by `DeploymentSiteService.Deploy/UndeployPoolAsync` + the reverse-sync from `ReportDeployedStateAsync`. Reset to 0 by a seed import flips a Deployed entity back to Undeployed in Studio. |
| `CommunicationState` + `CommunicationStateTimestamp` | Written by the `OperatorHub` / `AdapterHub` connect & disconnect handlers and the rolling-upgrade shutdown guard. Seed-managed reset would mark an online operator as Unregistered. |
| `ConfigurationState` | Toggled by `AdapterService` on configuration deploy / failure. |
| `StatusMessage` | Live status text overwritten on every state transition — pure runtime breadcrumb. |
| `LastDeploymentError` + `LastDeploymentErrorTimestamp` | Persistent error history written when a deploy fails; cleared on the next successful deploy. The blueprint must never silently clear these. |
| `LastConfigurationError` + `LastConfigurationErrorTimestamp` | Same contract as the deployment-error pair, scoped to the configuration state machine. |
| `LastSyncedSequenceNumber` | Adapter offline-sync cursor — wiping it would replay every queued event. |

Before this marker landed (CK model 3.21.0 and earlier), bumping a
service-managed blueprint silently reset all of these on the next
controller restart. The first observed regression was the
1.3.0 → 1.4.0 bump on `System.Communication.Release` (commit
`80cd9422`, adding the `meshmakers-apps` HelmRepository entity): every
tenant whose `MeshAdapter` had been `Deployed` flipped back to
`Undeployed` even though the helm release was still healthy and the
operator was still connected. Recovery for that incident is documented
in `docs/runbooks/recover-mesh-adapter-state.md`. Any future
service-managed-blueprint bump on 3.22.0+ should not need a runbook
companion for this class of failure.

**Operator-managed workload deploy attributes are runtime-state too.** `Hostname`, `IngressEnabled`, `ChartVersion`,
`ValuesYaml` and `Values` on the deployable-workload attribute set
(`attributes/helmDeployment.yaml`, shared by `Adapter` and `Application`)
carry `isRuntimeState: true`. These fields are written at run time — the
productive hostname (e.g. `<tenant>.<customer-domain>`) and per-tenant
`ValueOverride`s (publicUri, clientId, …) by operators, `ChartVersion` by
the CD pipeline's `UpdateWorkloadChartVersion` — and an app blueprint that
seeds defaults (e.g. `EnergyCommunity.Base`'s Application entity) reset
all of them on every `InstallBlueprint -f`, silently breaking the deployed
ingress/DNS and OIDC configuration (observed live on a production demo tenant,
2026-08-04). Seeds now only apply on fresh installs. Consequence for
blueprint authors: a blueprint update can no longer ship CHANGED default
values for these five attributes to existing tenants — by design; document
new defaults in release notes instead. Note the association side (e.g. an
Application's `HelmRepository` edge) has no preserve mechanism — blueprints
must own their referenced `HelmRepositoryConfiguration` entity instead of
pinning foreign per-environment rtIds (a solution Base blueprint does this
since 2.2.5).

**`Pipeline.IsDebuggingEnabled` is runtime-state too.**
Debugging is an operational toggle (`SetPipelineDebug` / the Studio debug
button), not part of a pipeline's portable definition — so
`RtEntityToTcDtoConverter` skips it on export and it never round-trips
through `ExportRt`/`ImportRt` or a blueprint. The `isRuntimeState: true`
marker was added to `IsDebuggingEnabled` in `attributes.yaml` back in commit
`e54c749` but **in place on 3.22.0 without a version bump** (the 3.21.0→3.22.0
bump `0b31271` had already been cut ~46 min earlier), so a same-version
re-import is a no-op and the marker never reached tenants already on 3.22.0 —
which is why an imported pipeline (e.g. a customer simulator shipping
`IsDebuggingEnabled: true`) could still silently enable debugging. `3.23.0`
is the bump that actually propagates the marker on upgrade. No migration
script (`migration-meta.yaml` stays at 3.1.1 — schema-ahead-of-history; the
change is additive metadata, existing attribute values are untouched).
Note the flag only stops debug shipping via *export*; a hand-authored rt-model
that explicitly sets a runtime-state attribute still writes it on import, so
authored YAMLs must simply not declare `IsDebuggingEnabled` (fixed for the
simulator).

**`Configuration.ClientSecret` is runtime-state too.** The shared
`ClientSecret` attribute definition (used by `ServiceAccountConfiguration`,
`FinApiConfiguration` and `MicrosoftGraphConfiguration`) carries `isRuntimeState: true`
so a blueprint re-apply can no longer overwrite a live secret with the seeded
placeholder. Details, blast radius and the first-install semantics: see
"Pipeline Service Account — mandatory execution identity" below.

**The last seed-owned `EnergyCommunityConfiguration` values are tenant-owned.**
`BillingReportName`, `BillingReportFilePrefix`, `BillingMailServerConfiguration` and
`ConsumptionRecordRequestDelay` moved from `isRuntimeState: false` to `ownership: TenantOwned`,
and the type's `${System}/Name` assignment carries a per-assignment `ownership: TenantOwned`
override (the shared `System/Name` definition stays seed-owned for every other type). They had
been kept seed-owned so that a blueprint version could correct them, but the tenant-neutral
`EnergyCommunity.Base` seed ships them empty (the delay with its default) and never seeds a name:
every re-apply emptied what the community had configured, and billing lost its report template.
`TenantOwned` rather than `isRuntimeState: true` because these are settings a tenant export has
to carry — preserved on re-apply, still part of `ExportRt`. Consequence for blueprint authors: a
changed default for these values reaches existing communities only through a CK migration.

**`Adapter.AdapterConfiguration` is `ownership: Secret` (4.7.0).** The
JSON a tenant types into an adapter (for the EDA adapter the Ponton host, user and password) was
seed-owned, and a blueprint seed can only carry the empty skeleton, so every blueprint update
(`EnergyCommunity.EdaIntegration`, `Base`) replaced the stored value through the Upsert's full
ReplaceOne — three production communities lost their EDA credentials on 2026-10-08. Now a re-apply keeps the
stored value (also when the seed omits the attribute) and an `ExportRt` leaves it out. `Secret`
rather than `TenantOwned` because the JSON holds a password and must not travel in an export file;
for a `String` attribute `Secret` ownership changes exactly two things — preserve on upsert and
exclude from export — so the read path (`GetAdapterAsync`, `AdapterService`, the SignalR push to the
adapter) and the Studio edit path (`systemCommunicationAdapters.update` with `item.configuration`)
are untouched. It is **not** the `valueType: Secret` of (no encryption, no is-set read
state). Consequences: a fresh adapter still gets the seed value; a blueprint can no longer *change*
the configuration of an existing adapter (use a CK migration step or a documented manual step); an
exported adapter re-imported elsewhere starts without its configuration. Regression test:
`IntegrationTests/Repository/AdapterConfigurationOwnershipTests` (empty-skeleton seed, seed that omits
the attribute, fresh adapter, compiled ownership) against real MongoDB. It drives
`IImportRtModelCommand` with `Upsert`, the choke point of every blueprint apply; the orchestration
above it (Update/Merge modes, preview) is covered by the engine stories and the test-environment verification.
The long-term replacement is a dedicated configuration type with real `Secret` fields.

**Typed workload sizing: `CpuRequest`, `CpuLimit`, `MemoryRequest`, `MemoryLimit`, `LastDeploymentWarning`.** Five optional `String` attributes on the abstract `DeployableWorkload`
(`attributes/workloadResources.yaml`), inherited by `Adapter`, `Application` and `AdapterPool`, all
`isRuntimeState: true`. The four sizing attributes carry Kubernetes quantity strings (`250m`, `512Mi`);
`LastDeploymentWarning` is written by the controller (a later story of the Feature: an explicit Helm value
overrides a typed setting) and was added now so the Feature needs one CK Minor only. Runtime state, unlike
`PoolMemberCpuRequest` … `PoolMemberMemoryLimit` (`isRuntimeState: false`, the pool author's declaration):
operators set the sizing on live tenants, so a blueprint re-apply must keep it — also against a seed that
ships its own non-empty default (omitted / empty seed values are already guarded for seed-owned attributes
in the meantime, a *different non-empty* one is not). Additive Minor (ck-semver rule T1), no migration entry,
existing workloads have all five empty. The attributes reach the Helm release (see
"Typed sizing reaches Helm" below); a workload that sets none of them renders exactly what it rendered on 4.9.0.
Tests: unit `DeploymentSiteServiceTests/DeployWorkloadResourceAttributesTests` (schema on all three kinds,
unchanged `ValuesYaml` / `Values` for Adapter and Application) and integration
`Repository/WorkloadResourceAttributesReapplyTests` (compiled ownership on all three kinds; Upsert with an
omitted, empty and a different `MemoryRequest` keeps the operator's `512Mi`) against real MongoDB.

**Typed sizing is validated at deploy.** Workload entities have no write hook (they are written
through the asset repository), so `DeploymentSiteService.EnsureWorkloadIsHelmDeployableAsync` validates
the four sizing attributes via `WorkloadResourceSettingsValidator` right after the leasing check and before
any operator call: every non-blank value must be a Kubernetes quantity (`KubernetesQuantity`: `m`/`n`/`u`,
`k`..`E`, `Ki`..`Ei`, decimal exponent, plain and decimal numbers; negative values and `512 MB` are
rejected), and a request must not exceed its limit on the parsed values (`1024Mi` equals `1Gi`). An
`AdapterPool` must leave all four empty and use `PoolMemberCpuRequest` … `PoolMemberMemoryLimit` (one
sizing path per pool). A rejection throws a `DeploymentSiteServiceException` naming the attribute and the
value and, best effort, writes `DeploymentState` Error + `LastDeploymentError` (Studio shows it at the
field). Unit tests: `KubernetesQuantityTests`, `DeploymentSiteServiceTests/DeployWorkloadResourceValidationTests`.
The memory-limit rule for `ExecuteCSharp` on leased adapters must read the *effective* memory limit
(typed value, else explicit Helm value, else chart default).

**Typed sizing reaches Helm, explicit values win.** Precedence: **chart default < typed attributes
< explicit `ValuesYaml` < explicit `Values[]`** (then the `replicaCount` pin of a pool, unchanged). It is
built in the controller, in `WorkloadResourceOverrides.Apply`, called from
`BuildWorkloadDeployedDtoAsync` — *not* in the operator: a new values layer would need a
`WorkloadDeployedDto` change in `octo-sdk`, and edge clusters run older operators that would silently ignore
it. Each non-blank typed value becomes one more `Values[]` entry (`CpuRequest` → `resources.requests.cpu`,
`CpuLimit` → `resources.limits.cpu`, `MemoryRequest` → `resources.requests.memory`, `MemoryLimit` →
`resources.limits.memory`), added **only if neither `Values[]` (path match case-insensitive, presence counts)
nor `ValuesYaml` (a non-blank scalar at that key; invalid YAML counts as unset, helm reports it) already
sets that path**; empty typed attributes add nothing, so the chart default stays. When an explicit value
overrides a typed one (same quantity written differently, `1024Mi` vs `1Gi`, is not an override) the
deploy writes `LastDeploymentWarning` — `MemoryRequest 512Mi is overridden by an explicit Helm value
(ValuesYaml resources.requests.memory)`, several joined by `; ` — logs it as a warning and returns it:
`IDeploymentSiteService.DeployWorkloadAsync` returns `Task<string?>`, `POST deploymentsite/workloads/deploy`
answers `204` as before and `200 {"warning": "..."}` only when there is one (the SDK client accepts any
success status). A conflict-free deploy clears the attribute; it is written only when the value changes
(`ICommunicationRepository.SetWorkloadDeploymentWarningAsync`, best effort). Every path that builds the
deploy DTO goes through it — manual deploy, `ReconcilePendingWorkloadsAsync` (`IsReconciliation`), the
deploymentSite fan-out — so the typed values survive a controller restart and a pool re-registration. An
`AdapterPool` is untouched: it keeps `PoolMember*` (`AppendAdapterPoolMemberOverrides`); merging both sets
is a Major for CK v2. Tests: `DeploymentSiteServiceTests/DeployWorkloadResourcePrecedenceTests`,
`Controllers/PoolControllerDeployGateTests`.

**`DataPoint.ExternalId` is seed-owned again.** `ExternalId` carried `isRuntimeState: true` since
an earlier change, which was meant for the `MappingTargets` RecordArray (`MappingTarget.ExternalId`). `DataPoint` is the record
of seed-owned RecordArrays too - `Loxone/Control.States` and `SubControl.States` - and the export evaluates record
**members** one by one (`RtEntityToTcDtoConverter` re-enters `ConvertAttributes` for each record), so every exported
data point lost its `ExternalId`: its identity and a required member, the key the "Store Control States" pipeline
matches States by (`ExportRtByDeepGraph` / `ExportRtByQuery` of a Control exported only `Name`). Now
`ownership: SeedOwned`; `CurrentValue`, `LastUpdate` (live values) and `SourceIdentifier` stay runtime state, so the
file still carries no live values. No side effect on `MappingTargets`: it is runtime state as a whole and is skipped
as one unit by export and by re-apply (preservation inspects top-level type attributes only). MINOR
(`ck-semver-rules`: "Attribute ownership changed"), `octo-ckc ValidateVersion` VALID against the published 4.10.0, no
migration entry, no blueprint bump. The model is **not** published by the merge (run the CI pipeline with
`publishSystemCkModels=true`). Dependents: `Loxone-5.4.0` and `System.Ai-4.6.0` are range-retaining
(`System.Communication-[4.5,5.0)` / `[4.0,5.0)`; the `usedSurface` is a list of element ids, not content) and keep
resolving; a *classic* exact-pin dependent would go `ResolveFailed` on any minor and has to be re-pinned. Data written
by an older export (records without `ExternalId`) is accepted by an import without validation - the engine does not
check required members of a record - so such a tenant needs a re-discovery. Tests: integration
`Repository/DataPointExportOwnershipTests` (compiled ownership of the record members, the real converter over the real
cache with a seed-owned DataPoint RecordArray, `MappingTargets` still dropped as a whole) and
`Migrations/SystemCommunication411DependentsTests` (published 4.10.0 + Loxone 5.4.0 + System.Ai 4.6.0 + Basic 2.5.0
byte for byte from the public catalog, then the build's 4.11.0: all stay `Available`; negative control: the same Loxone
without its persisted ranges goes `ResolveFailed`). The export/import round trip of a record array with this ownership
layout is `Jobs.IntegrationTests/DataPointRoundTripTests` in octo-bot-services.

**`PipelineExecution.SealedCaller`.** One optional `String` attribute, `isRuntimeState: true`, holding the
sealed envelope of the queued invoker (see "The queued invoker is sealed"). Additive MINOR; the seven `Caller*`
attributes stay but are deprecated (neither written nor read). **Rollout:** the embedded-import guard brings 4.12.0 with
the restarted controller; the controller needs `SecretEncryption` (key ring) to store an invoker. A work item that was
queued before the upgrade has no valid `SealedCaller`: it is **failed at lease grant** ("not queued by
the controller"), not leased (see "Only executions the controller queued are leased"). Such items are rare (they wait
only for a pool member); a re-run restores them, and no migration is possible because nothing proves an old value was
written by the controller.
Dependents keep resolving (minor within the same major).

When adding a new attribute on `Adapter` / `Pool` / similar entities,
decide at creation time: is the value driven by the blueprint author
(configuration → leave `isRuntimeState` unset), or by services /
operators / users at runtime (status / counters / error history →
mark `isRuntimeState: true`). Once shipped, downgrading from `true` to
`false` is a behaviour-breaking change.
