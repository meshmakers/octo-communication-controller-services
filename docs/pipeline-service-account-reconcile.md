---
description: "Pipeline service account: credentials reaching the adapter pod, declarative reconcile, identity health and rights analysis"
applies_to: "**/Services/ServiceAccount*Service.cs, **/BackgroundServices/PipelineServiceAccountProvisioningRetry*.cs"
---

### Phase 4 — the credentials reach the adapter pod

Everything above provisions and rotates a credential the adapter had no way of *receiving*. The credential delivery
change delivers it: `octo-communication-sdk` gained `AdapterOptions.IssuerUri` / `ClientId` / `ClientSecret`
plus an `AdapterAccessTokenService` that logs in **before** the hub client connects, and this service
supplies the two values through the `ValueOverride[]` that already goes to the operator at deploy
time.

**The coupling to the chart, side by side.** These four rows are the whole contract, and a typo in
any of them is invisible until a pod is running — the adapter simply connects anonymously:

| Controller (`DeploymentSiteService`) | Operator | Chart (`octo-mesh-adapter/templates/_env.tpl`) | Adapter |
|---|---|---|---|
| `ServiceAccountClientIdValuePath` = `serviceAccountClientId`, `IsSecret=false` | literal value in `values-overrides.yaml` | `.Values.serviceAccountClientId` | `OCTO_ADAPTER__CLIENTID` |
| `ServiceAccountClientSecretValuePath` = `secrets.serviceAccountClientSecret`, **`IsSecret=true`** | materialised into `{release}-octo-secrets`, path rewritten to `valueFrom.secretKeyRef` | `.Values.secrets.serviceAccountClientSecret` through `octo-mesh.secretEnv` (same helper as `secrets.rabbitmq`) | `OCTO_ADAPTER__CLIENTSECRET` |
| *(nothing — deliberately)* | `authUri` from `OperatorOptions.AuthUri` | `.Values.authUri` | `OCTO_ADAPTER__ISSUERURI` **and** `OCTO_ADAPTER__AUTHORITYURL` |

The third row is Gerald's decision and worth keeping: `AuthorityUrl` (inbound — the issuer secured
`FromHttpRequest@2` routes accept) and `IssuerUri` (outbound — the identity service the adapter
authenticates *itself* against) are two configuration keys because `AdapterOptions` lives in the SDK
and must also serve adapters that have no `MeshAdapterConfiguration` (Loxone, Modbus, Zenon). They
always name the same identity service, so the chart feeds both from the one `authUri` value; a second
chart value could only ever drift. Nothing is projected for it here.

**Where the projection happens.** `DeploymentSiteService.AppendPipelineServiceAccountOverridesAsync`, called
from `BuildWorkloadDeployedDtoAsync` after the entity's own overrides are built. It reads the account
through `IPipelineServiceAccountResolver.GetAdapterDefaultAsync` — the adapter-wide default, not a
per-pipeline override: this is the *process* identity of the pod, and a per-pipeline account is about
one pipeline's execution.

- **Adapters only.** `Application`s execute no pipelines, connect to no adapter hub, and their CK
  type does not carry the association; they are skipped before any repository read.
- **Not gated on `ReceivesClusterSecrets`, deliberately.** That flag decides whether a workload gets
  the *cluster's shared* data-store credentials (MongoDB, CrateDB) — a pure edge adapter must not.
  These two values are the opposite kind of thing: the adapter's own, per-adapter, tenant-scoped
  identity, and an edge adapter needs it *more* than an in-cluster one, because it is the only
  credential it presents when dialling into the controller across the network. Same precedent as the
  RabbitMQ broker password, which is injected unconditionally because every workload needs the
  command bus.
- **Everything degrades to "no credentials", never to a failed deploy.** No account linked, a
  half-written entity (attributes are read through `GetAttributeValueOrDefault`, never the generated
  mandatory-attribute getters), a resolver that throws while the CK cache is being unloaded — each
  logs and returns the overrides unchanged. The result is a pod that connects anonymously, which is
  what the entire fleet does today.
- **A path the workload entity already pins wins.** `WorkloadOverrideYamlBuilder` is last-wins, so
  appending unconditionally would silently overrule a deliberate manual override.
- 🔴 **The secret reaches no log path.** Not verbatim, not truncated, not in an exception message.
  Pinned by `DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget`, which reconfigures NLog
  to a `MemoryTarget` and asserts on the **rendered** output.

#### 🔴 The ordering: provisioning moved BEFORE the deploy notification

Phase 2 of the mandatory-identity work provisions the adapter's account in `DeployWorkloadAsync` **after** the deploy
notification ("best effort, so the identity round trip cannot delay the helm rollout"), while
`BuildWorkloadDeployedDtoAsync` runs before it. That was free while the account was only ever read
back by a *later* pipeline deploy. It is not free any more: the deploy notification now carries the
credentials, and the DTO is built from what exists at that moment — so the **first** deploy of every
freshly created adapter would ship none, the pod would come up anonymous, and nothing would
re-deploy it. Once the adapter-hub gate is armed, such a pod never gets online at all.

Provisioning therefore now runs **before** `BuildWorkloadDeployedDtoAsync`, immediately after
`EnsureWorkloadIsHelmDeployableAsync`. The two rejected alternatives: triggering a second deploy after
a successful provisioning costs a second helm rollout and pod restart on every deploy and needs its
own loop guard; and documenting "the first deploy is anonymous" pushes a manual second click onto
every adapter creation. What is paid instead is latency on the deploy call — bounded by the
provisioning service's own 30 s identity timeout, normally milliseconds — and it is paid *before* the
rollout starts rather than during it, so a slow identity service delays a deploy instead of
half-configuring one. The best-effort contract is unchanged: a provisioning failure is still
swallowed and the workload still deploys, just without credentials.

The former pool fan-out `DeployManagedWorkloadsAsync` had no live caller and was removed with the secret-defaults change; every workload
deploy goes through `DeployWorkloadAsync`.

Phase 4 tests: `Services/PoolServiceTests/DeployWorkloadServiceAccountCredentialsTests` (both paths
projected with the right secret flags, Application projects nothing and never asks the resolver, no
account / half-written account / throwing resolver each deploy without credentials, an entity pin is
kept, `ReceivesClusterSecrets=false` still gets them, an encrypted attribute reaches the wire
decrypted, no secret in any NLog target, and a literal assertion that the two value paths match the
chart) plus `DeployWorkloadAsync_ProvisionsBeforeItBuildsTheDeployNotification` in
`DeployWorkloadServiceAccountProvisioningTests`.

### Phase 5 — declarative reconcile and identity health + hardened guard

**The declarative-account change** made the account declarative: `ServiceAccountConfiguration` (CK 3.32.0, additive)
carries `AssignedRoleNames` (null = legacy, roles unmanaged) and `AllowDelegation`, the backfill
became an idempotent reconcile (`ReconcileAdapterAsync` / `ReconcileConfigurationAsync` — never
rotates an existing secret), `IssuerUri` defaults to the portable `{{service.authority}}` token
resolved at projection time by the workload-template machinery, and REST endpoints (`POST
{tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile`, `POST
{tenantId}/v1/serviceAccount/reconcile?configurationRtId=…`, `POST
{tenantId}/v1/serviceAccount/{configurationRtId}/rotateSecret`) expose it; a user-initiated
reconcile only materialises roles when the caller holds `UserManagement`. See the XML docs on
`IPipelineServiceAccountProvisioningService` for the full contract.

**The read-side change** adds the read side and hardens the deploy guard.

**Identity-health aggregate** — `IServiceAccountHealthService` / `ServiceAccountHealthService`:

```
GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/health        (adapter-scoped)
GET {tenantId}/v1/serviceAccount/{configurationRtId}/health          (configuration-bound)
  → 200 ServiceAccountHealthDto { OverallStatus, ConfigurationRtId,
                                  ConfigurationWellKnownName, ClientId, Checks[],
                                  LinkedConfigurations[] }
```

Both `TenantCommunicationApiReadOnlyPolicy`. One check per concern, each with a machine-readable
name (`association` — adapter variant only, `configuration`, `client`, `secret`, `roles`,
`delegation`, `tenant`, `issuerUri`), a status (`Healthy` / `Violation` / `Unknown` /
`NotApplicable`), a violation `Code` (`association-missing`, `configuration-missing`,
`client-missing`, `secret-missing`, `roles-drift` — with `MissingRoles` / `SuperfluousRoles`
lists, `delegation-drift`, `tenant-mismatch`, `issuer-uri-drift`) and a human message naming the
reconcile as the fix. `OverallStatus`: any violation → `Unhealthy`, else any unknown → `Unknown`,
else `Healthy`. The secret check reports **presence only** — the value never leaves the entity.
Roles are only compared for declarative accounts (`AssignedRoleNames != null`); the issuer rule is
the sweep's own (`PipelineServiceAccountProvisioningService.IsIssuerUriHealthy`, shared so
endpoint and sweep cannot disagree). **Degrades instead of failing**: an unreachable identity
service turns the identity-backed checks (`client`, `roles`, `delegation`) into `Unknown`, never
into a 5xx or a violation.

**Controller→identity read** — `IIdentityClientReader` / `IdentityClientReader`. The bus carries
no identity *query* surface (only the create/converge command) and the SDK service client stays
rejected (second transport, no bootstrap credential — see phase 2). What every path that needs the
read *does* have is the calling user's bearer token, so the reader forwards it verbatim to
identity's REST API at `AuthorityUrl` (`GET {tenantId}/v1/Clients/{id}`, `…/{id}/roles` + `GET
…/Roles` to map role rtIds to the names the declaration speaks in) — the caller's own privileges
answer the question, no new service credential, no escalation. Identity's read endpoints accept
exactly the `octo_api` scope family the controller's own policies require. No ambient HTTP
request, 401/403/5xx, timeout → `IdentityClientLookupStatus.Unavailable`, which every consumer
treats as "unknown", never as "missing". Named `HttpClient` with a 10 s timeout (Program.cs).

**Hardened deploy guard** (`EnsurePipelineHasServiceAccountAsync`, both deploy paths): after the resolution check, (a) an account without a usable **client secret** is refused —
a local fact, `AdapterServiceException.PipelineServiceAccountSecretMissing` — **unless** impersonation stands in: an
unset secret passes when a capable impersonation actor exists (the adapter's own client with a usable secret, a
different client than the account); a secret that is stored but unreadable here (unknown key id, corrupt) is always
refused (`docs/pipeline-service-account-grants.md`); (b) the **identity
client's existence** is verified through the reader —
`AdapterServiceException.PipelineServiceAccountClientMissing` on an authoritative NotFound (or a
configuration naming no `ClientId` at all). Both messages name the reconcile endpoints and Studio
as the fix, in the style of the mandatory-identity work. Check (b) is gated by `ServiceAccountGuardOptions`
(`ServiceAccountGuard:CheckIdentityClient`, **default `true`**;
`OCTO_SERVICEACCOUNTGUARD__CHECKIDENTITYCLIENT=false` loosens an environment without a release —
the switch turns the whole identity round trip off, not just the refusal). 🔴 An **unreachable**
identity answer never blocks: warning log, deploy proceeds — identity downtime must not brick
pipeline deploys, and the adapter-side token request surfaces a real problem immediately anyway.

Phase 5 tests: `Services/ServiceAccountHealthTests` (all checks green on both variants, every
violation with its code, unlinked-but-existing entity, legacy roles opt-out, both delegation drift
directions, tenant mismatch naming the foreign tenant, issuer token case-insensitivity +
authority acceptance, identity-unreachable → Unknown aggregate, partial role-read degradation),
`Services/AdapterServiceTests/DeployPipelineServiceAccountHardenedGuardTests` (secret-missing and
client-missing refusals with remedy text on both deploy paths, secret check independent of the
option, option-disabled pass-through without any identity call, identity-unreachable
pass-through), `Controllers/ServiceAccountHealthEndpointTests` (routing, 404/400 mapping). The
integration fixture substitutes the reader with an `Unavailable` answer — it runs no identity
service, and the guard is non-blocking by contract.

### Phase 6 — rights analysis: touched types × data permissions

**`IServiceAccountRightsAnalysisService` / `ServiceAccountRightsAnalysisService`** answers "which
roles does this pipeline service account need?" — read-only, side-effect free, entirely from
tenant-local data:

```
GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rightsAnalysis      (adapter-scoped)
GET {tenantId}/v1/serviceAccount/{configurationRtId}/rightsAnalysis       (configuration-bound)
  → 200 ServiceAccountRightsAnalysisDto { AnalyzedPipelines[], ProtectedTypes[],
        DynamicTypeUsages[], Warnings[], DeclaredRoles[]|null, MissingRoles[],
        SuperfluousRoles[], BaselineRoles[], Message }
```

Both `TenantCommunicationApiReadOnlyPolicy`. Three data sources are joined:

1. **Touched CK types** — extracted from the pipeline set's `PipelineDefinition` YAML via the
   existing `IPipelineDefinitionService` (the debugger's dictionary-based YamlDotNet parser; a new
   `TryGetAllNodes` distinguishes unparsable from empty). The scan walks every node's properties
   **recursively** (association updates carry `targetCkTypeId`/`originCkTypeId` in nested update
   records) looking for `ckTypeId` / `ckTypeIds` / `targetCkTypeId` / `originCkTypeId`; container
   nesting (`ForEach@1`, `If@1`, …) is already handled by the parser's `transformations`
   recursion. A `*ckTypeIdPath` property or a non-literal value (template token, JSONPath) lands
   in `DynamicTypeUsages` — **reported as not statically analyzable, never silently dropped**.
2. **Data policies/permissions** — untyped `RtEntity` reads through the tenant
   repository: `GetDataPoliciesAsync` (all `System.Identity/DataPolicy`),
   `GetDataPermissionsForPoliciesAsync` (outbound `PolicyPermission`),
   `GetGrantingRolesForDataPermissionsAsync` (inbound `GrantsPermission` → `Role`). The
   controller references **no generated System.Identity model** — the four stable ids live in
   `Repository/SystemIdentityCkIds`. Type matching is on the `Model/Type` name part,
   **version-insensitively** (pipeline YAML is unversioned, policy targets are canonical
   `SemanticVersionedFullName` and may carry an element version — `NormalizeCkTypeId` strips it).
3. **The declaration** — `AssignedRoleNames`; `null` = legacy → recommendation without
   a delta.

Pipeline-set semantics follow the resolver: adapter-scoped = executing pipelines minus
those with their own override; configuration-bound = the adapter-default pipelines resolving to
it plus pipelines overriding **to** it (new reverse read `GetPipelinesUsingServiceAccountAsync`,
inbound over `Uses`; the resolver still decides which link is effective). Unprotected touched
types are **dropped** (no policy targets them → no role needed). `ProtectedTypes` is the
(type × policy) join — one entry per pair, with `OwnerScoped` flagged: 🔴 **D7 — an owner-scoped
(`OwnedOnly`) grant never counts as coverage** (a service account owns nothing); a type with only
owner-scoped grants is called out as blind in the summary. Delta: `MissingRoles` = full-scope
granting roles of uncovered types; `SuperfluousRoles` = declared roles granting nothing for any
touched type — except the provisioning baseline (`CommunicationManagement`,
`PipelineServiceAccountProvisioningService.DefaultAssignedRoleNames`, and since the declarative-account change
`StreamDataReader`, `…BaselineRoleNames`), which lands in
`BaselineRoles` instead (it grants no data access and every pipeline SA needs it). Robustness:
unparsable/empty definitions become `Warnings` entries, an empty pipeline set returns an
empty-but-valid result.

Phase 6 tests: `Services/ServiceAccountRightsAnalysisTests` (extraction incl. nested containers +
list/record recursion, dynamic-path and templated-id reporting, unprotected drop, versioned-target
matching, role join, owner-scope flag + blind-type handling, delta with baseline, legacy null
declaration, unparsable-YAML warning, empty set, both pipeline-set resolutions),
`Controllers/ServiceAccountRightsAnalysisEndpointTests` (routing, 404/400 mapping — same
pattern as the read side).
