# Shared adapter leasing — trying it locally

A hand-run of AB#4924 against `Start-Octo`, end to end: one lending tenant with a pool, one
borrowing tenant with a `Leased` adapter, one member process, one pipeline execution that gets
queued, leased, run and released.

🔴 **Status of this document.** Steps 1–3 and 6–8 are derived from code that is tested and were
checked against the command definitions. **Nothing here has been run end to end yet** — this is the
runbook for the first attempt, not a report of one. Step 4 in particular names two possible routes
because the AdapterPool entity has no dedicated CLI verb. Correct this file as you go; a runbook
that was never walked is a hypothesis.

## 0. What has to be true first

- The whole dev checkout builds: `invoke-buildall -branch dev -configuration DebugL -excludeFrontend $true`.
  26 repos, 0 failures. Leasing spans the SDK, the adapter, the controller and the CK model, so a
  partial build will fail somewhere unhelpful.
- Docker is up (MongoDB, RabbitMQ).

## 1. Bring the services up

`Start-Octo` — identity, asset repository, communication controller and friends on ports 50xx.
Do **not** kill individual services under a running `Start-Octo`; restart the set.

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

## 4. An `AdapterPool` in the lender, a `Leased` adapter in the borrower

There is no `CreateAdapterPool` CLI verb — increment 5 creates pools through the operator, which
locally means Kubernetes. For a hand-run, either:

- **Refinery Studio**, against the local asset repository, or
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
  fields and the checked-in `schema.graphql` predates 4.0.0. Until the codegen runs with the model
  train, the queue is fully operable from `octo-cli` and MCP but only partially visible in the
  Studio.
- **End-to-end traces.** There are none, for anything, in this estate — `ObservabilityBuilder`
  registers no `ActivitySource` and no trace context is propagated. Correlate on lease id and
  execution id in the logs.
