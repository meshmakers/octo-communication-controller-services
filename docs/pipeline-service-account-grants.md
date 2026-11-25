---
description: "Pipeline service account: installation defaults, MayActAs, StreamDataReader baseline, role grants and Uses-linked accounts"
applies_to: "**/Services/ServiceAccountImpersonationSwitchService.cs, **/Services/UsesLinkedServiceAccountLookup.cs, **/Controllers/ServiceAccountController.cs"
---

### Phase 7 — installation defaults and MayActAs materialisation

**Issuer fields** (CK **3.33.0**, minor): `IssuerUri`, `TenantId` and `ClientSecret` on
`ServiceAccountConfiguration` became **optional** — `isOptional` sits on the type's attribute
*references*, so the shared definitions (`ClientId`/`ClientSecret` with FinApi/MicrosoftGraph,
`${System}/TenantId` platform-wide) stay mandatory everywhere else. Empty now MEANS something,
mirrored by the adapter (octo-mesh-adapter `ServiceAccountTokenService`, commit acff5ac): empty
`IssuerUri` = the adapter's own installation, empty `TenantId` = the tenant the adapter runs for,
empty/`<placeholder>` `ClientSecret` = "no secret — impersonate instead". The reconcile
**creates new accounts with issuer and tenant EMPTY** and converges the two historic installation
spellings to empty — the `{{service.authority}}` token (case-insensitive) and this installation's
concrete `AuthorityUrl` (trailing-slash-insensitive), plus a `TenantId` naming the current tenant.
Any **other** concrete issuer/tenant is left alone by the reconcile (this *inverts* the earlier
"converge foreign issuers onto the token" rule and retires the "always rewrite TenantId"
rule) — but since **AB#9776 (own issuer only, Reimar 2026-11-24)** it is no longer a supported
target: the mesh adapter sends service-account grants only to its own identity service and refuses
a foreign issuer or a tenant other than the pipeline's on every execution, the deploy guard refuses
such a pipeline and health reports it as a violation (below). One rule decides "own" for reconcile, health and
deploy alike: `ServiceAccountOwnIssuer` (`OwnNames`, `IsOwnIssuer`, `IsOwnTenant` - the token in any spelling, the
own `AuthorityUrl` and the resolved `ServiceUrls` authority with case, default-port and trailing-slash variants, the
tenant without case and blanks). Helpers on `PipelineServiceAccountProvisioningService`: `ConvergeIssuerUri`,
`ConvergeTenantId` (both built on that rule), `IsSecretUsable` (byte-for-byte the adapter's rule: empty or leading `<` = no secret). The
projection passes an empty `IssuerUri` through EMPTY; token resolution remains only for legacy
entities that still carry it. The secret transition stays dual-path: the reconcile still issues a
secret where none exists — an impersonation-only account is authored, never generated.

**v1** — the controller declares which clients may impersonate a pipeline service
account: `DistClientDto.MayActAsClientIds` (octo-common-services; additive + idempotent
identity-side, unknown actors skipped with a warning, `null` = no edge changes, removal stays a
v1 non-goal). The **adapter's own client** is the adapter-default account's client — exactly the credentials in the pod's Helm values — so the adapter path never declares actors (a
self-edge authorizes nothing); the **standalone** branch of `ReconcileConfigurationAsync` resolves
its actors via `GetPipelinesUsingServiceAccountAsync` → `GetAdapterByPipelineAsync` → the adapter
defaults' `ClientId`s (deduplicated, sorted, self excluded; no actor resolvable → `null` + info
log, never a failure). Rotation sends `null` — like roles, edges are never touched by a rotation.

**Health** — new check `impersonation`: `NotApplicable` with message when the account has a
usable secret, when nothing links a standalone account to an adapter, or when no capable actor
exists (the adapter-scoped variant is *always* one of these — it evaluates the adapter's own
account, which cannot impersonate itself); **`Unknown`** when a capable actor exists, because 🔴
**the MayActAs edge is not verifiable controller-side**: the identity REST surface the
`IIdentityClientReader` uses (`GET {tenantId}/v1/Clients/…`) exposes clients and role edges but
**no client-to-client associations** — the edge lives in `System.Identity/MayActAs` and only the
identity token endpoint consults it (`ClientImpersonationStore`). The `secret` check became
credential-aware: empty is a `Violation` (`secret-missing`) **only when no impersonation is
possible**; with a capable actor it is `Healthy` with a message naming that actor. `issuerUri` /
`tenant` report `Healthy` with "installation default" when empty; the token/own-authority/current
tenant spellings stay healthy as legacy (message says the next reconcile empties them). **AB#9776:**
any other issuer is a violation `issuer-foreign` (the `issuer-uri-drift` code stays retired), and any
tenant other than the current one (case and surrounding blanks ignored) is a `tenant-mismatch`
violation, whatever the issuer — there is no healthy foreign pairing any more.

**Deploy guard** — refuses `PipelineServiceAccountSecretMissing` only when the account has
neither a usable secret NOR a capable impersonation actor (adapter default with usable secret,
different client) — both local facts; the edge itself stays best-effort exactly like the
client-existence check (identity unreachable never blocks), and the refusal message names both
repairs. **AB#9776 (own issuer only)**, checked first: `PipelineServiceAccountForeignIssuer` when
`IssuerUri` is not empty, not the `{{service.authority}}` token (any case and spacing) and not an own
name (`AuthorityUrl` or what `{{service.authority}}` resolves to via `ServiceUrls`), compared after
normalisation (`ServiceAccountOwnIssuer`: scheme/host case, default port, trailing slash; user info,
query, fragment and non-http schemes never match); `PipelineServiceAccountForeignTenant` when
`TenantId` is not empty and not the pipeline's tenant. The adapter enforces the same rule at run time
(`OwnIssuerPolicy` in octo-mesh-adapter), so this is the explicit message, not the only barrier.

Phase 7 tests: `Services/PipelineServiceAccountReconcileTests` (+ `…ProvisioningServiceTests`,
`…RotationTests`) — create-with-empty, the full convergence matrix (token incl. case, own
authority incl. trailing slash, current tenant → empty; foreign issuer/tenant untouched incl.
no-write), MayActAs on the wire (adapter path null, standalone actors deduplicated, unused
standalone null); `Services/ServiceAccountHealthTests` — installation-default messages,
credential-aware secret, all four impersonation states, foreign issuer/tenant as violations (AB#9776),
placeholder secret; `…/DeployPipelineServiceAccountOwnIssuerTests` — AB#9776 foreign issuer and
look-alikes, foreign tenant refused before any state write, own spellings still deploy;
`Services/AdapterServiceTests/DeployPipelineServiceAccountHardenedGuardTests` — the
matrix (capable actor deploys incl. placeholder target secret, no/incapable actor refused with
remedy, identity-down non-blocking); `…/PipelineServiceAccountProjectionTests` — empty issuer
passes through empty.

### Phase 8 — `StreamDataReader` baseline

The asset repository gates stream-data reads on `StreamDataReader`, `StreamDataWriter` or
`StreamDataAdmin` (`StreamDataReadAuthorization:RoleEnforcement` Off / Warn / **Enforce**,
error code `StreamDataReadRoleRequired`). Pipelines that read archives (allocation, billing,
simulation) run as the adapter's pipeline service account, which held only `CommunicationManagement`
— under `Enforce` they were refused.

`PipelineServiceAccountProvisioningService.BaselineRoleNames` (`[StreamDataReader]`) is therefore
added to every **declarative** account's roles when the declaration is materialised
(`EffectiveRoleNames` = declared roles in their order + every baseline role not declared yet).
Rules:

- **Not persisted.** `AssignedRoleNames` stays what the operator (or the default
  `DefaultAssignedRoleNames = [CommunicationManagement]`) wrote. Existing accounts get the role on
  their next **system-initiated** reconcile (tenant start / `clearCache`, workload deploy) — no data
  migration, no manual step, the entity is not rewritten.
- **Added, never replacing.** Declared roles are sent as before; a declaration that names
  `StreamDataReader` itself sends it once. An empty declaration still carries the baseline — the
  baseline cannot be removed through the declaration.
- **Same security gate.** A user-initiated reconcile without `UserManagement` sends no roles at all,
  baseline included.
- **Legacy accounts are not covered.** An account without `AssignedRoleNames` (pre-CK 3.32.0) keeps
  `null` on the wire, so identity leaves its role edges alone — grant `StreamDataReader` by hand
  (`octo-cli AddClientToRole --clientId octo-pipeline-sa-<adapterRtId> --roleName StreamDataReader`)
  or opt the account into declarative management, before the asset repository goes to `Enforce`.
- **Identity side.** The roles travel over the bus (`CreateIdentityDataCommandRequest`); its consumer
  syncs the declared list for `octo-pipeline-sa-` clients and does not run the HTTP `RoleChangeGuard`, so the controller's own roles do not limit what it can assign here.
- **Read side follows.** The health check expects declared ∪ baseline (a missing `StreamDataReader`
  edge is `roles-drift` until the next reconcile); the rights analysis counts the baseline as
  coverage and reports a declared `StreamDataReader` under `BaselineRoles`, never as superfluous.

Tests: `Services/PipelineServiceAccountReconcileTests` (existing default declaration gets the role
without an entity write, declaration naming the baseline sends it once, empty declaration keeps the
baseline, unprivileged user sends no roles, `EffectiveRoleNames`),
`Services/PipelineServiceAccountProvisioningServiceTests.Provision_FreshAdapter_AssignsCommunicationManagementAndStreamDataReader`,
`Services/ServiceAccountHealthTests.GetConfigurationHealth_BaselineEdgeMissing_ReportsStreamDataReaderMissing`,
`Services/ServiceAccountRightsAnalysisTests` (`…DeclaredStreamDataReader_IsBaselineNotSuperfluous`,
`…TypeGrantedByTheBaseline_IsNotMissing`).

### Phase 9 — roles identity grants by design

The identity service grants `FileManagement` to every `octo-pipeline-sa-*` client on its own
(`CreateIdentityDataCommandRequestConsumer` adds the edge on every identity-data command and
treats it as implicitly declared, `PipelineServiceAccountFileRoleGrant` adds it at every tenant setup;
`octo-identity-services/docs/persistence.md`). Phase 8's health check did not know this, so every
adapter account showed `roles-drift` with `FileManagement` superfluous.

`PipelineServiceAccountProvisioningService.IdentityGrantedRoleNames` (`[FileManagement]`), next to
`BaselineRoleNames`, is the single list of these roles; `IdentityGrantedRoleNamesFor(clientId)`
returns it for a client with `ClientIdPrefix` (`octo-pipeline-sa-`, ordinal like identity) or a
not-yet-derived id, and nothing for a custom client id. Rules:

- **Never sent.** The roles are not part of `EffectiveRoleNames`; the controller neither sends nor
  removes them.
- **Tolerated, not required.** The health check excludes them from `SuperfluousRoles` but never
  reports them missing — identity grants `FileManagement` only when the role is seeded (System.Files
  installed). Every other undeclared role is still `roles-drift`.
- **Same answer in the rights analysis.** They count as coverage, and a declared `FileManagement` is
  reported under `BaselineRoles`, not as superfluous. On a custom client id both views treat
  `FileManagement` like any other role.
- **Keep in sync** with identity: a new identity-side grant for pipeline service accounts goes into
  this list in the same change.

Tests: `Services/ServiceAccountHealthTests` (`…PipelineAccountWithIdentityGrantedFileManagement_IsHealthy`,
`…PipelineAccountWithoutFileManagement_IsHealthy`, `…PipelineAccountWithUnexpectedRole_ReportsOnlyThatRole`,
`…CustomClientIdWithFileManagement_ReportsItSuperfluous`), `Services/ServiceAccountRightsAnalysisTests`
(`…PipelineAccountDeclaringFileManagement_IsBaselineButUnexpectedRoleStaysSuperfluous`,
`…TypeGrantedByIdentityGrantedFileManagement_IsNotMissing`, `…CustomClientIdDeclaringFileManagement_IsSuperfluous`),
`Services/PipelineServiceAccountReconcileTests.IdentityGrantedRoleNamesFor_PrefixedOrDerivedClient_IsFileManagementAndNeverSynced`.

### Phase 10 — Uses-linked accounts are reconciled automatically

Until the standalone-account change the automatic paths reconciled only the adapters' own accounts. Standalone
`ServiceAccountConfiguration`s that pipelines link via `Uses` (per-pipeline overrides a blueprint ships, e.g.
`accounting-ai-prompt`, `accounting-pipeline-reader`) were reconciled only by
`POST {tenantId}/v1/serviceAccount/reconcile?configurationRtId=…`. A blueprint ships them without a client id, so
after `InstallBlueprint`/`UpdateBlueprint` `DeployDataFlow` was refused ("identity client '' … does not exist"),
and the adapter health still answered `Healthy`.

- **Discovery** — `UsesLinkedServiceAccountLookup.FindAsync(repository, tenantId, adapterRtId?)`: every
  configuration of the tenant that no adapter owns (`GetAdapterForServiceAccountAsync == null`) and that at least
  one pipeline uses (`GetPipelinesUsingServiceAccountAsync`); with an adapter, only those used by a pipeline that
  adapter executes. It walks configurations → pipelines, not pipelines → `Uses` targets, because those load as the
  base `RtConfiguration` and a type test never matches (see `AdapterService.CreatePipelineConfigurationAsync`).
  Unused accounts are left to the manual endpoint.
- **Reconcile** — `IPipelineServiceAccountProvisioningService.EnsureLinkedConfigurationsProvisionedAsync(tenantId,
  adapter?)` runs the reconcile of `ReconcileConfigurationAsync` with the `System` context (roles are materialised)
  per account, isolated and non-throwing: a failure is logged, stored as a tenant error event naming the account and the manual
  endpoint, and reported in `Failures`. Three differences to the manual endpoint: (a) the passes are serialised per
  account in the process (`SemaphoreSlim` per tenant and rtId) and the account is re-read under the guard
  (`GetServiceAccountByRtIdAsync`), so overlapping passes reuse a secret the previous one saved; across controller
  instances the next pass heals a lost race, because it re-sends the stored secret; (b) an account with a foreign
  `IssuerUri` or `TenantId` is skipped, because its client lives in another installation or tenant;
  (c) **the automatic paths never re-key an account that already declares a `ClientId`** (decided 2026-11-15). An account without a stored secret that is on impersonation, that declares a `ClientId`, or whose
  impersonation actors could not be looked up this pass (`ResolveImpersonationActorClientIdsAsync` failed, or
  `IsImpersonationCapableAsync` answered `null`) is sent with **no** secret, so identity keeps the one it has
  (`CreateIdentityDataCommandRequestConsumer` keeps the existing secret when none arrives). Reason: a pending
  impersonation switch is not persisted, so "client waiting for `InvalidateSwitchedSecretAsync`" cannot be told apart
  from "client never created", and cutting a running credential short is the worse failure. Only an account without
  a `ClientId` (the blueprint shape) that cannot impersonate gets a fresh stored secret; every other fresh secret
  comes from the manual reconcile (v1 behaviour: throwaway secret, a failed lookup counts as not capable) or the
  rotate endpoint. A `CommunicationRepositoryException` with an inner exception from `GetAdapterByPipelineAsync` is a
  failed query, not "pipeline not assigned": the actor lookups propagate it (verdict unknown), only the plain refusal
  counts as no adapter.
  `EnsureTenantProvisionedAsync` calls it **after** the adapter sweep (the adapters' own clients are the accounts'
  `MayActAs` actors) and adds its counts to the report, so blueprint apply (`BlueprintAppliedConsumer`) and tenant load (`DefaultConfigurationCreatorService`) cover them without code of their
  own; the early "no adapters" exit is gone. `DeploymentSiteService.DeployWorkloadAsync` calls it for the deployed
  adapter right after `EnsureAdapterProvisionedAsync`. No background retry: a transient
  identity failure is healed by the next tenant load, blueprint apply or workload deploy, or by the manual endpoint.
- **Health** — the adapter-scoped `GET …/adapter/{adapterRtId}/serviceAccount/health` evaluates each Uses-linked
  account of the adapter's pipelines like the configuration-bound variant, but with **this** adapter's own client as
  the impersonation actor (an account shared by several adapters is judged per adapter; if that actor cannot be looked
  up, the `secret` and `impersonation` checks answer `Unknown`, never a violation; the exception is logged, the REST
  answer carries a generic text), and returns them in
  `ServiceAccountHealthDto.LinkedConfigurations`. The check `linkedConfigurations` rolls them up: `Violation`
  (`linked-configuration-unhealthy`, names the accounts) when one is unhealthy, `Unknown` when one could not be
  evaluated or the lookup failed, `NotApplicable` when no pipeline links an account of its own, else `Healthy`.

Tests: `Services/PipelineServiceAccountLinkedConfigurationTests` (install shape, update of the declaration, adapter
first, unused/adapter-owned skipped, no adapters, isolation, lookup failure, adapter scope, impersonating account
keeps its identity secret, actor lookup failure keeps it too, declared ClientId without actor is not re-keyed, new
account without actor gets a stored secret, wrapped adapter lookup failure is not "no actor", unassigned pipeline
is, foreign target skipped, re-read entity wins, deleted meanwhile),
`Consumers/BlueprintAppliedConsumerTests` (`BlueprintInstall_…`, `BlueprintUpdate_…`, real provisioning service),
`DefaultConfigurationCreatorServiceTests/EnsurePipelineServiceAccountsAsyncTests.TenantLoad_…`,
`DeploymentSiteServiceTests/DeployWorkloadServiceAccountProvisioningTests` (order, Application skipped),
`Services/ServiceAccountHealthTests` (`GetAdapterHealth_UsesLinked…`, `…LinkedLookupFails_DegradesToUnknown`,
`…SharedSecretlessLinkedAccount_IsJudgedWithEachAdapterAsActor`, `…LinkedActorLookupFails_SecretAndImpersonationAreUnknown`),
integration `Repository/UsesLinkedServiceAccountLookupTests` (real MongoDB graph reads). Shared fixture:
`Helper/UsesLinkedServiceAccountFixture`.
