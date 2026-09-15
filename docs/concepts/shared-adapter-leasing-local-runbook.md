# Shared adapter leasing — trying it locally

A hand-run of AB#4924 against `Start-Octo`, end to end: one lending tenant with a pool, one
borrowing tenant with a `Leased` adapter, one member process, one pipeline execution that gets
queued, leased, run and released.

✅ **Status: WALKED END TO END, 2026-09-15**, lender `accounting` → borrower `salzburgdev`, on the
`dev` checkout in `DebugL`. Every step below is now an observation rather than a hypothesis, and the
observations disagreed with the previous version of this document in six places. They are marked
🔴 **CORRECTED**.

🔴 **Three of those corrections are product defects, not documentation drift.** A pool-member process
**cannot start** on the code as it stands (§7.3, two independent defects), and a `Leased` adapter's
pipeline **cannot be deployed** through the normal deploy path (§5.2). The walk below only completed
because of the local patch reproduced in §7.4, which was reverted afterwards. Nothing in the estate
has ever run a pool member before this walk. The full list is in §9.

---

## 0. What has to be true first

- The whole dev checkout builds: `invoke-buildall -branch dev -configuration DebugL -excludeFrontend $true`.
  26 repos, 0 failures. Leasing spans the SDK, the adapter, the controller and the CK model, so a
  partial build will fail somewhere unhelpful.
- Infrastructure is up (MongoDB, RabbitMQ, CrateDB). ⚠️ **There are two mutually exclusive ways to
  provide it** and they bind the same host ports (27017 / 5672 / 15672 / 5432 / 4301): the
  `docker compose` stack (`Start-OctoInfrastructure`) and the local **kind** cluster's `octo-infra`
  namespace (`Install-OctoKubernetes`, `extraPortMappings`). `Start-OctoInfrastructure` refuses to
  start while a `*-control-plane` container is running, and that refusal is the *only* hint you get.
  Check which one holds the local data before switching: `docker volume ls | grep mongo` versus
  `kubectl --context=kind-kind -n octo-infra get pods`. Either way, `Start-Octo`'s host processes
  talk to `localhost:27017` and neither knows nor cares which side answers.
- ⚠️ `kind-control-plane` has restart policy `on-failure:1`, so Docker Desktop brings it back after a
  reboot and it re-takes the ports. `docker stop` alone is not enough if something restarts it;
  `docker update --restart=no kind-control-plane` first, and put the policy back afterwards.
- ⚠️ If the docker-compose Mongo restarts in a loop with *"Read security file failed … /data/file.key:
  bad file"*, `octo-tools/infrastructure/file.key` is **missing** and Docker has created a *directory*
  in its place. The key is generated once by `Install-OctoInfrastructure` and is not in git. Remove
  the bogus directory, regenerate 741 random bytes base64 into `file.key`, and restart. It is the
  replica set's internal-auth key only — regenerating it does not touch data.
- ⚠️ With fresh Mongo volumes you also need `Install-OctoInfrastructure` (replica-set `rs.initiate`
  plus the `octo-system-admin` user), not just `Start-OctoInfrastructure`. And a fresh system
  database means a fresh identity database: every stored `octo-cli` refresh token is void and
  `LogIn` falls back to an interactive device flow, which no headless run can complete. Keeping the
  existing infrastructure is therefore much cheaper than recreating it.

### 0a. 🔴 NEW — the kind infra probes are set to time out, and the symptom points at the wrong layer

Observed 2026-09-15 and it cost the first 40 minutes of the walk. Every call into the asset
repository returned

```
ASSET1002 / MONGO_CONNECTION / END_OF_STREAM
MongoDB.Driver.MongoConnectionException: An exception occurred while receiving a message from the server
 ---> System.IO.EndOfStreamException: Attempted to read past the end of the stream.
```

Mongo was **healthy** — `kubectl exec mongodb-0 -- mongosh --eval 'db.adminCommand({ping:1})'`
answered `ok: 1` with 350 live connections. The break was in front of it:

```
$ kubectl --context=kind-kind -n octo-infra get pods
mongodb-0      0/1  Running
rabbitmq-...   0/1  Running

$ kubectl --context=kind-kind -n octo-infra get endpoints
mongodb-ext    <none>          # no endpoints -> kube-proxy rejects the NodePort
rabbitmq       <none>
```

Root cause: `octo-tools/kubernetes/infra/mongodb.yaml` and `rabbitmq.yaml` declare **exec** readiness
probes (`mongosh --eval …`, `rabbitmq-diagnostics -q ping`) and set **no `timeoutSeconds`**, so
Kubernetes applies its default of **1 second**. Measured on this machine, `mongosh` alone needs
~1.4 s to start. The pods therefore never become Ready, the Services lose their endpoints, and every
host process talking to `localhost:27017` gets a connection that is accepted and immediately closed —
which the .NET driver reports as `EndOfStreamException`, i.e. as if Mongo were corrupt.

Two fixes, and they are not equivalent:

```bash
# non-disruptive unblock, no pod restart, takes effect in ~2 s
kubectl --context=kind-kind -n octo-infra patch svc mongodb-ext -p '{"spec":{"publishNotReadyAddresses":true}}'
kubectl --context=kind-kind -n octo-infra patch svc mongodb     -p '{"spec":{"publishNotReadyAddresses":true}}'
kubectl --context=kind-kind -n octo-infra patch svc rabbitmq    -p '{"spec":{"publishNotReadyAddresses":true}}'
```

That is what this walk used. The **real** fix is `timeoutSeconds: 5` (and ideally
`failureThreshold: 6`) on both probes in `octo-tools/kubernetes/infra/*.yaml`; it restarts the pods,
so do it deliberately rather than in the middle of something else.

## 1. Bring the services up

`Start-Octo` — identity, asset repository, communication controller and friends on ports 50xx.
Do **not** kill individual services under a running `Start-Octo`; restart the set.

- ⚠️ `-reportingService $true` fails on the `dev` checkout: `octo-report-services` is not one of its
  20 repos and the job dies with *"Cannot find path …/octo-report-services/bin/DebugL/net10.0/"*.
  It takes the whole `Start-Octo` set down with it, because one Completed job ends the run. Leave
  reporting off and import `System.Reporting` from the `main` checkout's compiled
  `ck-system.reporting-2.yaml` with `octo-cli -c ImportCk` if a tenant needs it.
- ⚠️ Starting the services before Mongo's replica set is actually up gives a misleading
  `Hangfire.Mongo.MongoConnectException` ("Did not receive ping response … within 5000ms") in
  `BotServices.log`, and again the whole set exits. Wait for the containers, then start.
- The binary that `Start-Octo` runs is `<branch>/<repo>/bin/DebugL/net10.0/`; `octo-cli` on PATH may
  come from a *different* checkout (the octo-tools profile appends
  `meshmakers/octo-cli/bin/Release/...`). Check the "Executable directory:" line it prints, and use
  `dev/octo-cli/bin/DebugL/net10.0/osx-arm64/octo-cli` explicitly when the CK engine version matters.
- 🔴 **`Start-Octo`'s own mesh-adapter job passes a switch that no longer exists.** `Start-Octo.psm1`
  starts it with `--Adapter:TenantId=$meshAdapterTenantId`, but increment 3 **deleted**
  `AdapterOptions.TenantId` and replaced it with `DedicatedTenantId`. The binding is silently
  dropped and the adapter falls back to the hard-coded default `"meshTest"`. Unrelated to leasing,
  but it is in the same file you will be reading in §7.

## 2. Two tenants, and one must be the parent of the other

Lending never flows upwards and never sideways between roots (concept §3): a borrower must be a
**descendant** of the lender.

🔴 **CORRECTED — verify the parent link in the registry, not by assumption.** The resolver
(`TenantLendingScopeResolver.WalkAsync`) does **not** read `octosystem`'s tenant list. It opens the
**lender's own** tenant context and calls `GetDirectChildTenantsAsync`, i.e. it reads
`RtEntity_SystemTenant` inside the *lender's* database. Both of these must agree, and only the
second one is what lending actually uses:

```bash
mongosh "mongodb://octo-system-admin:OctoAdmin1@localhost:27017/admin" --quiet --eval '
  db.getSiblingDB("octosystem").RtEntity_SystemTenant.find({},{attributes:1})
    .forEach(x => print(JSON.stringify(x.attributes)));'
# -> {"tenantId":"salzburgdev","parentTenantId":"accounting","databaseName":"salzburgdev"}

mongosh "mongodb://octo-system-admin:OctoAdmin1@localhost:27017/admin" --quiet --eval '
  db.getSiblingDB("accounting").RtEntity_SystemTenant.find({},{attributes:1})
    .forEach(x => print(JSON.stringify(x.attributes)));'
# -> must list the borrower; this is the list the lending walk reads
```

If the borrower is not in the lender's subtree the scheduler correctly refuses every lease with
`LendingScopeDenied`, and the refusal is easy to misread as a bug.

### 2a. 🔴 NEW — getting an `octo-cli` context for the borrower without an interactive login

A stored `local_<tenant>` context whose refresh token has expired cannot be repaired headlessly:
`LogIn` falls back to a device flow. Two ways round it, both used in this walk:

1. **An ancestor admin's token already works on the descendant's routes.** The `local_accounting`
   user token carries `allowed_tenants: [accounting, …, salzburgdev, …]`, and both
   `POST https://localhost:5001/tenants/salzburgdev/graphQl` and
   `GET https://localhost:5015/salzburgdev/v1/...` accept it (HTTP 200). Good enough for every read
   and for the controller REST endpoints; `octo-cli` itself still wants a context per tenant.
2. **A client-credentials context**, which is additive and removable:

```bash
octo-cli -c AddContext -n ab4924_salzburgdev \
  -isu https://localhost:5003/ -asu https://localhost:5001/ -csu https://localhost:5015/ \
  -bsu https://localhost:5009/ -aisu https://localhost:5019/ -tid salzburgdev
octo-cli --context ab4924_salzburgdev -c LogInClientCredentials -id claude-agent -s "$SECRET"
# ... and afterwards
octo-cli -c RemoveContext -n ab4924_salzburgdev
```

The token it issues carries `tenant_id: salzburgdev` and `scope: octo_api`, which is all the
controller's `SystemCommunicationApiReadWritePolicy` requires (`Program.cs` only asserts the scope
claim — **no role is checked** on that policy). `ImportRt`, `DeployPipeline`, `ExecutePipeline`,
`GetCommunicationLifecycle` and `SetCommunicationLifecycle` all worked with it.

⚠️ Never use `-c UseContext`; it mutates the shared `ActiveContext`. `--context <name>` is per-call
and prints *"(active context '…' unchanged)"* to prove it.

## 3. The CK model

`System.Communication` is at **4.0.0** locally (999.0.0 packages). Both tenants need it.
The migration `3.35.0-to-4.0.0` renames `Pool` → `DeploymentSite` and the `Manages`/`ManagedBy`
roles → `Hosts`/`HostedBy`; the role half needs the `RenameAssociationRole` transform, which exists
only in the locally built engine.

Check the result from the tenant's GraphQL endpoint rather than from a log:

```graphql
{ runtime { systemCommunicationDeploymentSite { totalCount } } }
```

An answer means 4.0.0 is live. If `systemCommunicationPool` answers instead, the model did not
install — and if the *new* name is absent the endpoint returns **HTTP 400**, not an empty result, so
a naive "did it error?" check reads backwards.

### 3a. 🔴 NEW — four GraphQL shapes that will waste your time

The runtime schema is not the shape the concept documents imply. All four were hit in this walk:

| You will write | It is actually | Error you get |
|---|---|---|
| `{ … { nodes { … } } }` | **`items`** | `Cannot query field 'nodes' on type 'SystemCommunicationAdapterConnection'` |
| `hostedBy { items { … } }` | `hostedBy(ckTypeIds: ["System.Communication/DeploymentSite"]) { items { … } }` | `Argument 'ckTypeIds' … is required` |
| `fieldFilters: [ … ]` | **`fieldFilter: { … }`** (singular, one object) | `Unknown argument 'fieldFilters' … Did you mean 'fieldFilter'` |
| `finishedAt` on an execution | **`completedAt`** | `Cannot query field 'finishedAt'` |

Enums come back SCREAMING_SNAKE (`LEASED`, `QUEUED`, `DESCENDANTS`, `QUEUE_DEPTH_OR_WAIT_SECONDS`).

✅ **`System.Ai` is at 4.0.0 and installs on a 4.0.0 tenant.** ⚠️ **The tenant feature reported
*AI Services: Enabled* the whole time the model was absent**, so `GetTenantFeatures` is not evidence
that a model is installed — list the models. Worth keeping because it generalises: a frontend
`schema.graphql` introspected from a tenant with a missing model silently loses every type of that
model, without any error anywhere.

## 4. An `AdapterPool` in the lender

There is no `CreateAdapterPool` CLI verb.

- **Refinery Studio** has authoring screens since increment 10 (*Communication → Adapter Pools →
  New*, plan §11a.2b). ⚠️ **Not exercised by this walk** — the `ImportRt` route was taken instead
  because it is scriptable, re-runnable and diffable, and because the runbook needed a worked
  example that did not depend on a browser. The Studio route is therefore still unwalked.
- ✅ **`octo-cli -c ImportRt`** — what this walk used, verbatim and working:

```yaml
# rt-ab4924-pool-accounting.yaml  — import as the LENDER
$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
dependencies:
  - System.Communication-[4.0,5.0)
entities:
  - rtId: 49240000000000000000aa01
    ckTypeId: System.Communication/AdapterPool
    attributes:
      - { id: System/Name,        value: AB4924 Test Pool }
      - { id: System/Description, value: Hand-run pool for the first end-to-end leasing walk. }
      - { id: System.Communication/MinReplicas,                         value: 1 }
      - { id: System.Communication/MaxReplicas,                         value: 2 }
      - { id: System.Communication/PoolMemberCpuRequest,                value: 100m }
      - { id: System.Communication/PoolMemberCpuLimit,                  value: 500m }
      - { id: System.Communication/PoolMemberMemoryRequest,             value: 256Mi }
      - { id: System.Communication/PoolMemberMemoryLimit,               value: 512Mi }
      - { id: System.Communication/PoolScaleUpPolicy,                   value: 0 }   # QueueDepthOrWaitSeconds
      - { id: System.Communication/ScaleUpQueueDepthThreshold,          value: 5 }
      - { id: System.Communication/ScaleUpQueueWaitSeconds,             value: 60 }
      - { id: System.Communication/AdapterSharingMode,                  value: 1 }   # 🔴 1 = Descendants
      - { id: System.Communication/LendingMaxConcurrentLeasesPerTenant, value: 2 }
    associations:
      - roleId: System.Communication/Hosts
        targetRtId: '670000000000000000000001'          # the tenant's Default Cloud deployment site
        targetCkTypeId: System.Communication/DeploymentSite
```

```bash
octo-cli --context local_accounting -c ImportRt -f rt-ab4924-pool-accounting.yaml -w
```

🔴 **`AdapterSharingMode` must not be left at its default.** The default is `0` = `NotShared`, and
`TenantLendingScopeResolver.ResolveLendableTenantsAsync` **returns an empty set without walking the
tree at all** for `NotShared`. A pool created with everything else right but this left out lends to
nobody, and the refusal (`LendingScopeDenied`) names the tenant tree, which sends you looking in the
wrong place.

Note the attribute id is `System.Communication/PoolScaleUpPolicy` but the *attribute name* on the
type is `ScaleUpPolicy`, and likewise `AdapterSharingMode` → `SharingMode`. Runtime YAML uses the
**id**; GraphQL uses the **name**.

Verify, and take the rtId from here rather than from the file:

```bash
curl -sk -X POST https://localhost:5001/tenants/accounting/graphQl -H "Authorization: Bearer $TOK" \
  -H 'Content-Type: application/json' -d '{"query":"{ runtime { systemCommunicationAdapterPool { items { rtId name sharingMode minReplicas maxReplicas } } } }"}'
# {"rtId":"49240000000000000000aa01","name":"AB4924 Test Pool","sharingMode":"DESCENDANTS","minReplicas":1,"maxReplicas":2}
```

**Do not deploy the pool.** `DeployWorkload` on it goes to the Communication Operator, which locally
means kind. Running the member by hand (§6) is the honest instrument anyway.

## 5. A `Leased` adapter and one pipeline in the borrower

```yaml
# rt-ab4924-borrower-salzburgdev.yaml — import as the BORROWER
$schema: https://schemas.meshmakers.cloud/runtime-model.schema.json
dependencies:
  - System.Communication-[4.0,5.0)
entities:
  - rtId: 49240000000000000000bb01
    ckTypeId: System.Communication/Adapter
    attributes:
      - { id: System/Name, value: AB4924 Leased Adapter }
      - { id: System.Communication/LifecycleMode,    value: 3 }        # 🔴 3 = Leased
      - { id: System.Communication/LentFromTenantId, value: accounting }
      - { id: System.Communication/LentFromPoolRtId, value: 49240000000000000000aa01 }
    associations:
      - roleId: System.Communication/Hosts
        targetRtId: '670000000000000000000001'
        targetCkTypeId: System.Communication/DeploymentSite
  - rtId: 49240000000000000000bb02
    ckTypeId: System.Communication/DataFlow
    attributes:
      - { id: System/Name, value: AB4924 Leasing Walk }
  - rtId: 49240000000000000000bb03
    ckTypeId: System.Communication/Pipeline
    associations:
      - { roleId: System/ParentChild,            targetRtId: 49240000000000000000bb02, targetCkTypeId: System.Communication/DataFlow }
      - { roleId: System.Communication/Executes, targetRtId: 49240000000000000000bb01, targetCkTypeId: System.Communication/Adapter }
    attributes:
      - { id: System/Name,    value: AB4924 Leasing Probe }
      - { id: System/Enabled, value: true }
      - { id: System.Communication/DeploymentState, value: 0 }
      - id: System.Communication/PipelineDefinition
        value: |
          triggers:
            - type: FromExecutePipelineCommand@1
          transformations:
            - type: SetPrimitiveValue@1
              targetPath: $.marker
              valueType: String
              value: AB4924-LEASED-PROBE-OK
            - type: GetRtEntitiesByType@1
              description: Read the adapters of WHICHEVER tenant database this actually runs against
              ckTypeId: System.Communication/Adapter
              targetPath: $.adaptersSeen
              identity: ServiceAccount
            - type: SetPipelineExecutionResult@1
              path: $
              maxLength: 20000
```

**Make the pipeline's output prove *which tenant database* it ran against**, not just that it ran.
A `GetRtEntitiesByType@1` over a type whose population differs between the two tenants is the
cheapest such probe: `accounting` has 1 adapter, `salzburgdev` has 3, so the output alone settles
the isolation question (§8, evidence 6).

### 5.1 🔴 NEW — the borrower's `PipelineServiceAccount` has to be provisioned by hand

Without it the lease is refused with `BorrowerCredentialMissing`. It is normally created by
`DeployWorkloadAsync`, which a `Leased` adapter never goes through. There is **no `octo-cli` verb**;
use the idempotent controller endpoint directly:

```bash
curl -sk -X POST -H "Authorization: Bearer $TOK" -H 'Content-Length: 0' \
  https://localhost:5015/salzburgdev/v1/adapter/49240000000000000000bb01/serviceAccount/reconcile
# {"outcome":"Provisioned","clientId":"octo-pipeline-sa-49240000000000000000bb01",
#  "configurationWellKnownName":"pipeline-service-account-49240000000000000000bb01","roleChangesSkipped":false, ...}
```

The client is created in the **borrower's** identity database with grant types
`client_credentials, impersonate, on-behalf-of` and gets the `CommunicationManagement` role.

### 5.2 🔴 CORRECTED — `DeployPipeline` does not work on a `Leased` adapter, and does not need to

```
$ octo-cli --context <borrower> -c DeployPipeline -aid 49240000000000000000bb01 -pid 49240000000000000000bb03 -f probe.yaml
NotFound: {"errorMessage":"[salzburgdev] Adapter 'System.Communication/Adapter@49240000000000000000bb01' has no
live SignalR connection. The adapter pod must be deployed and online before its pipeline configuration can be
pushed. Deploy the adapter first via the 'Deploy Adapter' action (or 'Pool → Deploy Workload' on the API), then
retry 'Update Configuration'."
```

This is not a misconfiguration. `AdapterService.DeployPipelineAsync` starts with
`adapterCache.TryGetTenant(...)` / `adapterTenant.AdapterById.TryGetValue(...)`, and
`AdapterTenant.AddAdapter(rtEntityId, connectionId, configuration)` only ever inserts an adapter that
has an **open `/{tenantId}/adapterHub` connection**. A `Leased` adapter is by definition a workload
with no process of its own, so it can never be in that cache. There is no `Leased` branch anywhere
in that method. **See §9, defect 3.**

The lease path does not care: `AdapterService.GetLeasedPipelineConfigurationAsync` requires only that
the pipeline exists, is `Enabled`, carries a definition, has an `Executes` edge to the adapter and has
a parent `DataFlow`. It never looks at `DeploymentState`. The `ImportRt` above satisfies all five, so
**skip the deploy** — but know what you lose by skipping it:

- `Pipeline.ExecutionClass` stays at the CK default **`Batch`**. It is resolved only inside
  `SetPipelineDefinitionAsync`, i.e. only on deploy (plan §6.4 says so for GraphQL writes; it applies
  to `ImportRt` identically). Every queue entry in this walk therefore read `class=Batch`, including
  the manual `FromExecutePipelineCommand@1` run that the plan classifies `Interactive`.
- `Adapter.OnDemandCapable` stays **false**, because it is refreshed only by
  `onDemandCapabilityService.RefreshWorkloadCapabilityAsync` at the end of a deploy. `LeaseService`
  does not check it, so the lease is granted anyway — but `PoolService`'s deploy guard
  (`Leased && !OnDemandCapable` → reject) would refuse this adapter, which means the authored state
  and the validated state disagree.

## 6. Turn leasing on — **on both tenants**

```bash
octo-cli --context local_accounting     -c SetCommunicationLifecycle -le true   # lender
octo-cli --context ab4924_salzburgdev   -c SetCommunicationLifecycle -le true   # borrower
octo-cli --context local_accounting     -c GetCommunicationLifecycle
# Communication lifecycle for tenant 'accounting': ScaleToZeroEnabled=False, LeasingEnabled=True
```

Both halves are required and the refusal names which one is missing. Default is off.

🔴 **Budget ~40 s, not 30 s, for a change to take effect.** `LifecycleConfigurationService` caches
for 30 s and `LeaseSchedulerBackgroundService` runs every 5 s, and the two do not line up. Measured
on the `-le false` → `-le true` transition in this walk: `LeaseWaitMs = 41970`.

Turning it off **holds** the queue exactly as documented — see §8, evidence 7.

## 7. Start a member process

🔴 **CORRECTED — the command in the previous version of this document does not work, and neither
does the configuration it lists.** Three separate things were wrong.

### 7.1 `dotnet run --project src/MeshAdapter` is the wrong binary

`MeshAdapter.csproj` hard-codes `<OutputPath>..\..\bin\$(Configuration)\</OutputPath>`, so
`src/MeshAdapter/bin/` is **empty** and the real output is the repo-root `bin/DebugL/net10.0/`.
Run the DLL that `Start-Octo` would run, which also avoids rebuilding under a live `Start-Octo`:

```bash
cd dev/octo-mesh-adapter/bin/DebugL/net10.0
dotnet Meshmakers.Octo.MeshAdapter.dll --urls="http://0.0.0.0:5031"
```

Pick ports that are free; 5020/5021 belong to `Start-Octo`'s own mesh-adapter job.

### 7.2 🔴 A pool member DOES need `Adapter:DedicatedTenantId`, set to the LENDING tenant

The previous version said *"There is deliberately **no** `DedicatedTenantId` here"*, and
`AdapterPoolMemberOptions`' own XML doc says the same. **Both are wrong about what the running code
needs.** `ConfigureAdapterAuthenticatorOptions` is the *only* place that fills
`AuthenticatorOptions.TenantId`, and that is the *only* thing that puts `acr_values=tenant:{id}` on
the member's own client-credentials request. Leave it unset and `AdapterOptions`' constructor default
`"meshTest"` applies, the member's management token carries `tenant_id: meshtest`, and
`AdapterPoolHub.CheckConnectionTenantBinding` rejects the registration — or, on the default
`AdapterPoolHubAuthorizationMode.LogOnly`, silently accepts it and only logs what an enforcing run
would have refused.

Working configuration, as run:

```bash
export ASPNETCORE_ENVIRONMENT=Development
export OCTO_SYSTEM__SYSTEMDATABASENAME=OctoSystem
export OCTO_SYSTEM__ADMINUSERPASSWORD=OctoAdmin1
export OCTO_SYSTEM__DATABASEUSERPASSWORD=OctoUser1
export OCTO_SYSTEM__USEDIRECTCONNECTION=true

export OCTO_ADAPTERPOOL__POOLTENANTID=accounting
export OCTO_ADAPTERPOOL__POOLRTID=49240000000000000000aa01
export OCTO_ADAPTERPOOL__MEMBERID=octo-pool-0

export OCTO_ADAPTER__COMMUNICATIONCONTROLLERSERVICESURI=https://localhost:5015
export OCTO_ADAPTER__IGNORECERTIFICATEVALIDATION=true
export OCTO_ADAPTER__ISSUERURI=https://localhost:5003/
export OCTO_ADAPTER__CLIENTID=claude-agent          # any client_credentials client IN THE LENDER
export OCTO_ADAPTER__CLIENTSECRET=...
export OCTO_ADAPTER__DEDICATEDTENANTID=accounting   # 🔴 the LENDER; see above

export Logging__LogLevel__Default=Debug             # the lease lifecycle lines are DEBUG
```

The policy the pool hub enforces (`SystemCommunicationApiReadWritePolicy`) asserts only the
`octo_api` scope claim, so the member's own client needs **no role**.

### 7.3 🔴 WAS A BLOCKER — fixed after this walk, verified unpatched on 2026-09-15

Both defects below are **fixed** (`octo-communication-sdk` `26e48f2` for the cycle and the lending
tenant, `6fb32e7` for the options binding). A member started from the committed code on the evening
of 2026-09-15 with no patch at all and registered with 117 node descriptors — see §11. The failure
description is kept because the *shape* of the failure is the lesson: neither defect produced an
error, and a registration-counting DI sweep walks straight past both.

Two independent defects, both silent. With either one present you get **no error**: the process
starts, prints two lines, and sits there for ever.

**Defect 1 — a singleton dependency cycle in `AddAdapterPoolMember()`.**

```
AdapterPoolClient(IAdapterLeaseScope, IAdapterPoolHubClient, …)
  -> IAdapterPoolHubClient  = AdapterPoolHubClient(IOptions<…>, ILogger, IServiceClientAccessToken, IAdapterPoolHubCallbacks)
    -> IAdapterPoolHubCallbacks = p => p.GetRequiredService<AdapterPoolClient>()     // AdapterPoolServiceCollectionExtensions.cs:~74
      -> AdapterPoolClient …
```

`CallSiteFactory`'s cycle detection cannot see through a factory lambda, so nothing throws
*"A circular dependency was detected"*. The recursion happens at resolution time, and
`ServiceLookup.StackGuard.RunOnEmptyStack` keeps moving it onto fresh threads, so it never even
`StackOverflow`s. Symptom, in full:

```
2026-09-15 09:35:10.0047| INFO|Octo Adapter, Version 999.0.0.0
2026-09-15 09:35:10.0322| INFO|Development Version
2026-09-15 09:35:12.2845|DEBUG|Hosting starting
                                                  <- nothing, ever; 0.6 s CPU over 10 minutes
```

`dotnet-dump collect` + `clrstack` shows the loop verbatim:

```
System.Threading.WaitHandle.WaitOneNoCheck(…)
Microsoft.Extensions.DependencyInjection.ServiceLookup.StackGuard.RunOnEmptyStackCore[…]
…ServiceProvider.CreateServiceAccessor(…)
…ServiceProviderServiceExtensions.GetRequiredService[…](IServiceProvider)
Meshmakers.Octo.Sdk.Common.Adapters.AdapterPoolServiceCollectionExtensions+<>c.<AddAdapterPoolMember>b__0_3(IServiceProvider)
…CallSiteRuntimeResolver.VisitConstructor(…)      <- repeats for ever
```

**Defect 2 — `AdapterPoolMemberOptions` is never bound to configuration.**
`AddAdapterPoolMember()` calls `services.AddOptions<AdapterPoolMemberOptions>()` and stops there.
All three hosts (`AdapterBuilder`, `WebAdapterBuilder`, `MeshAdapter/Program.cs`) `Bind()` a **local**
instance used only for the `if (poolMemberOptions.IsEnabled)` decision. `IOptions<AdapterPoolMemberOptions>`
therefore always resolves to an all-default object, and the member says so and does nothing:

```
2026-09-15 09:46:21.4536| WARN|Adapter pool member service started without a configured pool;
                               set OCTO_ADAPTERPOOL__POOLTENANTID and OCTO_ADAPTERPOOL__POOLRTID. Doing nothing.
```

`AdapterPoolHubClientOptions` gets a null `PoolTenantId` / `PoolRtId` from the same source.

### 7.4 The local patch this walk used, and what it is not

🔴 **Obsolete — do not apply it.** The real fix landed in `octo-communication-sdk` (`26e48f2`), where
it also covers the `octo-adapter-*` hosts, and the second walk (§11) started a member without it.
Kept only as the record of how the first walk got through.

Reproduced so the walk can be repeated. **It is a diagnostic crutch, not a proposed fix** — the real
fix belongs in `octo-communication-sdk` (`AddAdapterPoolMember`), where it would also cover
`octo-adapter-*` hosts, and both halves want a test at the composition-root level rather than at the
registration level. Applied to `octo-mesh-adapter`, built to a scratch output directory, and reverted.

```diff
--- a/src/MeshAdapter.Sdk/Leasing/MeshAdapterPoolServiceCollectionExtensions.cs
+++ b/src/MeshAdapter.Sdk/Leasing/MeshAdapterPoolServiceCollectionExtensions.cs
@@
         services.TryAddSingleton<IAdapterLeaseWorkItem, LeasedPipelineWorkItem>();
+
+        // Breaks the construction-time cycle: TryAdd inside AddAdapterPoolMember() becomes a no-op.
+        services.AddSingleton<IAdapterPoolHubCallbacks>(sp => new DeferredAdapterPoolHubCallbacks(sp));
 
         services.AddAdapterPoolMember();
+
+internal sealed class DeferredAdapterPoolHubCallbacks : IAdapterPoolHubCallbacks
+{
+    private readonly IServiceProvider _serviceProvider;
+    public DeferredAdapterPoolHubCallbacks(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;
+    public Task LeaseAsync(LeaseDto lease)  => _serviceProvider.GetRequiredService<AdapterPoolClient>().LeaseAsync(lease);
+    public Task DrainAsync(string reason)   => _serviceProvider.GetRequiredService<AdapterPoolClient>().DrainAsync(reason);
+}

--- a/src/MeshAdapter/Program.cs
+++ b/src/MeshAdapter/Program.cs
@@
     if (poolMemberOptions.IsEnabled)
     {
+        builder.Services.Configure<AdapterPoolMemberOptions>(options =>
+            builder.Configuration.GetSection(AdapterPoolMemberOptions.SectionName).Bind(options));
         builder.Services.AddOctoMeshAdapterPoolMember();
     }
```

Build it without touching the repo's `bin/` or either local NuGet feed, then swap in only the two
assemblies you changed:

```bash
dotnet build src/MeshAdapter/MeshAdapter.csproj -c DebugL -p:GeneratePipelineSchema=false \
  -p:OutputPath=$SCRATCH/build-out/ -p:BaseOutputPath=$SCRATCH/build-base/
rsync -a dev/octo-mesh-adapter/bin/DebugL/net10.0/ $SCRATCH/member-bin/
cp $SCRATCH/build-out/Meshmakers.Octo.{MeshAdapter,Sdk.MeshAdapter}.{dll,pdb} $SCRATCH/member-bin/
```

⚠️ Verify afterwards that `Meshmakers.Octo.Sdk.Adapters.dll` in `member-bin` is **byte-identical** to
the one in the repo's `bin/DebugL/net10.0/`. The default `NuGet.Config` points `local-nuget` at
**`main/nuget`**, not `dev/nuget`, so a restore can silently pull main-branch 999.0.0 packages — which
have no leasing in them at all.

### 7.5 What a healthy member start looks like

```
| INFO|Adapter access token acquired for client claude-agent in tenant 'accounting', expires at …
| INFO|Starting as member 'octo-pool-0' of adapter pool 49240000000000000000aa01 in lending tenant 'accounting'
| INFO|SignalR connection started, calling connect function
| INFO|Registered as member 'octo-pool-0' of adapter pool 49240000000000000000aa01 in tenant 'accounting'
```

and from the lender's side:

```bash
curl -sk -H "Authorization: Bearer $TOK" \
  https://localhost:5015/accounting/v1/adapterPool/49240000000000000000aa01/members
# [{"memberId":"octo-pool-0","activeLeaseId":null,"activeLeaseTenantId":null,"isDraining":false,
#   "lastSeenUtc":"2026-09-15T07:47:56.048152Z"}]
```

A second member is just a second process with a different `MEMBERID`.

## 8. Give the borrower some work, and watch it

```bash
octo-cli --context ab4924_salzburgdev -c ExecutePipeline -id 49240000000000000000bb03
# {"id":"71592b0d-1a0b-4e84-9b2f-cb676382b337","dateTime":"2026-09-15T07:48:11.236387Z","status":null, ...}
```

🔴 `dateTime` in that answer is the **queue** time, not a start time. `StartedAt` is deliberately
unset on a queued execution (concept §8, Q5).

The seven things to look at, all as observed on 2026-09-15:

**1 — the execution is created `Queued`, not sent to the execute queue.** Read it from the borrower's
GraphQL, not from a log:

```json
{ "executionId": "cc50af16-8acc-457d-a00c-7fc0b973b561", "status": "QUEUED",
  "queuedAt": "2026-09-15T07:51:05.654Z", "startedAt": null, "leaseGrantedAt": null,
  "leaseReleasedAt": null, "leaseWaitMs": null, "leasedFromTenantId": null, "leasedOnMemberId": null }
```

**2 — the queue shows position within the tenant plus tenants ahead, never a global rank.**

```
$ octo-cli --context local_accounting -c GetAdapterPoolQueue -id 49240000000000000000aa01
Adapter pool '49240000000000000000aa01': 0 leased, 1 waiting.
  WAITING 71592b0d-…  tenant=salzburgdev  pipeline=AB4924 Leasing Probe  class=Batch
          queuedAt=2026-09-15T07:48:11.2360000Z  position=1 in its tenant, 0 tenant(s) ahead in the rotation

$ octo-cli --context local_accounting -c GetAdapterPoolQueue -id 49240000000000000000aa01 -j
[{"ExecutionId":"cc50af16-…","BorrowerTenantId":"salzburgdev","PipelineRtId":"49240000000000000000bb03",
  "PipelineName":"AB4924 Leasing Probe","ExecutionClass":1,"QueuedAtUtc":"2026-09-15T07:51:05.654Z",
  "PositionInTenant":1,"TenantsAheadInRotation":0,"LeasedOnMemberId":null,"LeaseExpiresAtUtc":null,"IsLeased":false}]
```

**3 — the grant.** Controller, within one 5 s round:

```
| INFO|LeaseService.GrantLeaseAsync|Granted lease 'a2299f4b18d94dd7b1e84ce795d7a85d' of pool 49240000000000000000aa01
       (tenant 'accounting') to tenant 'salzburgdev' on member 'octo-pool-0', expires 2026-09-15T08:03:13.7351640Z
| INFO|LeaseSchedulerService.ScheduleForPoolAsync|Adapter pool … granted 1 lease(s) across 1 borrowing tenant(s);
       0 work item(s) still queued
```

and on the entity — this is where the evidence belongs, not in the log:

```json
{ "status": "COMPLETED", "triggerType": "MANUAL",
  "queuedAt":        "2026-09-15T07:48:11.236Z",
  "leaseGrantedAt":  "2026-09-15T07:48:13.737Z",
  "startedAt":       "2026-09-15T07:48:13.737Z",
  "leaseReleasedAt": "2026-09-15T07:48:19.062Z",
  "completedAt":     "2026-09-15T07:48:19.155Z",
  "leaseWaitMs": 2501, "durationMs": 5418,
  "leasedFromTenantId": "accounting", "leasedFromPoolRtId": "49240000000000000000aa01",
  "leasedOnMemberId": "octo-pool-0" }
```

`LeaseWaitMs` = `LeaseGrantedAt − QueuedAt` exactly (2501 ms). `StartedAt == LeaseGrantedAt`, which
is what makes `LeaseReleasedAt − LeaseGrantedAt` minus the pipeline's own run the pool overhead.

**4 — the member really runs the borrower's pipeline.**

```
| INFO|Taking lease 'a2299f4b…' for tenant 'salzburgdev' from pool 49240000000000000000aa01 of tenant 'accounting', expires …
| INFO|This pool member now acts as client 'octo-pipeline-sa-49240000000000000000bb01' of leased tenant 'salzburgdev'
|DEBUG|CK model cache warmed for leased tenant 'salzburgdev'
| INFO|Registering multiple pipelines for tenant salzburgdev. Pipeline count: 1
| INFO|Running leased pipeline System.Communication/Pipeline@49240000000000000000bb03 for tenant 'salzburgdev'
       as execution '71592b0d-…' under lease 'a2299f4b…'
| INFO|Resolved service account identity for client octo-pipeline-sa-49240000000000000000bb01 in tenant 'salzburgdev':
       subject octo-pipeline-sa-49240000000000000000bb01 with 1 role(s)
| INFO|PipelineExecution/SetPipelineExecutionResult@1: Pipeline execution result set (3877 characters)
| INFO|PipelineExecution: Pipeline completed
```

**5 — the release.** Member → controller, and the pipeline's output rides home on it:

```
|DEBUG|Received hub invocation: ReleaseLeaseAsync [ LeaseResultDto { LeaseId = a2299f4b…, Reason = Completed,
       Success = True, StatusMessage = Executed pipeline 49240000000000000000bb03 as execution '71592b0d-…'.,
       OutputData = {"marker":"AB4924-LEASED-PROBE-OK", …}, WorkDurationMs = 817, ReleasedAtUtc = … } ]
| INFO|LeaseService.ReleaseLeaseAsync|Lease 'a2299f4b…' of tenant 'salzburgdev' released by its member: Completed, success=true
```

then, on the member, in this order — and the order is the isolation invariant, not housekeeping:

```
|DEBUG|Pipeline registrations dropped for released tenant 'salzburgdev'
|DEBUG|CK model cache unloaded for released tenant 'salzburgdev'
|DEBUG|Borrower identity dropped after releasing tenant 'salzburgdev'
```

**6 — 🔴 the isolation claim, checked rather than assumed.** Four independent observations, none of
them "no errors appeared":

- *The token.* `IdentityServices.log` carries the request and the issued JWT verbatim. Request:
  `{"acr_values": "tenant:salzburgdev", "grant_type": "client_credentials", "scope": "octo_api",
  "client_id": "octo-pipeline-sa-49240000000000000000bb01"}`. Decoding the JWT the log prints gives
  exactly: `{"iss":"https://localhost:5003/", "scope":"octo_api", "tenant_id":"salzburgdev",
  "role":"CommunicationManagement", "client_id":"octo-pipeline-sa-49240000000000000000bb01"}` —
  **no `accounting` anywhere in it**, and `OctoAccessTokenShapeHandler` strips `sub`, as designed.
- *The member's log for that execution.* Across the whole process log, `tenant 'accounting'` appears
  **4 times**, all before any lease: the member's own token acquisition, its "Starting as member…",
  its "Registered as member…", and the lease-grant line that names the **lending** tenant. It never
  appears as the executing tenant. Inside the lease window the other 78 hits on the string
  "accounting" are all CK model ids (`Basic.Accounting-1.6.0`, `Meshmakers.Accounting.Tesla-1.1.0`)
  read *out of `$db: "salzburgdev"`*.
- *The database the member actually talked to.* Counting `"$db"` in the rendered Mongo commands
  inside the lease window: `salzburgdev` 128, `octosystem` 69 (the tenant registry), `admin` 9
  (transaction control), **`accounting` 0**. Across the entire member process log,
  `"$db":"accounting"` occurs **zero** times.
- *The pipeline's own output.* `SetPipelineExecutionResult@1` returned
  `{"marker":"AB4924-LEASED-PROBE-OK","adaptersSeen":{"TotalCount":3,"Items":[… "Mesh Adapter" …,
  "FinAPI Adapter" …, "AB4924 Leased Adapter" …]}}`. Those are **salzburgdev's** three adapters;
  `accounting` has exactly one. The run read the borrower's data, not the lender's.

**7 — the kill switch holds the queue, and the refusal is named.** With `-le false` on the **lender**
only, the work item is still enqueued (that half is the borrower's switch) and then refused every
round, verbatim:

```
|DEBUG|LeaseSchedulerService.TryGrantAsync|No lease granted for execution 'cc50af16-…' of tenant 'salzburgdev'
       on pool 49240000000000000000aa01: Adapter pool leasing is disabled for the lending tenant 'accounting'.
       Enable it with octo-cli SetCommunicationLifecycle -le true.
```

(reason `LeaseRefusalReason.LeasingDisabledLender`). It repeated at 09:51:06.4, :11.5, :16.5, :21.5,
:26.6, :31.6 — **the 5 s scheduler round, confirmed**. Nothing was dropped: re-enabling the lender
drained it 42 s later with `leaseWaitMs: 41970`, `status: COMPLETED`.

⚠️ That refusal is logged at **DEBUG**. "Why is my queue not moving" is therefore invisible at the
default log level, and at INFO you see the enqueue and then silence.

### 8.1 Warm-up cost, measured

Same pipeline, same member, two consecutive leases:

| | lease 1 (cold) | lease 2 (warm) |
|---|---|---|
| `durationMs` | 5418 | **405** |
| CK-model warm-up | 4.1 s | 0.14 s |
| Mongo reads on `salzburgdev` | 128 | 125 |
| Mongo reads on `octosystem` | 69 | **6** |

The borrower's own data is fully re-read every lease (128 → 125); what survives is the
tenant-**independent** registry lookup and JIT. That is the shape concept §4 wants, and it is worth
re-measuring whenever the lease participants change.

### 8.2 Cancelling a waiting entry

```bash
octo-cli --context local_accounting -c CancelQueuedExecution -id <poolRtId> -eid <executionId>
```

An entry that already holds a lease answers **409** — interrupting a running pipeline is a different
operation and deliberately not the same verb. ⚠️ **Not exercised by this walk.**

## 9. What is still broken, in priority order

Rewritten 2026-09-15 after §11. Items 1, 2 and 3 are **fixed and verified**; what follows them is
what actually remains.

1. ✅ **`AddAdapterPoolMember()` cannot be resolved** — fixed, `octo-communication-sdk` `26e48f2`
   (`DeferredAdapterPoolHubCallbacks`). A member starts unpatched (§11).
2. ✅ **`AdapterPoolMemberOptions` is never bound** — fixed, `6fb32e7`. Note the sharp edge found
   while fixing it: `Configure<IConfiguration>` makes `IConfiguration` mandatory and breaks every
   composition without one; `Configure<IServiceProvider>` + `GetService<IConfiguration>()` is right.
3. ✅ **`DeployPipeline` has no `Leased` branch** — addressed in `631d0d9` (increment 11). The lease
   path never needed it; what the branch buys is `ExecutionClass` and `OnDemandCapable` resolution.
4. 🔴 **A pool member cannot run as a pod at all.** Two separate gaps, both found on 2026-09-15:
   - The chart has no pool wiring. `octo-mesh-adapter/src/charts/octo-mesh-adapter` contains no
     `ADAPTERPOOL` value, no `extraEnv` escape hatch; `PoolService.AppendAdapterPoolMemberOverrides`
     sets only `replicaCount` and the sizing values. `OCTO_ADAPTERPOOL__POOLTENANTID` / `POOLRTID` /
     `MEMBERID` and `OCTO_ADAPTER__DEDICATEDTENANTID` have no route into the container.
   - The member has no database credential in a cluster — **not even for the registry**. It reads
     `octosystem` (tenant registry + CK model) and calls `listDatabases` against `admin` on **every
     lease**, not just at startup (§11 measures both). The chart's only source for
     `OCTO_SYSTEM__DATABASEUSERPASSWORD` / `__ADMINUSERPASSWORD` is `_env.tpl:29/30` →
     `.Values.secrets.databaseUser` / `databaseAdmin`, i.e. exactly the cluster-secret tier that
     `WorkloadReconciler.AppendClusterSecrets` withholds from an `AdapterPool`. This walk supplied
     them as environment variables, which is precisely what a pod cannot do.
5. 🔴 **A leased member subscribes to the borrower's bus triggers** and leaves the queue behind on
   release — see §11.
6. 🔴 **The scheduler tick is the throughput ceiling**: a release does not wake the scheduler, so
   grants are exactly `LeaseSchedulerIntervalSeconds` apart regardless of how short the work is
   (§11 measures 5.11–5.14 s between grants for runs of 0.43–1.36 s).
7. **`AdapterOptions.DedicatedTenantId` on a pool member** — §7.2. The code requires it; the model
   comments and this runbook said it must not exist. One of the two has to change: either the doc, or
   `AddAdapterPoolMember()` grows its own `IConfigureOptions<AuthenticatorOptions>` that projects
   `AdapterPoolMemberOptions.PoolTenantId` instead.
8. **The lease refusal is DEBUG-only** — §8, evidence 7.
9. **`Start-Octo.psm1` passes `--Adapter:TenantId`**, a property increment 3 deleted — §1.

## 10. The failure modes still unprovoked

- **Stop the member mid-lease.** The execution should be interrupted and **re-queued as a new
  execution entity** (plan §9.7); the interrupted attempt keeps its own lease span for billing.
- ✅ **Two borrowers, one member** — done, §11: enqueued 3+3 in tenant-blocked order and the grants
  came out strictly alternating. ⚠️ Still open on this point: with every pipeline classifying as
  `Batch` (§5.2), the `Interactive`-before-`Batch` half of §9.2 cannot be observed through this route,
  and the starvation shape (200 for one tenant, 1 for the other) was not tried.
- **Let a lease expire** (`LeaseTtlMinutes`, default 15 — confirmed: granted 07:48:13.7, expires
  07:48:13.735 + 15 min = 08:03:13.735). The member is **drained and restarted**, not re-used.
- **Two controller instances**, i.e. the admission gate declining (`AdmissionGateDeclined`). Not
  reachable with one `Start-Octo`.
- **Scale-up.** `MaxReplicas > 1` with queue pressure drives `ScaleWorkloadDto` against the operator,
  which locally means kind.
- **The Refinery Studio authoring route** (§4) and **`CancelQueuedExecution`** (§8.2).

## 11. Second walk, 2026-09-15 evening — a real application pipeline, and two borrowers

Same estate, three questions the first walk left open: does a **real** application pipeline run under
a lease, does the rotation actually rotate, and does one process survive holding **two different**
tenants in sequence. All three were answered, and two new defects fell out.

### 11.1 What was set up

The locally deployed accounting application (`meshmakers-app`) is wired to tenant `salzburgdev`
(`config.json`: login tenant `accounting`, mesh adapter `salzburgdev-670000000000000000000002`), and
`meshmakers` is a second child of `accounting` carrying the same blueprint. Both borrowed from the
`AB4924 Test Pool` of §4.

| Tenant | `Leased` adapter | DataFlow | Pipeline (clone of `Match Transactions`, byte-identical definition) |
|---|---|---|---|
| `salzburgdev` | `…bb01` (from §5) | `…bb02` | `…bb11` |
| `meshmakers` | `…cc01` | `…cc02` | `…cc11` |

🔴 **A clone, not a re-pointed original.** The real pipeline keeps its `Executes` edge to the
dedicated adapter; only the copy is leased. The blueprint owns those entities (`rtBlueprintLocked`),
so a re-point would be undone by the next apply and would silently move production work onto the pool.

Two things the borrower needs, neither of which a `Leased` adapter gets on its own: the service
account (§5.1, `POST {tenant}/v1/adapter/{id}/serviceAccount/reconcile`) and `LeasingEnabled`.
`SetCommunicationLifecycle -le true` alone is safe — it changes only the flags you pass, so
`meshmakers`' `ScaleToZeroEnabled=True` survived.

### 11.2 The triggers do not run on the lease path, and it matters

`LeasedPipelineWorkItem` calls `etlDataOrchestrator.ExecutePipelineAsync(registration.
NodeDefinitionRoot, …)` with the lease's input as the data root. **The trigger nodes never execute.**
A leased pipeline therefore needs no `FromExecutePipelineCommand@1`; the `Match Transactions` clone
kept its real `FromPipelineTriggerEvent@1` + `FromHttpRequest@1` and ran anyway, entering at the first
transformation with `{}` as input — which for this pipeline means "consider everything", the same
thing its nightly cron means.

### 11.3 The real pipeline ran, and the evidence is entity data

`POST salzburgdev/v1/pipeline/execute?pipelineRtId=…bb11`:

```
leaseWaitMs = 1002   durationMs = 2640   status = Completed   errorMessage = null
leasedFromTenantId = accounting   leasedOnMemberId = octo-pool-0
```

From the member's log, counted rather than eyeballed: **808 Mongo commands against
`"$db":"salzburgdev"`, zero against `accounting`** — 323 on `BankTransaction`, 321 on
`AccountingDocument`, 150 on `NamedEntity`, 37 on `PaymentOrder`, plus reads of
`SystemIdentityDataPolicy` and `SystemIdentityDataPermission`. That last pair is the part worth
keeping: the run evaluated **data permissions under the borrower's identity**, so it was not quietly
executing as the system context. Teardown was complete — pipeline registrations, CK model cache,
repository clients and borrower identity all dropped on release.

🔴 **The dedicated path was broken at the same moment.** The same pipeline's cron run on
`…670000000000000000000002` failed at 20:45 UTC with *"no token could be acquired for service account
client `octo-pipeline-sa-670000000000000000000002`"*, while the leased run at 20:46 succeeded. Local,
probably the deployed pod's service-account configuration — but unexplained, and the asymmetry is the
opposite of what one would expect.

### 11.4 Round-robin, with the one enqueue order that can prove it

Enqueue **all three of one tenant first**, then all three of the other. Interleaving them proves
nothing, because FIFO and rotation produce the same sequence. Six executions in 320 ms, three of
`salzburgdev` (`…bb11`) then three of `meshmakers` (`…cc11`):

| # | | tenant | granted | waitMs | durMs |
|---|---|---|---|---|---|
| 1 | M1 | meshmakers | 20:51:52.465 | 4384 | 799 |
| 2 | S1 | salzburgdev | 20:51:57.601 | 9716 | 1360 |
| 3 | M2 | meshmakers | 20:52:02.716 | 14575 | 426 |
| 4 | S2 | salzburgdev | 20:52:07.828 | 19877 | 1284 |
| 5 | M3 | meshmakers | 20:52:12.944 | 24743 | 463 |
| 6 | S3 | salzburgdev | 20:52:18.068 | 30052 | 1320 |

Strictly alternating; FIFO would have given S,S,S,M,M,M. 🔴 **The rotation carries state across
leases**: the first grant went to `meshmakers` although `salzburgdev` had queued first — `salzburgdev`
had been served last, five minutes earlier. `GetAdapterPoolQueue` shows it honestly, with `ahead=1`
moving between the tenants between rounds.

### 11.5 Two different tenants in one process — the isolation claim, measured

Counting every Mongo command in each lease window from the member's log:

| Lease | leased tenant | own database | `octosystem` | `admin` | **another borrower's database** |
|---|---|---|---|---|---|
| M1 / M2 / M3 | meshmakers | 243 / 236 / 236 | 6 | 126 | **none** |
| S1 / S2 / S3 | salzburgdev | 808 / 801 / 801 | 6 | 691 | **none** |

No command against the other borrower's database in any window, and none outside the windows at all.
Both tenants' data was identical before and after (the pipeline found nothing to match), checked
against a `mongodump` taken before the run.

The `admin` traffic is `commitTransaction` / `abortTransaction` plus 28 `listDatabases`, and
`octosystem` is the tenant registry and CK model. **Neither goes through the lease credential** —
they use the process configuration, which is §9 item 4's second half: not a startup cost, a per-lease
one.

### 11.6 🔴 The member subscribes to the borrower's trigger queue, and leaves it behind

Registering the borrower's pipeline for the lease also registers its **bus triggers**. Observed:

```
Connect receive endpoint: …::bot::pipeline-trigger-salzburgdev-49240000000000000000bb11
Declare queue: … durable      Bind exchange: source: …:PipelineTriggerSchedule (fanout)
Consumer Ok → 1.9 s → Consumer Cancel Ok … 0 received
```

On release **only the consumer is cancelled**; the durable queue and its binding remain — confirmed
in RabbitMQ afterwards (queue present, 0 consumers). Two consequences, neither yet provoked:

1. A borrower's cron firing **during** a lease is delivered straight to the member, which runs it
   **outside the lease bookkeeping** — no controller-created execution entity, no queue position, no
   billing span.
2. Between leases the trigger events accumulate in that queue and arrive as a **burst** at the next
   lease. A neighbouring queue in the same estate was holding 14 such messages.

### 11.7 🔴 The scheduler tick is the throughput ceiling

Intervals between the six grants: **5.136 / 5.115 / 5.112 / 5.116 / 5.124 s** — exactly
`LeaseSchedulerIntervalSeconds` (default 5, `LeaseSchedulerBackgroundService`). The work itself took
0.43–1.36 s, so the member was **idle 75–92 % of the time** and a single member drains at most
**~12 executions per minute regardless of how small the work is**. A release does not wake the
scheduler; the next grant waits for the next tick. The tick is documented as "the scheduling round"
(plan §9); this consequence is documented nowhere, and for `Interactive` work it is up to five
seconds of avoidable waiting per queue position.

### 11.8 What is left running after this walk

The member process, the two clone pipelines with their `Leased` adapters and service accounts,
`LeasingEnabled=true` on `meshmakers` (it was false), and the trigger queue from §11.6. None of it is
load-bearing; all of it is removable with `RemoveRt` plus `-le false`.

## What this cannot show locally

- **Pool deployment through the operator** (increment 5) is Helm against Kubernetes; locally that is
  kind, not `Start-Octo`. Running the member by hand is the more honest instrument anyway.
- **Queue in Refinery Studio**: `QueuedAt`, `LeaseWaitMs` and `ExecutionClass` exist in the generated
  types since the schema refresh (plan §11a.2a), but the execution-history dialog does not select them
  yet, so the queue is fully operable from `octo-cli` and MCP and only partially visible in the Studio.
- **End-to-end traces.** There are none, for anything, in this estate — `ObservabilityBuilder`
  registers no `ActivitySource` and no trace context is propagated. Correlate on lease id and
  execution id in the logs, which is what §8 above does.
