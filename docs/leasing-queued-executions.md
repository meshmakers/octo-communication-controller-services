---
description: "Leasing: only controller-queued executions are leased, a sealed execution is leased at most once, the lease carries the work"
applies_to: **/Services/LeaseService.cs, **/Repository/Queued*.cs, **/Consumers/LeaseTriggerConsumer.cs
---

### Only executions the controller queued are leased

The lease queue used to trust any entity with `Status = Queued` and the pipeline/adapter associations, whoever wrote
it; the generic runtime write path (GraphQL create, MCP) can create one. Now the controller leases only what it
sealed itself.

**Design (no new attribute, no CK bump).** The `SealedCaller` envelope of is also the provenance of the work
item. `QueuedCaller.Apply(execution, tenantId, pipelineRtId, adapterRtId, caller, token, envelope)` is called for
**every** execution the controller queues (`TriggerManagementService`: REST execute, method invoker, cron via
`LeaseTriggerConsumer`; `LeaseService.InterruptAndEnqueueAsync`: retry) and seals it even when there is no caller
(`SubjectId` null). Payload additions: a provenance marker, the pipeline RtId, the adapter RtId and a SHA-256 of the
input, so an execution that is controller-queued but whose pipeline or adapter association or input was edited
afterwards does not open either. The binding to tenant and execution RtId is the one of `IExecutionEnvelope`.
System.Communication stays 4.12.0 (the method-invocation marker needs no bump either, it rides in the same envelope). (A queued item whose pipeline is
moved to another adapter while it waits fails the adapter binding; re-run it.)

**Gate.** `LeaseService.GrantLeaseAsync` runs `CheckProvenanceAsync` after the leasing kill switch and the borrower
lookup and before any credential is decrypted, for every request that names an `ExecutionId`, an execution RtId or a
pipeline: the scheduler's (the queue entry travels on the request) and the **hand-driven**
`POST {tenantId}/v1/adapterPool/{id}/lease`, whose `ExecutionId` must resolve (`TryGetQueuedExecutionAsync`) to a
`Queued` execution of the borrower whose provenance opens. A lease that names no work is not gated. Verdicts
(`QueuedCaller.TryOpen` returns `Provenance`):

| Verdict | Cause | Effect |
|---|---|---|
| `Valid` | opens for tenant, execution, pipeline, adapter and input | the grant continues |
| `Forged` | no seal, plain text, edited, copied, other purpose/tenant/execution, no provenance marker, changed pipeline/adapter/input | the execution is failed **by RtId**, only while `Queued` (`TryFailQueuedExecutionAsync`: reason, borrower event, WARN naming tenant/execution/pipeline, never a value); refusal `QueuedProvenanceInvalid` |
| `Unverifiable` | this controller has no key ring (`NoKey`) or not the key id (`UnknownKey`, e.g. key rotation) | **not** failed: WARN, refusal `QueuedProvenanceUnverifiable`, the item stays `Queued` for a pod that holds the key |

The failure is keyed by RtId because the business `ExecutionId` is a writable attribute and need not be unique; the
claim (`LeaseClaim.ExecutionRtId`) latches the verified entity by RtId for the same reason. The scheduler drops a
forged head item (`QueuedProvenanceInvalid` removes it from the round's list) **without blocking the tenant**, so the
tenant's next item is served in the same round.

**Enqueue.** Without a key ring the controller cannot seal, so `TriggerManagementService` refuses to queue
(`TriggerManagementServiceException.CannotSealQueuedExecution`, nothing written), like the kill switch and a missing
pool.

**Retry** re-seals for the new execution (`QueuedCaller.Reseal` keeps provenance, pipeline, adapter and input; the
token is cleared). When the old value does not carry, the retry site logs a WARN and the retry is refused at its own
grant.

**Rollout (decision: fail closed, no code grace window).** Executions that are `Queued` when the new controller runs
have no provenance marker and are failed at the next scheduling round. Because an older controller instance still
leases unsealed items, roll out like this: (1) switch the leasing kill switch off (`SetCommunicationLifecycle -le
false`; it holds the queue), (2) let the queued leased items drain or cancel them, (3) roll **all** controller
instances, (4) switch leasing back on. Retries created by an old pod during the window are covered by step 2.

**Known residual risk (closed by the single-lease rule).** Provenance proves who queued an execution, not that the entity is still in
the state it was queued in: a writable status/timestamp could reset an execution that already ran so that it was leased
once more. The controller-owned single-use claim below closes this.

Tests: unit `LeaseServiceTests/LeaseCarriesTheWorkTests` (refusal matrix, key-less and unknown-key controllers stay
queued, hand-driven endpoint), `ExecutionEnvelopeTests`, `LeaseSchedulerServiceTests/QueuedWorkReachesTheLeaseTests`
(forged head item does not starve the tenant), `AdapterLeasingMetricsTests`, `TriggerManagementServiceTests/
LeasedAdapterEnqueueTests`; integration against MongoDB (Testcontainers) `Repository/AdapterPoolQueueTests`: generic
`Queued` execution failed, controller-queued ones pass the gate, edited input / copied seal / re-pointed pipeline or
adapter failed, two entities with the same `ExecutionId` (forged failed by RtId, real untouched and claimed), the
hand-driven lease gated, retry keeps provenance. The integration fixture resolves the real `LeaseService` (tenant tree
and credential resolvers substituted); a full member grant needs a connected pool member, which the fixture does not
run, so the positive case asserts the specific downstream refusal reason.

### A sealed execution is leased at most once

The status and the lease timestamps of a `PipelineExecution` are ordinary attributes: a writer on a generic surface
(GraphQL update, MCP) can put a finished execution back to `Queued` and clear the lease latch
(`leaseGrantedAt`). The sealed envelope still opens (it is bound to tenant, execution, pipeline, adapter and input, none
of which changed), so the execution would be leased AGAIN, as its original caller and trust level, and a method run
with its original marker. The seal therefore needs a single use that no generic write can reset.

**Design.**

| Piece | Where |
|---|---|
| Single-use identity `ClaimId` (random GUID) and `IssuedAtUtc`, members of the sealed payload (authenticated, so they cannot be edited or copied) | `QueuedCaller.Payload`, written by `Apply` and `Reseal` |
| Claim record: atomic insert-if-absent, key `"{tenantId}/{claimId}"`, also names the `leaseId` it was taken for | `IExecutionLeaseClaimStore` / `ExecutionLeaseClaimStore`, collection `execution_lease_claims` in the **system database** |
| Claim taken | `LeaseService.GrantLeaseAsync`, after the seal verified and a member was reserved, before the admission gate (the `Queued -> Running` latch) and before anything is sent |
| Seal age limit | `CommunicationControllerOptions.QueuedSealMaxAgeDays` (default 30) |

- **Where the identity lives.** Inside the sealed payload, next to the caller and the method-invocation marker, so one
  claim covers the whole envelope (caller AND marker). `StripToken` (at claim) rewrites the envelope but keeps `ClaimId`
  and `IssuedAtUtc`; a reset execution therefore presents the same identity.
- **Where the claim lives.** A controller collection in the system database, written with the controller's own admin
  client (`AdminMongoRepositoryClient`, same connection rules as the engine). It is not a CK entity and not in a tenant
  database, so the runtime API, asset-repo GraphQL and the MCP generic tools never see it, and neither does the
  per-tenant database user that a pool member receives with a lease. No CK change (System.Communication stays 4.12.0).
- **Replica safety.** `InsertOne` on the primary key. A duplicate key is the answer "already claimed" for every
  controller instance; there is no read-before-write window. The claim comes after `QueuedCaller.TryOpen` verified the
  seal (a forged value never reaches the store) and after the member reservation (a refusal such as "no idle member"
  must not burn the claim).
- **Outcomes of a duplicate.** The refusal carries a reason code only, never a statement about the claim:
  - claim younger than `LeaseService.ClaimInFlightWindow` (2 minutes): another instance is granting this very
    execution (every instance's scheduler reads the same queue): `AdmissionGateDeclined`, the execution is left alone.
    The scheduler **skips** such an item for the round (`LeaseSchedulerService.TryGrantAsync`), it does not block the
    tenant's next item;
  - older: a claim for this seal was recorded a while ago and the execution is queued again: WARN (tenant, execution,
    pipeline; never the identity or any sealed value), the execution is failed by RtId while `Queued`
    (`TryFailQueuedExecutionAsync`, text "not leased: claim already recorded") so the queue does not retry it every
    round, refusal `QueuedProvenanceInvalid`.
  - **Server time.** The age is measured entirely on the MongoDB server clock: the claim is one upsert whose fields are
    all `$ifNull(existing, new)` with `$$NOW` for `claimedAtUtc`, and every probe stamps `probedAtUtc = $$NOW`; the age is
    `probedAtUtc - claimedAtUtc`. Controller replicas' clocks play no role. (`UpsertedId` tells the inserter.)
  - **A crash between the claim and the gate fails the job.** If the instance that recorded the claim dies (or the gate
    throws) before it started the execution, the item cannot be told apart from a replay once the in-flight window has
    passed: it is failed with the same text. This is the deliberate fail-closed price of a record that nothing can reset;
    a lost item is re-run by its caller, a replay is not possible.
- **Claim given back** only on a CLEAN `admitted == false` of the admission gate (lost the latch to another instance,
  cancelled): the execution did not start, the claim is deleted (best effort), the seal is still leasable. When the gate
  THROWS the claim stays (its write may have been applied and the execution may already run); the member is released
  either way. After the gate admitted, the claim stays whatever happens to the dispatch.
- **Lease-bound requeue.** `InterruptAndEnqueueAsync` (TTL expiry, lost member, refused lease) interrupts and re-seals only
  for a lease that took a claim: `IExecutionLeaseClaimStore.HasLeaseClaimAsync(tenant, leaseId)` must find a claim record
  that names this lease id (generated by the controller at grant, so it is the controller's own proof, not something the
  member or the entity says). A **hand-driven** lease (`POST .../lease` with an `ExecutionId`) takes no claim, so it never
  requeues anything: otherwise "reset a used execution, lease it by hand, let the lease expire" would launder the claim
  through a re-seal under a fresh identity. `TryInterruptLeasedExecutionAsync` additionally never interrupts a `Queued`
  execution (no lease is behind it). A store that cannot be reached means "not bound" (nothing is re-sealed).
- **Requeue bound to the execution, once (N1).** "Some claim of this lease exists" is not enough: a lease rebuilt from
  member-supplied ids (`ResolveUnheldReleaseAsync`, resume) could name another execution. After the interrupt, the seal
  about to be re-sealed is opened and its `ClaimId` must satisfy
  `IExecutionLeaseClaimStore.TryMarkRequeuedAsync(tenant, claimId, leaseId, executionRtId)`: an atomic
  `findOneAndUpdate` on `{tenant}/{claimId}` with the filter `leaseId == lease.LeaseId`, `executionRtId == that
  execution` and `requeuedAtUtc` absent, which sets `requeuedAtUtc = $$NOW`. Only the caller that sets it may `Reseal` and
  enqueue; the reaper and a refused/lost release racing for one lease therefore produce exactly one retry. The order fails
  closed: the attempt is already `Interrupted` (terminal) when the check runs, and a failed check creates no retry. A
  value that does not open at all yields a retry without provenance, which is refused at its own grant.
- **Store unreachable.** Fail closed: no lease, refusal `AdmissionGateFailed`, WARN, the execution stays queued.
- **Re-queue.** `QueuedCaller.Reseal` seals the retry with a NEW `ClaimId` and a new `IssuedAtUtc`; the old identity is
  never copied. The retry is leasable once, and exists only for a lease-bound requeue (below).
- **Hand-driven lease** (`POST .../lease` with an `ExecutionId`) carries no work and consumes no claim; for that reason it
  can never requeue (see "Lease-bound requeue").

**Retention.** Every record has `expireAtUtc = claimedAt + QueuedSealMaxAgeDays + 1 day` and a TTL index
(`execution_lease_claims_expire_ttl`, `expireAfterSeconds: 0`, created lazily on first use together with the lookup index
`execution_lease_claims_lease` on `(tenantId, leaseId)`; a failed index creation is retried after one minute, not on every
call) deletes it; the TTL monitor
runs about once a minute. The invariant that makes deleting safe: a seal older than `QueuedSealMaxAgeDays` is refused at
grant (and its execution failed), so an expired record can never make an old seal usable again. A queued item that waits
longer than the limit is therefore failed instead of leased; a retry is sealed anew and starts a new age. Records of a
deleted tenant simply expire (the identity is unique per seal). Do not shorten the TTL without shortening the age limit.

**Pre-upgrade items (decision: fail closed, no migration).** A sealed value without `ClaimId`/`IssuedAtUtc` (queued by a
controller before this change) is refused like a forged one ("queued before the upgrade") and its execution failed.
Adopting such a value would mean accepting a seal that can never be claimed, which is exactly the hole this change
closes. The retry of an interrupted pre-upgrade attempt is sealed anew by `Reseal` and is fine.

**Rollout.** The train already requires the leasing sequence of the queued-provenance envelope: (1) leasing kill switch off, (2) let queued
leased items drain or cancel them (any still queued are failed at the first round of the new controller) **and let the
in-flight leases granted by old controller pods finish or cancel them: they have no claim record, so after the roll they
are not requeued when they expire or are lost, and the stuck-execution reaper fails them after about 30 minutes**, (3) roll all
controller instances (an old instance ignores `ClaimId` and would lease a replayed seal), (4) leasing back on. No data
migration, no new setting needed (`QueuedSealMaxAgeDays` defaults to 30). The controller needs its admin database
credential (`OctoSystemConfiguration.AdminUser` / `AdminUserPassword`), as the engine's system context already does; it
creates the collection and the TTL index on first use. Operators may inspect the collection read-only; deleting a record
re-opens the seal of that execution, so do not.

Tests: unit `LeaseServiceTests/LeaseSingleUseClaimTests` (reset after a run, reset method run, token strip keeps the
identity, requeue gets a new identity, parallel grants, gate declined/threw gives the claim back, unreachable store,
pre-upgrade seal, expired seal, hand-driven lease; a gate that throws keeps the claim; hand-driven lease + reap and
hand-driven lease + refusal requeue nothing, while the lease that took the claim requeues under a new identity; log has a
WARN and no identity or sealed value); integration (MongoDB,
Testcontainers) `Repository/AdapterPoolQueueTests.AResetCompletedExecution_IsNotLeasedASecondTime` (normal run and method
run through the real repository, claim store and latch: complete, reset via the generic write path including the latch
timestamp, no second lease, failed once the claim is past the in-flight window),
`Repository/AdapterPoolQueueTests.AHandDrivenLeaseOnAResetExecution_CannotLaunderTheClaimThroughARequeue` and
`Repository/ExecutionLeaseClaimStoreTests` (24 concurrent callers and which lease won, per tenant, release, record shape
on the server clock, TTL and lease indexes, collection in the system database and not in the tenant database).

### ✅ The lease carries the work (§9.9 / D4)

This section used to say a lease carried `ExecutionId` and nothing else, so a member that received
one had nothing to run. Resolved: `LeaseDto` carries the pipeline rtId, the input and the projected
`PipelineConfigurationDto`, and `octo-mesh-adapter` implements the work item. `LeaseService` refuses
the lease — **before a member is reserved** — when the pipeline cannot be projected.
