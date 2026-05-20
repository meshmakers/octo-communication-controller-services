# E2E Smoke Test — CI/CD Workload Rollout

This runbook validates the full Epic 3054 pipeline end-to-end on the
`test-2` cluster — one adapter chart and one app chart rolled to every
tenant that uses them, plus a deliberate failure to confirm the per-
workload isolation. **Manual**, not wired into CI. Run after non-trivial
changes in any of the touched repos (controller `WorkloadController`,
`octo-cli` workload commands, `octo-mesh-deployment` pipelines, identity
mirror provisioning).

## What we verify

```
[CI build of octo-mesh-adapter or <app>-app finishes on main]
            ↓
[ADO resource trigger fires the per-chart wrapper pipeline]
            ↓
[deploy-workload.yml template]
            ├─ reads ci_deploy_client_id / ci_deploy_client_secret from Vault
            ├─ installs octo-cli from octo-cli-CI artifact
            ├─ logs into octosystem → GetTenants
            ├─ for each tenant:
            │     ├─ fresh client_credentials login (Phase-1 mirror identity)
            │     ├─ GetWorkloadsByChart -cn <chartName>
            │     │   • empty list → silent-skip
            │     │   • non-empty → for each workload:
            │     │       UpdateWorkloadChartVersion -id <rtId> -cv <ver>
            │     │       DeployWorkload -id <rtId>
            └─ collects per-workload failures, exits non-zero at end if any
            ↓
[Communication Controller WorkloadController PATCH /chart-version]
            └─ writes CommunicationEvent "Chart version … (source: CI/CD)"
            ↓
[Communication Controller PoolController workloads/deploy]
            ↓
[Operator /operatorHub WorkloadDeployedAsync fan-out]
            ↓
[WorkloadReconciler.DeployAsync → helm upgrade --install --atomic ...]
            ↓
[New chart version live in cluster]  ← kubectl + helm assertions
```

## Prerequisites

| What | How to check |
|---|---|
| Epic 3054 Phase 1 (#4042–#4051) deployed on test-2 | Identity service version ≥ `0.1.<this-release>`; `RtClient.AutoProvisionInChildTenants` schema present (schema version 16) |
| Epic 3054 Phase 2 (#4052) deployed | Communication Controller version with `WorkloadController` (`GET /workload?chartName=…` returns 200 / empty array, not 404) |
| CI deploy client mirrored in every test-2 sub-tenant | `octo-cli -c GetClientMirrors -id ci-deploy-test-2` lists all expected sub-tenants |
| Vault entries | `secret/meshmakers/test-2/octomesh` has `ci_deploy_client_id` + `ci_deploy_client_secret` |
| ADO variable group `OctoDefault` | Has `IDENTITY_URL_test-2` / `ASSET_URL_test-2` / `COMM_URL_test-2` |
| `octo-cli-CI` pipeline artifact `octo-cli-linux-x64` | Latest run on `main` published the zip (the deploy-workload template downloads from there) |
| Per-chart wrappers imported in ADO | `deploy-adapter-chart-octo-mesh-adapter` and `deploy-app-energy-community` exist as ADO pipelines and have completed one successful manual run (required for ADO to activate resource triggers) |
| At least one Adapter using `octo-mesh-adapter` chart in `acme` and `voest` test tenants | Studio: open each tenant → Adapters → confirm `ChartName=octo-mesh-adapter` |
| At least one Application using `energy-community-app` chart in `acme` only | Studio: open `acme` → Applications → confirm `ChartName=energy-community-app`, `voest` has none |

## Variables for this run

```bash
export CLUSTER=test-2
export ADAPTER_CHART=octo-mesh-adapter
export APP_CHART=energy-community-app
export TENANTS_WITH_ADAPTER=(acme voest)        # both use the adapter
export TENANTS_WITH_APP=(acme)                  # only acme uses the app
export CONTROLLER_URL="https://comm.${CLUSTER}.mm.cloud"   # adjust if different
```

## Step 1 — Adapter chart rollout (full happy path)

### 1.1 — Snapshot the pre-state

```bash
# kubectl context for test-2
export KUBECONFIG=/path/to/test-2/kubeconfig

# Helm releases for the adapter chart, per tenant. Note the chart version.
for t in ${TENANTS_WITH_ADAPTER[@]}; do
  helm list -A -f "^${t}-" | grep "${ADAPTER_CHART}" || echo "no release for ${t}"
done
```

Expect to see one release per tenant, e.g. `acme-mesh-adapter` and
`voest-mesh-adapter`, each pinned to the **current** chart version. Note
that version — it's the "before" baseline.

### 1.2 — Trigger the CI build

Push a no-op commit to `octo-mesh-adapter` `main`, or re-queue
`octo-mesh-adapter-CI` from ADO UI. Wait for `:main-latest` Docker tag +
the chart version bump to appear in the Helm registry.

### 1.3 — Watch the rolling pipeline

In ADO open `deploy-adapter-chart-octo-mesh-adapter`. A new run should
appear within ~30 s of `octo-mesh-adapter-CI` finishing (resource-pipeline
trigger). On test-2 there's no approval gate — the rollout runs through
automatically.

In the run log, scroll to the `Roll octo-mesh-adapter -> …` step. Look for:

```
=== Tenant: acme ===
  OK   acme / <rtId> -> <newVersion>
=== Tenant: voest ===
  OK   voest / <rtId> -> <newVersion>
=== Tenant: octosystem ===
  no workloads for octo-mesh-adapter — skip
...

=== Rollout summary ===
Chart:    octo-mesh-adapter
Version:  <newVersion>
Cluster:  test-2
Rolled:   2 / 2
```

`Rolled: N/M` where `N == M > 0` is the success condition. Tenants without
the chart get a single `no workloads … — skip` line and don't show up in
the failed list.

### 1.4 — Verify on the controller

```bash
# Find the CommunicationEvent log entries for the version change.
# Use octo-cli or curl against the asset repo events endpoint; example via
# Studio for simplicity:
#   open <tenant>/repository/events
#   filter `Source: CommunicationService` and "Chart version"
```

Expect, **per tenant with the adapter**, exactly one Information event:

```
Chart version for workload <rtId> updated from <oldVer> to <newVer> (source: CI/CD).
```

The `(source: CI/CD)` tag distinguishes the rollout from any manual Studio
edits an operator might have done in parallel.

### 1.5 — Verify in the cluster

```bash
# Releases should now report the new chart version.
for t in ${TENANTS_WITH_ADAPTER[@]}; do
  helm list -A -f "^${t}-" | grep "${ADAPTER_CHART}"
done

# Pod image digests changed (operator ran helm upgrade --atomic).
for t in ${TENANTS_WITH_ADAPTER[@]}; do
  kubectl get pods -n octo -l "app.kubernetes.io/instance=${t}-mesh-adapter" \
    -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.status.containerStatuses[*].imageID}{"\n"}{end}'
done
```

Each release in the `helm list` output should now show the new chart
version. Each pod should have a `containerStatuses.imageID` that differs
from the pre-rollout digest noted in 1.1.

## Step 2 — App chart rollout (different trigger source, narrower tenant set)

### 2.1 — Trigger the CI build

Re-queue `energy-community-CI` or push a no-op commit to its `main`. Wait
for the chart artifact.

### 2.2 — Watch the rolling pipeline

ADO → `deploy-app-energy-community` → newest run. The summary should
report **only `acme` rolled**, every other tenant `no workloads … — skip`:

```
Rolled:   1 / 1
```

This is the tenant-filtering case: the app only lives in one tenant, so
the rollout fans out narrowly. The same rollout firing `:tenantFilter=all`
on a chart no tenant uses (e.g. a brand-new app before any
`RtApplication` exists in MongoDB) prints `Rolled: 0 / 0` — also valid.

### 2.3 — Verify

Same as 1.4 / 1.5, scoped to `acme`. Event + helm release + new pod
digest.

## Step 3 — Failure isolation (negative test)

The pipeline collects per-workload failures and reports a non-zero exit at
the end, but **does not** abort mid-tenant. This protects against a single
bad workload taking out an otherwise-successful rollout.

### 3.1 — Push a deliberately bad version

Manually run `deploy-adapter-chart-octo-mesh-adapter` from ADO with
parameters:

- `chartVersion`: `99.99.99` (or any version that does not exist in the
  Helm registry)
- `tenantFilter`: `all`

### 3.2 — Expected outcome

The pipeline succeeds at the **PATCH** stage (the controller writes any
SemVer-shaped string into MongoDB without consulting the registry — that
check is Helm's job at deploy time). The pipeline then fails at the
**deploy** stage, once per tenant that uses the adapter:

```
=== Tenant: acme ===
  FAIL acme / <rtId>
=== Tenant: voest ===
  FAIL voest / <rtId>

=== Rollout summary ===
Rolled:   0 / 2
Failed:   acme/<rtId> voest/<rtId>
##vso[task.logissue type=error]2 workload(s) failed: acme/<rtId> voest/<rtId>
```

The job exits 1, the ADO run is red. Crucially: **both tenants are
attempted** — the second tenant is not skipped because the first failed.

### 3.3 — Recover

Re-run the pipeline with the real version. The PATCH is idempotent
(records the version verbatim) and `DeployWorkload` re-fires the helm
upgrade — within a minute the cluster should be back on the previous
known-good release that the failed `--atomic` upgrade rolled back to.

```bash
# Confirm cluster is healthy again:
for t in ${TENANTS_WITH_ADAPTER[@]}; do
  helm status -n octo "${t}-mesh-adapter" | grep STATUS
done
# Expect: "STATUS: deployed" in every tenant.
```

## Step 4 — Confirm Studio sees what CI did

This is a sanity check that the source-of-truth-is-controller-DB decision
(Epic 3054 concept doc) actually holds:

1. Open Studio at `https://studio.test-2.mm.cloud/{tenant}/communication/adapters`
2. For an adapter that just got rolled: open it. The `Chart Version` field
   should match the version the pipeline rolled to.
3. The event log under `repository/events` shows the `(source: CI/CD)`
   entry.

If Studio shows a stale version, the cache invalidation between the
controller PATCH and Studio's GET is broken — that's a regression in
either `WorkloadController` or the Studio's `IdentityService`-equivalent
read path. Capture the ADO build id + the controller pod log and file an
issue against `octo-communication-controller-services`.

## Cleanup

There is nothing to undo: the rollout is a chart-version bump. To restore
a prior version manually:

```bash
# Find the previous version from helm history
helm history -n octo "<tenant>-<workload>"

# Roll back (operator-driven; this won't update the controller-side
# ChartVersion attribute, so do it in Studio or via octo-cli to keep the
# two sides in sync):
octo-cli -c UpdateWorkloadChartVersion -id <rtId> -cv <previousVersion>
octo-cli -c DeployWorkload -id <rtId>
```

## Sign-off

The test passes when **all** of the following are true on test-2:

- [ ] Step 1 adapter rollout: ADO run green, both tenants `OK`, pod digests changed, event log entries present.
- [ ] Step 2 app rollout: ADO run green, only `acme` rolled, others skipped silently.
- [ ] Step 3 failure isolation: ADO run red with two `FAIL` lines but **both** tenants attempted; recovery roll afterwards succeeds.
- [ ] Step 4 Studio shows the post-roll chart version on every affected workload.

Save the ADO run URLs + a short note ("CI rollout E2E on test-2,
<date>, passed") in `project-multi-tenant-client-credentials.md` memory
so the next reviewer can see when this was last validated.
