---
description: "Pipeline service account: mandatory execution identity, provisioning, roles and deliberate secret rotation"
applies_to: "**/Services/PipelineServiceAccount*.cs, **/Options/ServiceAccount*.cs"
---

## Pipeline Service Account — mandatory execution identity (phases 1 + 2)

**Background retry of failed provisioning.** The startup sweep (`EnsureTenantProvisionedAsync`) stays
single-shot: one `CreateIdentityDataCommandRequest` per adapter with a 30 s timeout. When identity restarts at the
same time as the controller (a release) that request times out or has no route, and there is no further tenant load
to repair it. `PipelineServiceAccountProvisioningService` (singleton) therefore remembers adapters that failed with
`IdentityUnavailableException` (identity did not answer) and `PipelineServiceAccountProvisioningRetryBackgroundService`
ticks it every 10 s (`RetryPendingAsync`). Backoff doubles from `PipelineServiceAccountRetryInitialDelaySeconds` (30)
up to `PipelineServiceAccountRetryMaxDelayMinutes` (5), at most `PipelineServiceAccountRetryMaxAttempts` (60) retries.
An entry is dropped on success (one Information log + one tenant information event), when the adapter or tenant is
gone, on a permanent refusal (identity answered with an error result: not retried, tenant error event as before) and
when the budget is spent. Each failed attempt logs one Warning with the next retry time. State is process-local.

Pipeline execution runs under a real identity instead of anonymously. Granularity:
**one service account per adapter as the default, optionally overridden per pipeline**;
a pipeline without a resolvable account is refused at deploy time.

**CK model (3.31.0, additive minor).**

| Change | File | Why |
|---|---|---|
| New association role `PipelineServiceAccount` (`PipelineServiceAccountOf` ← N / `PipelineServiceAccount` → ZeroOrOne) | `ConstructionKit/associations/pipelineServiceAccount.yaml` | Dedicated role, **not** the generic `Uses`. A second origin type on `Uses` flips the inverse `UsedBy` view on `Configuration` and the CK engine then drops `SystemConfigurationInterface` from the generated GraphQL schema for every `Configuration` subtype — the exact failure already documented on the `HelmRepository` link in `types/deployableWorkload.yaml`. |
| `Adapter.PipelineServiceAccount → ServiceAccountConfiguration` | `ConstructionKit/types/adapter.yaml` | Declared on `Adapter`, not on `DeployableWorkload`: only Adapters execute pipelines. |
| `ClientSecret` marked `isRuntimeState: true` | `ConstructionKit/attributes/finApiConfiguration.yaml` | See below. |

**Multiplicity is `ZeroOrOne`, i.e. optional — deliberately.** The obligation ("every mesh
adapter MUST have a service account") is enforced by the deploy guard, not by the model.
A mandatory `One` would (a) reject every existing `Adapter` entity on the next
blueprint re-apply / `ImportRt`, before any tenant has an account at all and before the
provisioning phase exists, and (b) reclassify the change as a **major** CK bump
(`octo-construction-kit-engine/docs/ck-semver-rules.md`: a new association referencing a
role with multiplicity `One` is Major). No migration script is needed — the engine's
no-migrations bridge synthesises a schema-only no-op step for purely additive bumps, so
`migration-meta.yaml` stays at 3.1.1.

**No blueprint bump (at that CK version).** `System.Communication.{Release,MainLatest}` kept `1.5.0` and their
`ckModelDependencies` floor `[3.22.0,4.0)` then (the blueprints have since moved on: `2.0.0` with `[4.0,5.0)` for the
model 4.0.0 migration, now `2.1.0` with `System.Communication-[4.5,5.0)`): their seed creates neither a
`ServiceAccountConfiguration` nor the new edge, and the floor is a satisfiability floor
only — the install target is the embedded `IServiceManagedCkModelDescriptor` version.
The CK bump alone is what carries the change to tenants.

**`ClientSecret` is runtime state now.** A client secret is issued by the identity provider
(or pasted in by an admin) *after* the entity exists — a blueprint can only ever seed a
placeholder, and before the marker every re-apply overwrote the live secret with it.
Blast radius: the attribute definition is **shared**, so `ServiceAccountConfiguration`,
`FinApiConfiguration` and `MicrosoftGraphConfiguration` all keep their secret across a
re-apply. Correct for all three; the consequence is that a blueprint can no longer *change*
a `ClientSecret` on an existing tenant — rotate through Studio / the provisioning path.
First install is unaffected: preservation only rewrites an incoming value when the target
entity already exists **and** already holds a value for the attribute
(`ImportRtModelCommand.PreserveAttributesForEntity`), so a fresh tenant still gets the
seeded value. The marker needs the version bump to land — a same-version re-import is a
no-op (`ImportCkModelAsync` short-circuits; see the `IsDebuggingEnabled` 3.22.0/3.23.0
cautionary tale above). Explicitly **not** marked: `IssuerUri` and `ClientId` are
per-environment author configuration a blueprint must be able to correct;
`TenantId` is `${System}/TenantId` and can only be marked in the System model.

**Resolution** — `IPipelineServiceAccountResolver` / `PipelineServiceAccountResolver`
(singleton, registered next to the capability service in `Program.cs`):

1. the pipeline's own `ServiceAccountConfiguration` on the generic `Uses` role — the
   per-pipeline override. Needs no model change; Pipeline→Configuration already exists.
   Several linked accounts are picked by ordinal `RtId` so every pod and every redeploy
   agrees; a warning is logged.
2. otherwise the executing adapter's `PipelineServiceAccount` edge
   (`ICommunicationRepository.GetServiceAccountForAdapterAsync`).
3. otherwise `PipelineServiceAccountResolution.Unresolved`.

`ResolveAsync` takes an already-known adapter (the deploy paths have it);
`ResolveForPipelineAsync` walks the `Executes` edge itself and treats "pipeline has no
adapter" as unresolved rather than an error — that condition belongs to the deploy paths.

**Projection into the pipeline configuration.** Configurations reach a pipeline
*exclusively* through that pipeline's own `Uses` edges: `GetConfigurationsByPipelineAsync`
traverses `Uses` outbound from the pipeline, `CreatePipelineConfigurationAsync` turns the
result into `ConfigurationDto`s keyed on `RtWellKnownName`, and the adapter materialises
that into a per-pipeline `GlobalConfiguration` dictionary. There is no adapter-level
configuration scope, so the adapter-wide default has to be **mixed in controller-side** —
done in `CreatePipelineConfigurationAsync` (which now takes the adapter's `RtId`), only
when the pipeline has no service account of its own, and skipped when the same `RtId`
**or** the same `RtWellKnownName` is already present (a duplicate key throws on the adapter).
No SDK/wire change and no new DTO shape, hence no adapter/controller version skew.
**The adapter caches `GlobalConfiguration` at pipeline registration — changing the linked
service account only takes effect after the pipeline / data flow is redeployed.**

**Deploy guard** (`AdapterService.EnsurePipelineHasServiceAccountAsync`, modelled on the gate): called from `DeployPipelineAsync` and `DeployDataFlowAsync`, in both cases
**before the first state write**, so a rejected deploy leaves nothing half-applied. It
throws `AdapterServiceException.PipelineHasNoServiceAccount` — deliberately the same
exception family as the gate (→ HTTP 404 in `PipelineController`, not 400): both
gates must surface identically in the Studio, which renders `ErrorResponse.ErrorMessage`
regardless of status. The message names cause, work item, pipeline, adapter and **both**
ways out (link the account on the adapter, or set a per-pipeline override).

**Not guarded in `DeploymentSiteService.DeployWorkloadAsync`** (documented in place next to the lifecycle validation): that method also validates `Application`s, which execute no
pipelines; the helm deploy of the adapter pod is the step that must succeed *before* a
service account can be provisioned onto it, so gating it would be circular and would make
every existing tenant's adapter undeployable ahead of the provisioning phase; and an
adapter with no pipelines is harmless. Enforcement stays tight either way — no pipeline
reaches an adapter without a resolvable identity. Phase 2 does the *opposite* on that path:
`DeployWorkloadAsync` **provisions** the adapter's account (best effort, after the deploy
notification) instead of gating on it — see below.

Phase 1 tests: `Services/PipelineServiceAccountResolverTests` (override wins, adapter default,
nothing linked, no adapter query when an override exists, deterministic multi-link pick,
`ResolveForPipeline` via `Executes`, pipeline without adapter),
`Services/AdapterServiceTests/DeployPipelineServiceAccountGateTests` (rejection message
contents, negative proof that neither the adapter nor any state write is reached, data-flow
path, both happy paths), `Services/AdapterServiceTests/PipelineServiceAccountProjectionTests`
(default projected exactly once, no duplicate when already linked, untouched when the
pipeline has an override, coexistence with other configurations).
`AdapterServiceTestsBase` arranges a provisioned tenant by default
(`DefaultAdapterServiceAccount` + `DefaultAdapterServiceAccountDto`) so the pre-existing
suites keep testing what they were written for; gate tests re-stub the repository to null.

### Phase 2 — provisioning (must ship together with phase 1)

🔴 **Phase 1 alone bricks every tenant.** Before this work nothing on the platform created a client
*secret*: dynamic client registration deliberately produces public clients
(`RequireClientSecret=false`), the operator's `CreateSecret` paths are Kubernetes secrets, and client
mirroring only copies. So without phase 2 the deploy guard refuses **every** pipeline deploy in
**every** tenant. Never release one without the other.

**`IPipelineServiceAccountProvisioningService` / `PipelineServiceAccountProvisioningService`**
(singleton, registered next to the resolver in `Program.cs`). Per adapter, in this order:

1. Resolve the adapter's linked account; if there is none, look one up by the **deterministic
   well-known name** `pipeline-service-account-{adapterRtId}` — that is what makes a second run
   adopt its own earlier work instead of creating a second credential entity (and what repairs a
   lost edge). The rtId, not the name, is the key: names are editable.
2. Decide the secret. An existing configuration with a **usable** secret (or one that impersonation stands in for)
   **and** a client id and a well-known name is complete and contributes its own plaintext. Empty issuer and tenant
   id are the canonical defaults and do not make it incomplete; a concrete foreign value is a deliberate target and
   is preserved. Otherwise a fresh 384-bit URL-safe secret is generated with `RandomNumberGenerator` - except that an
   automatic pass sends none for an account that already declares a `ClientId` (identity keeps its current secret;
   a fresh one comes only from the manual reconcile or the rotate endpoint).
3. Send `CreateIdentityDataCommandRequest` with that one client — **every pass**, not only the first.
   Re-sending the *same* plaintext hashes to the same value identity-side, so nothing rotates, while
   grants / scope / roles converge for a client created before this code, and a client someone
   deleted underneath us is recreated with the credential the adapter still holds.
4. Write the `ServiceAccountConfiguration` entity and the `PipelineServiceAccount` edge in one
   transaction (`ICommunicationRepository.SavePipelineServiceAccountAsync`) — unless step 2 found a
   complete, linked, current configuration, in which case nothing is written at all.

**Why the bus and not the identity REST API.** `ClientsController` already accepts a plaintext
`ClientSecret` and hashes it — but calling it needs an `octo_api` bearer token, i.e. a
client-credentials identity, which is exactly what is being created. No bootstrap client with a
secret is seeded anywhere (`System.Identity.Bootstrap` has only authorization-code and device-code
clients), so the REST route is circular. It would also add a `Meshmakers.Octo.Sdk.ServiceClient`
package reference and a second identity transport to a service that already has exactly one:
`ICommandClient<CreateIdentityDataCommandRequest>`. Cost of the bus route: three optional properties
on the shared `DistClientDto` in octo-common-services (`ClientSecret`, `RequireClientSecret`,
`AssignedRoleNames`) — additive, defaulting to the pre-existing behaviour, so every other producer is
unchanged. **Deployment order: identity first, then the controller**; an identity that predates the
change ignores the new fields and would create a secretless client.

🔴 **The command client is scoped.** `ICommandClient<T>` wraps MassTransit's `IRequestClient<T>`,
which is **scoped**; this service is a singleton consumed by the singleton `DeploymentSiteService`, so it
resolves the client per call from a fresh scope (the `CommunicationEventService` pattern).
Constructor-injecting it fails DI validation at startup.

**What the client looks like:** `AllowedGrantTypes = [client_credentials, on-behalf-of URN]`,
`AllowedScopes = [octo_api, ai.llm.invoke]`, `RequireClientSecret = true`, `AllowOfflineAccess = false`.
`ai.llm.invoke` (`Constants.LlmGatewayInvokeScope`) is the scope of the AI service's central-key LLM gateway: the
mesh adapter calls the gateway with the account's token, and a token carries the scope only when the adapter asks for it.
It does not open the gateway by itself: the gateway still refuses every tenant without `KeySource=CentralKey`, a monthly
cap and the central key in its configuration. No user-facing client gets the scope (identity's
`System.Identity.Bootstrap` 1.10.0 registers it and grants it to none of its clients). The list is re-sent on every
reconcile, so existing accounts get it on the next tenant start; the order towards identity does not matter (a scope
identity does not know yet only fails a request that asks for it). The
delegation URN (`Constants.OnBehalfOfGrantType`) is seeded **now** even though the delegation flow is not live:
Duende gates its extension-grant validators on the client's own `AllowedGrantTypes`, so adding it
later would mean touching every already-provisioned tenant.

**Triggers.** Two, both idempotent:

| Trigger | Where | Covers |
|---|---|---|
| Tenant-wide sweep | `DefaultConfigurationCreatorService.StartTenantAsync` → `EnsurePipelineServiceAccountsAsync`, right after `ApplyServiceManagedBlueprintsAsync` | The **backfill** for existing tenants (service start, Enable, and `PosUpdateTenant` — i.e. the documented `clearCache` recovery lever), and the blueprint-seeded default Adapter on a fresh tenant. |
| Per adapter | `DeploymentSiteService.DeployWorkloadAsync`, after the deploy notification, for `RtAdapter` only | An adapter an operator adds between two tenant loads. There is no adapter-*create* path in this service (adapters are RtEntities written through the asset repository), and nothing runs pipelines before its workload is deployed, so this is the earliest point that matters. |

**Fault tolerance is the point of the backfill.** `EnsureAdapterProvisionedAsync` never throws: it
logs an Error **and writes a persistent Error event into the tenant's event log**, so the refusal an
operator later sees on a pipeline deploy has a visible cause instead of only a pod log line. One
adapter's failure does not stop the others; a failing adapter lookup (CK cache being unloaded during
a tenant update) is reported, not thrown; and `EnsurePipelineServiceAccountsAsync` wraps the whole
thing again so tenant startup — adapters, pools, trigger schedules — completes regardless.

### 🔴 Roles: an under-privileged service account fails **silently**

The controller's own endpoints authorize on the **`octo_api` scope**, not on a role — every policy in
`Program.cs` is a `RequireClaim` on the scope claim, so `CommunicationManagement` is *not* required
for the deploy calls a pipeline makes back into this service. It is granted anyway, because of what
comes next.

For the delegation case the issued token carries the **intersection** of the service
account's roles and the calling user's roles. A service account without the tenant's *business* roles
(e.g. the Accounting roles) therefore produces an **empty intersection** — and identity treats an
empty intersection as a **success**, not an error: the token is issued, it simply carries no `role`
claim, and every role-gated consumer fails closed. The symptom is a chat that goes quiet, an export
that returns nothing, a pipeline that "does nothing" — with **no error anywhere**.

So when you set up delegation for a tenant, grant the pipeline service account
(`octo-pipeline-sa-{adapterRtId}`, one per adapter) the fachliche roles its pipelines must act
under, on top of `CommunicationManagement` (and `StreamDataReader`, which the reconcile adds to every
declarative account, see phase 8):

```
octo-cli AddClientToRole --clientId octo-pipeline-sa-<adapterRtId> --roleName <Role> --context <tenant>
```

(or Refinery Studio → Identity → Clients → Roles). Role names are matched case-insensitively against
the tenant's `RtRole.NormalizedName`; a role that does not exist yet is **skipped with a warning**
identity-side rather than failing the whole identity-data setup, and picked up by the next
provisioning pass.

Phase 2 tests: `Services/PipelineServiceAccountProvisioningServiceTests` (secret entropy / URL-safety /
randomness, both grant types + scope + role on the created client, entity and edge written with the
plaintext, second run rotates nothing and writes nothing, unlinked entity is re-linked without
rotation, entity without a secret gets a fresh one, issuer change converges, no secret in any NLog
target and `DistClientDto.ToString()` redaction, tenant sweep backfills / isolates one broken adapter
/ survives a failing adapter lookup, identity refusal is not written as a half-provisioned entity,
seed-pending still writes the entity),
`Services/AdapterServiceTests/DeployPipelineAfterProvisioningTests` (**the phase 1 ↔ phase 2 proof**:
same guard refuses before and passes after the backfill, and the projected configuration carries the
deterministic well-known name the mesh adapter's `ServiceAccountTokenService` looks accounts up by),
`Services/PoolServiceTests/DeployWorkloadServiceAccountProvisioningTests` (Adapter yes / Application
no / a throwing provisioning does not fail the deploy),
`Services/DefaultConfigurationCreatorServiceTests/EnsurePipelineServiceAccountsAsyncTests` (audit
event only when something changed, never throws), integration
`Repository/PipelineServiceAccountRepositoryTests` (entity + edge against real MongoDB, repeat run
keeps exactly one edge despite the ZeroOrOne multiplicity, a different account replaces the edge).


### Phase 3 — deliberate secret rotation

Everything above is built to **never** rotate: a convergence pass re-sends the plaintext it already
holds, because a service restart that invalidated every adapter's credential would be a
self-inflicted outage. That leaves a leaked or aged secret unretireable. The rotation verb is the
explicit counterpart.

```
POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rotateSecret
  → 200 RotateServiceAccountSecretResultDto { ClientId, ConfigurationWellKnownName,
                                              WasCreated, RequiresPipelineRedeploy, Message }
```

`AdapterController.RotateServiceAccountSecret` (`TenantCommunicationApiReadWritePolicy`) →
`IPipelineServiceAccountProvisioningService.RotateAdapterSecretAsync`.

**Why the REST verb belongs here and not (only) in the CLI.** This service is the sole owner of
*both* halves of the credential — it is the only producer of the identity client (over the
distribution event hub; the identity REST API is unreachable without exactly the client-credentials
identity being rotated) and the writer of the `ServiceAccountConfiguration` entity. A CLI-side
rotation would have to reproduce that pairing across two services and could leave the halves apart.
Putting it here means CLI, MCP and Studio all reach it through the existing
`ICommunicationServicesClient`, and the audit event is written where the change happens. The
octo-cli/SDK command is a follow-up in those repos; nothing else is needed server-side.

**Ordering is the consistency argument** — identity is written **first**:

| Failure | Resulting state |
|---|---|
| Identity write fails | Nothing changed anywhere. Both sides still hold the old secret; the verb throws and answers 400 saying the previous secret remains in effect. |
| Configuration write fails | The client already carries the new hash — the one genuinely inconsistent window. The previous plaintext is pushed back to identity (compensation), so the adapters running on it keep working, and the original failure is surfaced. |
| Compensation also fails | Logged as Error naming both sides. The next convergence pass re-sends the plaintext the configuration still holds and heals it. |

A second call after any failure rotates again from a consistent state; two successful calls in a row
each leave both sides on the same fresh secret.

🔴 **`isRuntimeState` on `ClientSecret` does not block this.** That marker only makes a *blueprint
re-apply* preserve the existing value (`ImportRtModelCommand.PreserveAttributesForEntity`) and
excludes the attribute from `ExportRt`. The runtime write path (`EntityUpdateInfo` /
`SavePipelineServiceAccountAsync`) is unaffected — which is precisely why rotation has to go through
this path: a blueprint can no longer change a live secret at all.

🔴 **A rotation only takes effect after a redeploy.** The adapter bakes the pipeline's
`GlobalConfiguration` — service-account credentials included — into the immutable
`PipelineRegistration` at **pipeline registration** (`PipelineRegistryService`,
octo-communication-sdk) and never refreshes it. Until the adapter's pipelines / data flows are
redeployed, they keep presenting the secret the identity service has already replaced.
`RequiresPipelineRedeploy` and the `Message` say so; the audit event repeats it.

🔴 **No secret leaves the two sanctioned sinks.** The response DTO deliberately has no secret-shaped
property (returning one would add proxy logs, shell history and CI output as a third home), and
neither plaintext — the new one or the retired one — is written to any log target, not even
truncated, and not on the failure paths that log the most.

Phase 3 tests: `Services/PipelineServiceAccountRotationTests` (both sides get the same new secret,
entity updated not duplicated, identity-before-configuration ordering, twice in a row stays
consistent and really rotates, identity refusal leaves the configuration untouched, configuration
failure restores the previous secret, a failing compensation still reports the original failure,
adapter without an account degrades to a provisioning that needs no redeploy, no secret in any NLog
target on the happy and the double-failure path),
`Controllers/AdapterControllerRotateServiceAccountTests` (redeploy hint + audit event, no
secret-shaped property on the DTO, provisioning variant, 404 unknown adapter, 400 malformed rtId,
400 naming that the previous secret still works).
