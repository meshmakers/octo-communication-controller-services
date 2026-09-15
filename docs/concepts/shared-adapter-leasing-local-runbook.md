# Shared adapter leasing — trying it locally

A hand-run of AB#4924 against `Start-Octo`, end to end: one lending tenant with a pool, one
borrowing tenant with a `Leased` adapter, one member process, one pipeline execution that gets
queued, leased, run and released.

🔴 **Status of this document.** Steps 1–3 and 6–8 are derived from code that is tested and were
checked against the command definitions. **Steps 0–3 have now been walked** (AB#4924 §11a.2 part B,
which needed a live 4.0.0 tenant for the Studio schema refresh) and are corrected below; steps 4–9
are still hypothesis. Step 4 in particular names two possible routes because the AdapterPool entity
has no dedicated CLI verb.

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

## 2. Two tenants, and one must be the parent of the other

Lending never flows upwards and never sideways between roots (concept §3): a borrower must be a
**descendant** of the lender. Create the lender first, then the borrower as its child.

```bash
octo-cli -c Create -tid lender
octo-cli -c Create -tid borrower       # as a child of lender
```

Verify the parent link before going further — if the borrower is not in the lender's subtree the
scheduler will correctly refuse every lease, and the refusal is easy to misread as a bug.

## 3. The CK model

`System.Communication` is at **4.0.0** locally (999.0.0 packages). Both tenants need it.
The migration `3.35.0-to-4.0.0` renames `Pool` → `DeploymentSite` and the `Manages`/`ManagedBy`
roles → `Hosts`/`HostedBy`; the role half needs the `RenameAssociationRole` transform, which exists
only in the locally built engine. That is why the whole checkout has to be built from source for
this exercise.

✅ **Observed.** The long-lived local `meshtest` tenant on the `dev` stack already carries
`System.Communication-4.0.0`, state `AVAILABLE`, reached by the service-managed path while the
0.2-dev services were running — the migration was not performed by hand and was not observed step by
step, so the `RenameAssociationRole` half of it is *not* yet independently verified here. Check the
result from the tenant's GraphQL endpoint rather than from a log:

```graphql
{ runtime { systemCommunicationDeploymentSite { totalCount } } }
```

An answer means 4.0.0 is live. If `systemCommunicationPool` answers instead, the model did not
install — and if the *new* name is absent the endpoint returns **HTTP 400**, not an empty result, so
a naive "did it error?" check reads backwards.

🔴 **`System.Ai` cannot install on a 4.0.0 tenant** — `System.Ai-3.7.0` still pins
`System.Communication-[3.0,4.0)` (plan §3.4, not yet bumped on `test/0.2-dev`). The AI service logs
*"Dependencies 'System.Communication-[3.36.0]' are unknown construction kit model libraries"* and
gives up. The tenant feature still reports *AI Services: Enabled* while the model is absent, so
`GetTenantFeatures` is not evidence — list the installed models. This matters far beyond the AI
adapter: a frontend `schema.graphql` introspected from such a tenant silently loses every
`SystemAi*` type.

## 4. An `AdapterPool` in the lender, a `Leased` adapter in the borrower

There is no `CreateAdapterPool` CLI verb — increment 5 creates pools through the operator, which
locally means Kubernetes. For a hand-run, either:

- ~~**Refinery Studio**, against the local asset repository~~ — 🔴 **not available.** The Studio has
  no `AdapterPool` screen (plan §11a.2, part B), only the queue and members panels, which read an
  existing pool. This route was a hypothesis and is now ruled out.
- **`octo-cli -c ImportRt`** with a small runtime-model YAML declaring the `AdapterPool` (with
  `MinReplicas`/`MaxReplicas` and the `PoolMember*` sizing) in `lender`, and an adapter with
  `LifecycleMode = Leased` in `borrower`.

Note the pool's `rtId` — every later step needs it.

## 5. Turn leasing on — **on both tenants**

```bash
octo-cli -c SetCommunicationLifecycle -le true          # as lender
octo-cli -c SetCommunicationLifecycle -le true          # as borrower
```

Both halves are required: lending is the lender's capability, borrowing is the borrower's, and
neither can assert the other's. Default is off. The value is cached for 30 s, so give it a moment
before concluding it did not take.

Turning it off **holds** the queue — queued work stays queued and visible, nothing new is enqueued
and no lease is granted. It does not drain and does not discard.

## 6. Start a member process

A mesh adapter becomes a pool member purely through configuration:

```bash
export OCTO_ADAPTERPOOL__POOLTENANTID=lender
export OCTO_ADAPTERPOOL__POOLRTID=<the pool rtId from step 4>
export OCTO_ADAPTERPOOL__MEMBERID=octo-pool-0          # optional; defaults to the machine name
dotnet run --project src/MeshAdapter -c DebugL
```

Expect in the log: *"Starting as member 'octo-pool-0' of adapter pool … in lending tenant 'lender'"*
followed by *"Registered as member …"*. There is deliberately **no** `DedicatedTenantId` here —
between leases a member serves nobody, and a process-wide tenant is the exact hazard increment 3
removed.

A second member is just a second process with a different `MEMBERID`.

## 7. Give the borrower some work

Deploy a pipeline to the borrower's `Leased` adapter and execute it. Because the adapter is
`Leased`, `StartExecutePipelineAsync` creates the execution as **`Queued`** with `QueuedAt` instead
of sending it to the execute queue.

## 8. Watch the queue

```bash
octo-cli -c GetAdapterPoolQueue -id <poolRtId>          # as lender
octo-cli -c GetAdapterPoolQueue -id <poolRtId> -j       # JSON
```

What to look for, in order:

1. the entry appears with `position=1 in its tenant, 0 tenant(s) ahead in the rotation` —
   **there is no global rank**, and there cannot be one under round-robin;
2. within one scheduler round (5 s default) it is granted: the member logs the lease, the entry
   moves to the leased section and names the member that holds it;
3. the pipeline runs in the member process, as the **borrower** — the token it presents carries
   `tenant_id=borrower`, not the lender;
4. on release the execution is terminal and both `LeaseGrantedAt` and `LeaseReleasedAt` are stamped.
   The gap between them, minus the pipeline's own run time, is the pool overhead — the thing the two
   separate spans exist to make visible.

Cancelling a **waiting** entry:

```bash
octo-cli -c CancelQueuedExecution -id <poolRtId> -eid <executionId>
```

An entry that already holds a lease answers **409** — interrupting a running pipeline is a different
operation and deliberately not the same verb.

## 9. The interesting failure modes to provoke

- **Stop the member mid-lease.** The execution should be interrupted and **re-queued as a new
  execution entity**; the interrupted attempt keeps its own lease span for billing.
- **Two borrowers, one member.** Round-robin should alternate, not serve the louder tenant first.
  Queue 200 items for one and 1 for the other; the single item must not wait for the 200.
- **`-le false` mid-queue.** Everything already queued stays; nothing new is granted.
- **Let a lease expire** (`LeaseTtlMinutes`, default 15). The member is **drained and restarted**,
  not re-used — its post-lease cleanliness is unproven (concept §6).

## What this cannot show locally

- **Pool deployment through the operator** (increment 5) is Helm against Kubernetes; locally that is
  kind, not `Start-Octo`. Running the member by hand is the more honest instrument anyway — you can
  watch one process take a lease, run someone else's pipeline, and give it back.
- **Queue in Refinery Studio**: `QueuedAt`, `LeaseWaitMs` and `ExecutionClass` are CK 4.0.0 GraphQL
  fields. ✅ The Studio's `schema.graphql` has been refreshed against a 4.0.0 tenant and codegen
  re-run (plan §11a.2a), so the fields exist in the generated types — but the execution-history
  dialog does not select them yet, so the queue is still fully operable from `octo-cli` and MCP and
  only partially visible in the Studio. Same for `AdapterPool`: a GraphQL type now, still no screen,
  so step 4's "Refinery Studio" route **does not exist** — `octo-cli -c ImportRt` is the only one.
- **End-to-end traces.** There are none, for anything, in this estate — `ObservabilityBuilder`
  registers no `ActivitySource` and no trace context is propagated. Correlate on lease id and
  execution id in the logs.
