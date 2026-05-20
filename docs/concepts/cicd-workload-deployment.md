# Multi-Tenant Client Credentials + CI/CD Workload Deployment

Status: **Draft / RFC** — design only, no code changes yet.
Last update: 2026-05-20.

## Initiative Summary

Two coupled work-streams that must ship in order:

1. **Phase 1 — Multi-Tenant Client Credentials** (`octo-identity-services` +
   `octo-frontend-refinery-studio` + `octo-cli`).
   Make it possible to provision a single ClientCredentials client identity
   into many sub-tenants automatically. **Foundation.** Must be complete
   before anything below starts.

2. **Phases 2–5 — CI/CD Workload Rollout** (`octo-communication-controller-services`
   + `octo-cli` + `octo-mesh-deployment`).
   When a Helm chart CI build finishes (`octo-mesh-adapter`,
   `energy-community-app`, `maco-app`, …), automatically roll the new chart
   version to every tenant that uses the chart.

Phase 1 is interesting on its own (it solves a long-standing gap in the
identity model for any service-to-service / third-party-integration use
case), and it removes the need for an `octosystem`-scoped "AdminProvisioning"
routing layer in the CI/CD work. Without it, Phase 2 would have to invent a
system-tenant API surface that the rest of the platform doesn't need.

---

# Phase 1 — Multi-Tenant Client Credentials

## Phase 1 Problem

Today a `ClientCredentialsClient` lives in exactly one tenant. Concretely:

- `AddClientCredentialsClient` creates an IdentityServer client record in
  the **calling tenant's** identity database.
- A token issued via `LogInClientCredentials` carries
  `tenant_id == <that-one-tenant>`. `AllowedTenantsResolver` populates
  `allowed_tenants` only with the login tenant id.
- `TenantAuthorizationMiddleware` (in `octo-common-services`) compares the
  token's `tenant_id` claim against the route tenant and rejects anything
  else.

So a client created in `acme` can call `acme/v1/...` and nothing else. There
is no equivalent of "add user to multiple tenants" for clients.

For users we already have a solution: `AdminProvisioningController` in
`octo-identity-services` + the CLI's `CreateAdminProvisioningMapping` /
`ProvisionCurrentUser` commands. A user from `octosystem` can be provisioned
as an admin in any target sub-tenant, getting a real local identity there.
Tokens are then issued by the **target tenant's** identity, so the
`tenant_id` claim matches the route and everything just works.

**Phase 1 mirrors that pattern for clients.**

## Phase 1 Goals

1. A client created in tenant `P` can carry a flag
   `AutoProvisionInChildTenants`. When `P` later creates a child tenant `C`,
   the same client (same `ClientId`, same secret hash, same scope set) is
   auto-provisioned in `C`'s identity database.
2. A `Backfill` operation can retro-fit the flag onto an existing client —
   walks all current sub-tenants of `P` and provisions the client in each.
3. Studio surfaces the flag, the list of sub-tenants the client is currently
   provisioned in, and the backfill action.
4. CLI surfaces the same.
5. Secret rotation at the parent propagates to all mirrors (single source of
   truth).

Non-goals for v1 (call out so we don't argue later):

- **Per-tenant scope differences.** Every mirror gets the parent's scope set
  verbatim. Matches the user-`AdminProvisioning` model.
- **Manual mirror lifecycle.** Mirrors are not editable in the sub-tenant —
  they are read-only reflections of the parent. Removing the parent client
  removes all mirrors. Removing a sub-tenant removes its mirror only.
- **Cross-instance** (across separate OctoMesh deployments). Out of scope.

## Phase 1 Backend (Identity Services)

Owner: `octo-identity-services`.

### Data model

Extend `Client` (IdentityServer's client record) with one column / claim:

| Field | Type | Default | Meaning |
|---|---|---|---|
| `AutoProvisionInChildTenants` | `bool` | `false` | When `true`, new child tenants get a mirror of this client provisioned automatically. |

In a separate small collection, track mirror state so Studio can show it
and so cleanup is robust:

`ClientMirror` (one document per parent-client × child-tenant pair):

| Field | Description |
|---|---|
| `ParentClientId` | The `ClientId` in the parent tenant. |
| `ParentTenantId` | The parent tenant (the source of truth). |
| `ChildTenantId` | The sub-tenant the mirror lives in. |
| `ProvisionedAt` | Timestamp. |
| `SecretHashVersion` | Monotonic counter incremented on secret rotation; used to detect mirrors that fell behind on a rotation and need re-sync. |

### Events / triggers

| Event | Action |
|---|---|
| `TenantCreated(childTenantId)` on parent `P` | For every client in `P` with `AutoProvisionInChildTenants=true`: create mirror client in `childTenantId`'s identity DB, write `ClientMirror` row. |
| `ClientSecretRotated(parentClientId)` | Walk `ClientMirror` for this client, update each mirror's secret hash, bump `SecretHashVersion`. |
| `ClientUpdated(parentClientId)` (scopes / grant types changed) | Same as rotation but for the scope set. |
| `ClientDeleted(parentClientId)` | Remove every mirror, then remove `ClientMirror` rows. |
| `TenantDeleted(childTenantId)` | Drop `ClientMirror` rows pointing at that tenant. (Mirror records inside the deleted tenant's DB are gone with the DB.) |

The first event is the load-bearing one — the others are upkeep.
Implementation reuses the existing `IDistributionEventHubService` patterns
already used in `octo-asset-repo-services/.../TenantsController.cs`
(`PreUpdateTenant`, `PosUpdateTenant`).

### REST API

`octo-identity-services` exposes (under `{tenantId}/v1/clients/...`, the
existing client-management route):

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `{tenantId}/v1/clients/{clientId}/mirrors` | List `ClientMirror` rows for this client — which sub-tenants it's currently provisioned in. |
| `POST` | `{tenantId}/v1/clients/{clientId}/provisionInExistingTenants` | Backfill: walk all child tenants of `{tenantId}`, create missing mirrors. Idempotent. |
| `POST` | `{tenantId}/v1/clients/{clientId}/provisionInTenant?childTenantId=…` | Manual one-shot provision into a specific child. |
| `DELETE` | `{tenantId}/v1/clients/{clientId}/mirrors/{childTenantId}` | Manual one-shot unprovision from a specific child (e.g. after a sub-tenant should no longer have access). |

`AddClientCredentialsClient` (existing) gets a new optional body field
`autoProvisionInChildTenants`. CLI flag follows in Phase 1 CLI section below.

### Auth flow after Phase 1

Provisioned client logs in **against the sub-tenant directly**:

```
ContextManager has tenantId=acme
OCTO_CLI_CLIENT_ID=ci-deploy
OCTO_CLI_CLIENT_SECRET=***
→ POST acme-identity/connect/token (client_credentials)
→ token { tenant_id: "acme", allowed_tenants: ["acme"] }
→ caller can hit acme/v1/* endpoints, full client scope set
```

`TenantAuthorizationMiddleware` is unchanged. No wildcard `allowed_tenants`,
no system-tenant routing detour. The token is for the sub-tenant the caller
wants to talk to.

To iterate over many tenants, the caller obtains one token per target tenant
(re-using the same `ClientId`/`Secret`). Token cost is fine — these are
millisecond OIDC roundtrips.

## Phase 1 Studio Integration (Refinery Studio)

Owner: `octo-frontend-refinery-studio`.

### Where this lives in the UI

Add to the existing **Clients** management page (under Settings → Identity →
Clients, or wherever client CRUD currently sits — confirm during PR). The
page is part of the OctoSystem-tenant-only view today; same access pattern
applies here because mirrors are managed from the parent.

### Form changes

In the **create/edit client dialog**, when the grant type is
`client_credentials`:

- New toggle: **"Auto-provision in child tenants"** with hover help: *"When
  this client is enabled and the current tenant creates a new sub-tenant,
  the sub-tenant will receive a mirror of this client automatically. Use
  for service-to-service or CI/CD identities that must access many
  tenants."*
- Toggle is hidden / disabled for non-client-credentials grant types
  (device flow / interactive doesn't make sense to auto-mirror).

### New panel: "Provisioned in"

Below the existing client property form, a collapsible card titled
**"Provisioned in sub-tenants"**:

| Tenant ID | Provisioned at | Secret version | Actions |
|---|---|---|---|
| acme | 2026-05-20 14:23 | 1 | [Re-sync] [Remove] |
| voest | 2026-05-20 14:23 | 1 | [Re-sync] [Remove] |
| energy-coop | 2026-04-12 09:01 | 0 (out of date) | [Re-sync] [Remove] |

Rows out-of-date on `SecretHashVersion` get a small warning badge with the
explanation in a tooltip.

Above the table, two buttons:

- **"Provision in existing tenants"** — calls
  `POST {tenantId}/v1/clients/{clientId}/provisionInExistingTenants`.
  Shown as primary action when the flag is on but the table is empty (the
  typical state right after flipping the flag on for an old client).
  Idempotent; confirms with a count: *"Provisioned in 3 sub-tenants (5
  already existed)."*
- **"Provision in specific tenant…"** — opens a small picker, calls
  `provisionInTenant?childTenantId=…`. Used when the operator forgot to
  enable the flag before a one-off tenant was created.

### Visibility from inside a sub-tenant

In the sub-tenant's **Clients** page, show provisioned mirrors as a **read-only
section** ("Provisioned by parent tenant") with the parent's tenant id, the
client id, the scope set, and a note that this client is managed in the
parent and cannot be edited here. Reason: sub-tenant admins need to be able
to see who has access into their tenant.

### Studio API client

Use the existing tenant-aware REST client. No new auth flow needed — the
Studio user already has cross-tenant rights when logged into `octosystem`.

### Studio behavior at tenant creation

When the operator creates a sub-tenant via the **Tenants** page, after the
create succeeds:

- Show a toast: *"Tenant 'acme' created. 3 client(s) auto-provisioned."*
  with a link to the affected clients filtered list. Gives immediate
  feedback that the magic happened.

## Phase 1 CLI Changes

Owner: `octo-cli`.

| Command | Change | New args |
|---|---|---|
| `AddClientCredentialsClient` | Existing — extend | `-apic` / `--auto-provision-in-child-tenants` (bool) |
| `UpdateClient` (or new `SetClientAutoProvision`) | Add ability to flip the flag on an existing client | `--client-id`, `--auto-provision-in-child-tenants` |
| `GetClientMirrors` | New | `--client-id`, `--json` |
| `ProvisionClientInExistingTenants` | New (backfill) | `--client-id` |
| `ProvisionClientInTenant` | New | `--client-id`, `--child-tenant-id` |
| `UnprovisionClientFromTenant` | New (destructive — confirmation prompt) | `--client-id`, `--child-tenant-id`, `--yes`/`-y` |

All run against the active context's tenant (typically `octosystem`).

## Phase 1 Tests

`octo-identity-services`:

- Unit tests for the `TenantCreated` consumer: provisions only flagged
  clients, skips already-mirrored ones (idempotency), writes `ClientMirror`.
- Unit tests for secret-rotation propagation.
- Integration test: create flagged client in `octosystem`, create three
  child tenants in sequence, verify mirrors exist and tokens issued by
  each child tenant carry the right `tenant_id`.
- Integration test for `provisionInExistingTenants` backfill: flag-off
  client created before three children exist → flip flag → call backfill →
  three mirrors materialize → calling fourth tenant create still auto-provisions.

Refinery Studio:

- Component tests for the toggle + the "Provisioned in" panel.
- One Karma test for the post-tenant-create toast count.

CLI:

- Snapshot tests for each new command's help text.
- Integration tests against a real identity server for `AddClient -apic`
  end-to-end and backfill.

## Phase 1 Exit Criteria

- A flagged client in `octosystem` is auto-provisioned in every new sub-tenant.
- A token issued by `<sub-tenant>` identity for that client carries
  `tenant_id == <sub-tenant>` and passes `TenantAuthorizationMiddleware` on
  any sibling service.
- Studio shows mirrors per client, the backfill action works, and the post-
  tenant-create toast fires.
- The CI client `ci-deploy-<cluster>` can be created (manually, one time)
  with `-apic`, and from then on, any new sub-tenant gets it for free.
- All tests above are green.

Only when this is done do we start Phase 2.

---

# Phases 2–5 — CI/CD Workload Rollout

## CI/CD Problem (recap)

When a service CI build finishes (e.g. `octo-mesh-adapter`,
`energy-community-app`, `maco-app`), a new Helm chart version is published
to the Helm registry. We need a CI/CD-driven way to **roll that new chart
version out to every tenant where the chart is used**:

| Workload class | Scope of rollout |
|---|---|
| Mesh / EDA / Modbus / Loxone adapters | All tenants that own a matching `RtAdapter` |
| Per-customer Applications (`energy-community-app`, `voest-app`, `maco-app`, …) | All tenants that own a matching `RtApplication` |

Additional constraints:

- **Core services must already be deployed** before adapter updates run
  (Communication Controller endpoints need to be reachable).
- **Apps have their own CI** and trigger their own CD independently.
- We do **not** want to maintain a per-app YAML pipeline + a per-cluster
  `apps.json` / `tenants.json` anymore — that information already lives in
  the Communication Controller's MongoDB (`RtAdapter` / `RtApplication`,
  both with `ChartName` + `ChartVersion`) and stays the single source of
  truth.

## CI/CD Solution (one paragraph)

The Studio / Controller's MongoDB is the **source of truth** for what
workloads exist in which tenants. CI does **two things only** per workload:

1. Set the new `ChartVersion` on the workload (`PATCH .../chart-version`).
2. Trigger the existing workload-deploy flow (`POST .../deploy`).

Because of Phase 1, CI authenticates **directly into each sub-tenant**
using its single auto-provisioned client identity. No system-tenant API,
no `targetTenantId` query parameter on every endpoint, no AdminProvisioning
detour.

## Status Quo (what already exists, after Phase 1)

| Component | State | Reference |
|---|---|---|
| CK entities `Adapter` + `Application` with `ChartName`/`ChartVersion`/`HelmRepository`/`ValuesYaml`/`Values[]` | Done | `System.Communication` 3.15.0+ |
| Workload deploy via operator (`helm upgrade --install`) | Done (Phase 3 of the helm initiative) | `octo-communication-operator/docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md` |
| Per-tenant REST endpoint to deploy a single workload: `POST {tenantId}/v1/pool/workloads/deploy?workloadRtId=…` | Done | `PoolController.DeployWorkloadAsync` |
| Per-tenant REST endpoint to deploy / undeploy a whole pool | Done | `PoolController` |
| `WorkloadEncryptionService` (`InstanceSecretKey`, AES-256-GCM) | Done | Phase 2 of the helm initiative |
| octo-cli ClientCredentials login (`OCTO_CLI_CLIENT_ID`/`OCTO_CLI_CLIENT_SECRET`) | Done | `LogInClientCredentials` |
| octo-cli context management (multi-cluster) | Done | `~/.octo-cli/contexts.json` |
| **Cross-tenant CI client identity** | **Phase 1** | this doc |
| Tenant listing: `GET {tenantId}/v1/tenants` returns the caller-tenant's direct children. Called as `octosystem`, this enumerates every customer tenant. | Done | `octo-asset-repo-services/.../TenantsController.cs` lines 51–93 |

## Source-of-truth Decision: Why "Controller DB", not `apps.json`

(Unchanged from the earlier draft.) We considered keeping the tenant-list in
`octo-mesh-deployment/clusters/<cluster>/apps.json`. Decision: **drop it**:

- The CK model already carries `ChartName`, `ChartVersion`, `ValuesYaml`,
  `Values[]`, `HelmRepository` on every workload entity. Duplicating that in
  `apps.json` means two places drift apart.
- The Studio is the operator UX. If an operator changes the chart version in
  Studio, the next CI deploy must not overwrite it — and equally, a CI roll
  must be visible in Studio. One store, both directions.
- Onboarding a new tenant or new app already requires creating the
  `RtApplication`. Demanding a parallel commit to `apps.json` is friction
  with no upside.

Trade-off accepted: harder to do "redeploy this exact version to this
exact cluster" as a diff-able git commit. Mitigation: every chart-version
write goes through `ICommunicationEventService.StoreInformationEventAsync`,
so the audit trail is preserved.

## Gaps (Phases 2–4)

Phase 1 makes Phase 2 small. Only two new endpoints needed on the
Communication Controller, both as ordinary tenant-scoped routes:

### G-CI-1 — `GET {tenantId}/v1/workloads?chartName=…`

Returns the workloads in the calling tenant whose `ChartName` matches:

```
GET acme/v1/workloads?chartName=octo-mesh-adapter
Authorization: Bearer <acme token, issued via auto-provisioned client>
→ 200 [
    { "rtId": "...", "name": "mesh-adapter-1", "ckTypeId": "...Adapter",
      "currentChartVersion": "1.2.2", "deploymentState": "Deployed" },
    ...
  ]
```

Empty array = chart unused in this tenant → CI silently skips. Reuses
`ICommunicationRepository` for the actual lookup. No `targetTenantId`
parameter — auth covers it.

### G-CI-2 — `PATCH {tenantId}/v1/workloads/{workloadRtId}/chart-version`

```
PATCH acme/v1/workloads/.../chart-version
{ "chartVersion": "1.2.3" }
→ 204 No Content
```

- Validates `chartVersion` is non-empty SemVer.
- Loads `RtAdapter` or `RtApplication`, updates `ChartVersion`, saves
  through `ICommunicationRepository`.
- Writes an `Information` event:
  *"Chart version for workload {name} updated from {old} to {new}
  (source: CI/CD)."*
- Does **not** trigger deploy — the caller decides when to roll.

That's it. Pool-deploy, workload-deploy, undeploy endpoints all already
exist on tenant-scoped routes and work as-is once the CI client has a token
for the target tenant.

### G-CI-3 — octo-cli commands

| Command | Calls | Args |
|---|---|---|
| `GetTenants` | `GET {tenantId}/v1/tenants` (Asset Repo, active context) | `--json` |
| `GetWorkloadsByChart` | `GET {tenantId}/v1/workloads?chartName=…` | `--chart-name`, `--json` |
| `UpdateWorkloadChartVersion` | `PATCH {tenantId}/v1/workloads/{rtId}/chart-version` | `--workload-rt-id`, `--chart-version` |
| `DeployWorkload` | `POST {tenantId}/v1/pool/workloads/deploy?workloadRtId=…` (existing endpoint, missing CLI command today) | `--workload-rt-id` |
| `UndeployWorkload` | mirror of above | `--workload-rt-id`, `-y` |

All run against the active context's tenant. To switch the active tenant
in CI without re-running `AddContext`, add `UseContext --tenant <id>` or a
one-shot `--tenant <id>` flag on the individual commands (bikeshed in PR).

### G-CI-4 — Generic ADO pipeline template

One template `templates/deploy-workload.yml` replaces all six per-app /
per-adapter pipelines:

```yaml
parameters:
  - name: cluster
    type: string
  - name: chartName
    type: string
  - name: chartVersion
    type: string
    default: ''   # if empty, read from triggering CI's versioninfo.txt
  - name: tenantFilter
    type: string
    default: 'all'   # 'all' or comma-separated tenantIds

steps:
  - bash: |
      set -e

      # 0. Resolve version
      VERSION="${{ parameters.chartVersion }}"
      [ -z "$VERSION" ] && VERSION=$(cat $(Pipeline.Workspace)/appChart/local/versioninfo.txt)

      # 1. Configure octosystem context for tenant discovery
      octo-cli -c AddContext -n $(cluster)-system \
        -isu $(IDENTITY_URL_$(cluster)) \
        -asu $(ASSET_REPO_URL_$(cluster)) \
        -ccu $(COMM_CONTROLLER_URL_$(cluster)) \
        -tid octosystem
      octo-cli -c UseContext -n $(cluster)-system

      export OCTO_CLI_CLIENT_ID=$(OCTO_CI_CLIENT_ID_$(cluster))
      export OCTO_CLI_CLIENT_SECRET=$(OCTO_CI_CLIENT_SECRET_$(cluster))
      octo-cli -c LogInClientCredentials

      # 2. Discover all tenants
      TENANTS=$(octo-cli -c GetTenants --json | jq -r '.[].tenantId')
      if [ "${{ parameters.tenantFilter }}" != "all" ]; then
        FILTER="${{ parameters.tenantFilter }}"
        TENANTS=$(echo "$TENANTS" | grep -Fx -f <(echo "$FILTER" | tr ',' '\n'))
      fi

      # 3. For each tenant: switch context, login again (sub-tenant token),
      #    list matching workloads, set chart version, deploy.
      FAILED=()
      for TID in $TENANTS; do
        octo-cli -c AddContext -n $(cluster)-$TID \
          -isu $(IDENTITY_URL_$(cluster)) \
          -ccu $(COMM_CONTROLLER_URL_$(cluster)) \
          -tid $TID
        octo-cli -c UseContext -n $(cluster)-$TID
        # Same client, different login tenant → token now carries tenant_id=$TID
        octo-cli -c LogInClientCredentials

        WORKLOADS=$(octo-cli -c GetWorkloadsByChart \
          --chart-name "${{ parameters.chartName }}" --json | jq -r '.[].rtId')

        for W in $WORKLOADS; do
          if octo-cli -c UpdateWorkloadChartVersion \
               --workload-rt-id $W --chart-version $VERSION \
             && octo-cli -c DeployWorkload --workload-rt-id $W; then
            echo "OK   $TID/$W → $VERSION"
          else
            echo "FAIL $TID/$W"
            FAILED+=("$TID/$W")
          fi
        done
      done

      if [ ${#FAILED[@]} -gt 0 ]; then
        echo "##vso[task.logissue type=error]${#FAILED[@]} workload(s) failed: ${FAILED[*]}"
        exit 1
      fi
    displayName: 'Roll chart ${{ parameters.chartName }} → ${{ parameters.chartVersion }} on ${{ parameters.cluster }}'
```

Per-service wrapper (one short file each):

```yaml
# pipelines/deploy-adapter-chart-octo-mesh-adapter.yml
resources:
  pipelines:
    - pipeline: coreServices
      source: 'deploy-octo-mesh-core-services'   # ordering guarantee
      trigger:
        stages: [DeployStaging, DeployProd]

stages:
  - stage: DeployStaging
    jobs:
      - template: templates/deploy-workload.yml
        parameters:
          cluster: staging-1
          chartName: octo-mesh-adapter
  # ... prod-1, prod-2 ...
```

```yaml
# pipelines/deploy-app-energy-community.yml
resources:
  pipelines:
    - pipeline: appChart
      source: 'energy-community-CI'
      trigger:
        branches: { include: [main, 'refs/tags/*'] }

stages:
  - stage: DeployStaging
    jobs:
      - template: templates/deploy-workload.yml
        parameters:
          cluster: staging-1
          chartName: energy-community-app
  # ... prod-2 with manual approval ...
```

## End-to-end flow (mesh adapter, all tenants — assuming Phase 1 is done)

```
1. octo-mesh-adapter CI completes → publishes chart 1.2.3 to Helm registry.

2. octo-helm-core-CI rebuilds the umbrella chart artifact.

3. deploy-octo-mesh-core-services pipeline rolls core services.

4. deploy-adapter-chart-octo-mesh-adapter is resource-triggered by step 3
   for the same release.

5. deploy-workload.yml runs per cluster:
   a. octosystem context → LoginClientCredentials → token{tenant_id:octosystem}.
   b. GetTenants → [acme, voest, energy-coop, ...].
   c. For each tenant T:
      - Switch to T context.
      - LoginClientCredentials (same ClientId/Secret, now issues token{tenant_id:T}
        — works because the client is auto-provisioned in T courtesy of Phase 1).
      - GetWorkloadsByChart --chart-name octo-mesh-adapter → [w1, w2, ...]
      - For each w: UpdateWorkloadChartVersion → DeployWorkload.

6. Controller dispatches WorkloadDeployedAsync → Operator runs
   helm upgrade --install → chart 1.2.3 pulled.

7. Each chart-version write + deploy logged via CommunicationEventService.
```

## End-to-end flow (per-customer app, e.g. energy-community-app)

Same as above, but:

- Triggered by `energy-community-CI` (not core).
- `chartName=energy-community-app`. Tenants where no `RtApplication` with
  this chart exists return `[]` from `GetWorkloadsByChart` and are silently
  skipped. No `tenantFilter` needed in the common case.
- For a canary release on one tenant: wrapper sets `tenantFilter: acme`.

## Order guarantee (core before adapters)

Mesh-adapter pipeline declares
`resources.pipelines.coreServices.source: deploy-octo-mesh-core-services`
with the appropriate stage filter — ADO holds the adapter rollout until
core is green for the same triggering release. Apps have no core dependency
and trigger independently from their own CI.

## Failure semantics

- **Per-workload failures are isolated.** A failed chart-version write or a
  failed `DeployWorkload` on one tenant does not stop the rollout on the
  others. The script collects failures and exits non-zero at the end.
- **No automatic rollback.** Per-tenant rollback is a manual operation
  (set `ChartVersion` back via Studio or `UpdateWorkloadChartVersion`, then
  redeploy). Auto-rolling everything backwards on a partial failure would
  make the blast radius worse.

## Phased implementation (after Phase 1)

### Phase 2 — Controller-side endpoints

`octo-communication-controller-services`:

- New `TenantApi/v1/Controllers/WorkloadController.cs` with:
  - `GET {tenantId}/v1/workloads?chartName=…`
  - `PATCH {tenantId}/v1/workloads/{workloadRtId}/chart-version`
- Unit + integration tests (TUnit + xUnit fixtures as in `CLAUDE.md`).
- Event-log entries via `ICommunicationEventService`.

### Phase 3 — octo-cli commands

`octo-cli`:

- `GetTenants` (Asset Repo).
- `GetWorkloadsByChart` (Comm Controller).
- `UpdateWorkloadChartVersion`.
- `DeployWorkload` / `UndeployWorkload`.

### Phase 4 — Generic deploy pipeline

`octo-mesh-deployment`:

- `pipelines/templates/deploy-workload.yml` (template above).
- One thin wrapper per chart with the resource trigger + the `chartName`.
- Old per-app/per-adapter pipelines stay alive for one release cycle, then
  deleted. `clusters/<cluster>/apps.json` and `tenants.json` deleted once
  both rollouts have switched over.

### Phase 5 — Smoke test on `test-2`

- Use `test-2`'s rolling environment (no approvals) to validate the full
  flow end-to-end with one adapter chart and one app chart.
- Verify event log shows chart-version updates + deploys.
- Verify a deliberately broken chart version fails only the affected
  tenant.

**Runbook**: `octo-communication-controller-services/docs/E2E-CICD-WORKLOAD-ROLLOUT.md`
captures the full manual procedure (prerequisites, four steps with
expected output, sign-off checklist).

## Open questions (for the CI/CD phases)

- **Nested customer sub-tenants**: `GetChildTenantsAsync` is one level deep.
  Production keeps the tree flat (every customer is a direct child of
  `octosystem`), so a single `GET octosystem/v1/tenants` returns the full
  set. If a customer ever spawns its own sub-tenants and chart X lives
  there too, CI would miss it. Mitigation: document in CI runbook. If/when
  needed, add `?recursive=true` to the Asset Repo endpoint.
- **System-API scope name**: `octo_api` covers the new endpoints today.
  Once Phase 1 is in, the CI client uses its standard scope set in every
  sub-tenant — no special scope needed.
- **HelmRepository credential rotation**: out of scope, handled separately
  under `project-helm-chart-secret-contract.md`.
- **System variables (planned)**: when implemented, `ValuesYaml` /
  `Values[]` may reference `${variable.name}`.
  `UpdateWorkloadChartVersion` should probably validate that any newly
  referenced variables exist. Track separately.

## Out of scope

- Multi-arch image gating (planned separately).
- Edge-cluster workload rollout (those operators live behind Tailscale;
  follow-up concept).
- Helm chart smoke-tests post-deploy (e.g. wait for pod ready). Today's
  operator's `helm upgrade --atomic` already does this and surfaces
  failures via `success=false` on the runtime entity; the rollout
  pipeline can use that signal in a future iteration.
