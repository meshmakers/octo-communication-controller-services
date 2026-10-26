# Method-Invocation Marker: Replaced Generic Create in Pipeline Runs

A CK method with `replacesGenericCreate: true` replaces the generic create of its type (CK language 2,
`octo-documentation` -> `technologyGuide/constructionKits/ckLanguage2/methods.md#replaces-generic-create`). The asset
repository refuses the generic create over GraphQL; the nodes `ApplyChanges@1`, `ApplyChanges@2` and `ImportFromExcel`
of the mesh adapter refuse the equivalent **insert** in a pipeline. The one run that must be allowed to insert is the
pipeline that implements the method (`execution.handler: pipeline`, see "CK method pipeline handler" in `CLAUDE.md`).
This page describes how the controller tells the adapter that a run is that run, and why a caller cannot fake it.

## 1. The marker

`ExecutePipelineMethodInvocation` (octo-sdk, `Meshmakers.Octo.Communication.Contracts.MessageObjects`) is an optional
member of `ExecutePipelineRequest` and `LeaseDto`:

| Field | Content |
|-------|---------|
| `MethodId` | Qualified id of the invoked method |
| `InvocationId` | Id of this call (the same for a retry of the same work item) |
| `CkTypeId` | Type of the call target |
| `PipelineRtId` | Id of the pipeline the controller bound to the method |

The mesh adapter carries it on the run context (`IMeshEtlContext.MethodInvocation`) and `GenericCreateGuard` lets an
insert of a flagged type pass only if the marker is present, its `MethodId` equals the qualified id of the method that
replaces the create of the inserted type, and its `PipelineRtId` equals the pipeline that is running. The controller
binds the marker to the pipeline; the comparison with the replacing method and with the executing pipeline is the
adapter's (two independent checks, see the adapter developer guide).

The marker does not travel over `ToPipelineDataEvent`: a chained pipeline is a different run without a marker and is
refused. This is intended: the insert has to happen in the method's own execution.

## 2. Who sets it

Only `PipelineMethodInvoker` produces a marker. It builds it from the accepted method request and the bound pipeline
and passes it as the optional parameter `methodInvocation` (default `null`) to
`TriggerManagementService.StartExecutePipelineAsync`, the one place that builds an `ExecutePipelineRequest`.

Every other way to start a run leaves it `null`:

- REST `POST .../pipeline/execute` binds only the query `pipelineRtId`, `isDryRun` and the raw body as pipeline input
  (a body member `methodInvocation` is plain input). `octo-cli`, the MCP tool and Refinery Studio use this route;
- cron triggers (`LeaseTriggerConsumer`) pass nothing;
- no SignalR hub method and no GraphQL field accepts an `ExecutePipelineRequest`, a lease or a marker.

The pipeline identity alone (well-known name = method id) is not a proof: the same pipeline can be executed by hand
through the REST route, and a method id inside the pipeline input is forgeable. That is why the marker is an explicit
member that no public interface can set.

## 3. Direct path and leased path

- **Direct path** (dedicated adapter): the controller publishes the request with the marker to the adapter's
  `FromExecutePipelineCommand` endpoint.
- **Leased path** (shared adapter pool): the run is queued as a `PipelineExecution` entity and later handed to a pool
  member as a lease. Entity attributes are ordinary, writable attributes (GraphQL, MCP), so the marker cannot be stored
  in a plain attribute. It is one more member of the **sealed envelope** (`PipelineExecution.SealedCaller`), the same
  envelope that carries the queued invoker and the provenance of the work item (`ExecutionEnvelope`: AES-256-GCM with
  the engine key ring, `enc:v2`; tenant, execution RtId, pipeline, adapter and input are part of the authenticated
  plaintext). No new CK attribute exists; `System.Communication` 4.12.0 (the version that introduced the envelope)
  covers it.
  - *Enqueue*: `QueuedCaller.Apply` seals the marker for a method run. Cron and REST runs have none.
  - *Grant*: the marker is read only if the whole envelope opens, and only if it is consistent (it has a `MethodId`
    and its `PipelineRtId` equals the queued pipeline). The lease carries it in `LeaseDto.MethodInvocation`.
  - *Requeue*: a retry re-seals the marker for the new execution; the ciphertext is never copied.

Details and tests: `CLAUDE.md`, sections "The queued invoker is sealed", "Method-invocation marker on the leased path",
"Only executions the controller queued are leased" and "A sealed execution is leased at most once"; the operational
view is in [Shared Adapter Leasing](shared-adapter-leasing.md) (borrower caller identity).

## 4. Lease sealing: what is protected against

| Attack | Defence |
|--------|---------|
| Write a marker (or a caller) into a queued execution through a generic write | The value is encrypted and bound to tenant and execution; plain text, edits and bit flips do not open. The execution is failed, not leased |
| Copy a valid sealed value from another execution | The execution RtId is part of the authenticated plaintext; it does not open for another execution |
| Create a queued execution by hand | The provenance in the envelope (pipeline, adapter, input hash) is missing; a hand-made item is never leased |
| Edit the pipeline, adapter or input of a sealed queued execution | These are bound inside the envelope; the item fails the grant |
| Reset a finished execution to `Queued` to run it again with its old marker | Single-use claim (`ClaimId`) recorded in the controller-owned collection `execution_lease_claims` in the system database; a second grant of the same seal is refused and the execution failed |
| Launder the claim through a hand-driven lease that expires | Only a lease that took a claim may requeue; a hand-driven lease takes none |

## 5. Fail-closed cases

Whenever the marker is lost, the result is availability, never a wrong allow. The adapter refuses the insert, the
method call fails:

- no `SecretEncryption` key ring on the controller: nothing is queued for a leased adapter (the enqueue is refused);
- a sealed value that does not open, is not bound to this execution, or is older than `QueuedSealMaxAgeDays` (default
  30): the execution is failed instead of leased (a controller without the key id only leaves it queued);
- an inconsistent marker (no method id, another pipeline): dropped, the lease is granted without it, the insert is
  refused;
- claim store unreachable: no lease, the execution stays queued;
- items queued before the upgrade carry no valid seal and no claim identity: failed, not migrated;
- old controller with a new adapter: no marker arrives, the method's own run is refused;
- a crash of the controller between recording the claim and starting the execution fails that job (the price of a
  record that nothing can reset).

## 6. Rollout order and mixed versions

1. Core libraries: `octo-sdk`, `octo-communication-sdk` (core-libs train).
2. Controller with `System.Communication` 4.12.0: leasing kill switch off (`SetCommunicationLifecycle -le false`), let
   queued leased executions drain or cancel them, let in-flight leases of the old pods finish (the stuck-execution reaper
   fails them after about 30 minutes), roll **all** instances, leasing back on. The controller needs the key ring and its
   admin database credential (`OctoSystemConfiguration.AdminUser` / `AdminUserPassword`). The sequence and its reasons
   are in [Shared Adapter Leasing](shared-adapter-leasing.md) (the notes under "Borrower caller identity") and in the `CLAUDE.md` rollout notes; they are not repeated here.
3. Mesh adapter right after.

There is **no compatibility switch**: a switch could only fail open. A new controller with an old adapter leaves the
insert unprotected until the adapter is upgraded (the old adapter ignores the unknown member and has no refusal). The
wire change is additive and nullable. Existing tenants are not affected unless a model uses `replacesGenericCreate`.

## 7. Residual risks

- A process with RabbitMQ publish rights can forge the marker on the bus, as it can forge `Caller`. The bus is a
  trusted boundary.
- A holder of the encryption key ring (the services, the operators of the instance) can seal arbitrary envelopes. The
  key ring is the root of trust for sealed values.
- A retried method run carries the same `InvocationId`; inserts the interrupted attempt already made can run again.
  Method pipelines should be idempotent. The adapter guard does not require unique invocation ids.
- The check covers the three adapter nodes named above. A node that writes entities by another route needs its own
  guard.
- A deleted claim record re-opens that seal until `QueuedSealMaxAgeDays` has passed: do not delete records of
  `execution_lease_claims`.
