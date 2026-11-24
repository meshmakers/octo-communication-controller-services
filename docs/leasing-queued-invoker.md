---
description: "Leasing: the sealed queued invoker and the method-invocation marker on the leased path"
applies_to: "**/Methods/PipelineMethodInvoker.cs, **/Services/PipelineExecutionService.cs"
---

### The queued invoker is sealed (System.Communication 4.12.0)

What the controller reads back from a queued `PipelineExecution` and acts on has to be provably its own
value: the entity's attributes are ordinary, writable attributes, so a value that has been edited, copied
from another execution or written in plain text must not decide who a leased run executes as.

| Piece | Where |
|---|---|
| Generic envelope: `Seal(purpose, payload, tenantId, executionRtId)` / `TryOpen` / `Reseal` | `Services/ExecutionEnvelope.cs` (`IExecutionEnvelope`, singleton, over `ISecretAttributeProtector`) |
| The queued invoker (principal + trust level + token) | `Repository/QueuedCaller` (`Apply`, `Open`, `Reseal`, `StripToken`), stored in `PipelineExecution.SealedCaller` |
| Written at enqueue | `TriggerManagementService` (leased path) |
| Verified at lease grant | `LeaseService.ResolveCaller`, fed by `QueuedExecution.SealedCaller` / `LeaseRequest.SealedCaller` + `ExecutionRtId` |
| Re-sealed on requeue | `LeaseService.InterruptAndEnqueueAsync` (`InterruptedLeasedExecution.SealedCaller` + `ExecutionRtId`) |
| Token removed at grant | `CommunicationRepository.TryClaimQueuedExecutionAsync` (`QueuedCaller.StripToken`) |

**Format.** The payload is wrapped as `{v, p, t, e, d}` (format version, purpose, tenant id, execution RtId, data)
and encrypted with the engine key ring (`enc:v2:<kid>`, AES-256-GCM, key rotation as for every `enc:v2` value) **for
the binding** `octo.communication.execution-envelope/<purpose>/<tenant>/<execution>`: the
binding is part of the AES-GCM associated data, so another purpose, tenant or execution does not open (reason
`Unreadable`), nor does a flipped bit, another key, plain text or `enc:v1`. A value produced through a Secret attribute
(same key ring, whose envelope a tenant administrator can read in a dump) never opens here, and a sealed value never
opens as a Secret attribute value. The reason of a refusal is logged (`ExecutionEnvelopeRejection`), never the value.

**Former form (before the channel-bound binding) is refused at once.** A value sealed with the binding only in its content cannot be told
apart from a Secret attribute value that carries a valid wrapper, so it is refused like any value that does not open
(`Unreadable`) and never re-sealed. Sealed values live only on `PipelineExecution.SealedCaller` for the life of one
execution (a seal the controller can open is refused after `QueuedSealMaxAgeDays` while queued; a retry gets a new
seal), so only the executions that are queued, or leased and then interrupted, at the moment of the upgrade are
affected. What happens to them depends on the key id of the value:

- **Queued, and the controller holds that key** (the usual case): the value reads `Unreadable`, the verdict is
  `Forged`, and the execution is failed at its grant (refusal `QueuedProvenanceInvalid`, borrower event, WARN). It
  never started.
- **Queued, and the controller does not hold that key** (a key that was rotated out): the engine reports the unknown
  key before it checks the binding, so the value reads `UnknownKey`, the verdict is `Unverifiable`, and the execution
  is **not** failed. It stays `Queued` at the head of its tenant's queue on that adapter pool, and every later queued
  execution of that borrowing tenant on that pool waits behind it (the scheduler blocks the tenant for the round
  instead of skipping the item). Every grant attempt logs the WARN "cannot be verified by this controller ... it stays
  queued" (refusal `QueuedProvenanceUnverifiable`). `QueuedSealMaxAgeDays` does not end this, because the age is read
  from the opened seal. If the key comes back, the next grant fails it as above. Otherwise the operator of the
  **lender** tenant (the tenant that owns the adapter pool) removes it with `AdapterPool.CancelQueuedExecution-1`
  (`DELETE AdapterPool/{adapterPoolRtId}/queue/{executionId}`), which frees the borrower's queue; the borrowing tenant
  cannot cancel it itself and starts the work again in its own tenant.
- **Leased and interrupted** (either key case): `Reseal` does not open the value, so the retry is re-queued without a
  seal and failed at its own grant (`Missing` counts as `Forged`); the WARN "The provenance of interrupted execution
  '<id>' did not carry onto its retry '<retry id>'" names both. The retry has a new execution id, but the work behind
  it is the interrupted run.

**Running an affected execution again.** An execution queued at the upgrade (failed at its grant, or cancelled while
queued) never started, unless it is itself the retry of an interrupted run: a retry that the previous version
re-queued before the upgrade is also queued at the upgrade, with a new execution id, but its work may already have
been partly done. Such a retry is named in the INFO "Interrupted leased execution '<id>' and re-queued it as
'<retry id>'"; a retry the new version re-queues is named in the WARN "The provenance of interrupted execution '<id>'
did not carry onto its retry '<retry id>'". The logs of a replaced pod may be gone, so the entities count as well: an
execution of the same pipeline in status `Interrupted` with the same input data marks a queued execution as its
retry. A queued execution counts as a retry unless the logs rule it out; a missing `Interrupted` entity does not. An
execution queued at the upgrade that the logs rule out as a retry is safe to run again. For an interrupted run and its retry, check for effects first (look up what the run
should have written, for example inserts of a method pipeline), and run it again only when nothing was committed, or
when the pipeline is idempotent; otherwise reconcile by hand, so no insert is repeated.

**Rollout and rollback.** The previous version opens a value without the binding, so a pod of the previous version,
during the rolling update or after a rollback, treats a bound value as the new one treats a former-form value (see
above). With one controller replica the overlap of the rolling update is the few seconds of the pod switch (longer when the
Deployment lets both pods run at once); a rollback affects every execution still queued with the bound form.
Executions failed or cancelled this way are run again under the rule above; for a rollback, read "previous" and "new"
the other way round (the retries the new version re-queued before the rollback are the interrupted work).

**Rules.**

- **Fail closed.** A value that does not open is never leased (below; the first sealed envelope alone let the run proceed
  without a caller). A controller that merely cannot read it (no key / unknown key id) leaves it queued. No stored trust level is trusted on its own; the trust level comes only out of the verified
  envelope.
- **No key ring, nothing stored.** `SecretEncryption` must be configured (it is on every cluster that uses Secret
  attributes). Without it `Seal` returns null and the enqueue is refused: a leased adapter cannot run
  without the key ring.
  The former behaviour "principal travels, token does not" is gone: an unsealed principal is not a principal.
- **Token.** Lives only inside the envelope. When the lease is granted (the claim) the attribute is replaced by the
  same envelope without the token, sealed for the same execution, so a re-queue still carries the caller but not the
  token (a retry runs as the caller without delegation).
- **Requeue** is a new execution: the old value is opened for the OLD RtId and sealed AGAIN for the retry's RtId, the
  ciphertext is never copied. A value that does not verify for the old execution is not carried.
- **The seven plain `Caller*` attributes** stay in the CK model (removing an attribute is a major change) but are
  deprecated: neither written nor read. Executions queued before the upgrade have no valid `SealedCaller` and are
  failed at lease grant (rollout note below).
- **Reuse.** Another value that the controller must be able to trust on a queued execution is one more member of the
  sealed payload (the method-invocation marker of does exactly that, see below) or, if it must be readable
  and replaceable on its own, one more `purpose` plus one more String attribute, written with `Seal` at enqueue and
  opened with `TryOpen` / `Reseal` — not a second mechanism.

Tests: `Services/ExecutionEnvelopeTests` (round trip, binding, tampering, copied secrets, no key ring, reseal,
`StripToken`), `Services/LeaseServiceTests/LeaseCarriesTheWorkTests` (the lease carries only what verifies: plain
text, forged JSON, `enc:v1`, other execution / tenant / key / purpose, flipped bit, copied secret, no key ring),
`Services/LeaseServiceTests/LeaseOutcomeAndRequeueTests` (requeue seals again, never copies),
`Services/TriggerManagementServiceTests/LeasedAdapterEnqueueTests`, integration `Repository/AdapterPoolQueueTests`
(generic update of the plain attributes and of the sealed attribute against real MongoDB, token removal at claim).

### Method-invocation marker on the leased path

**Design decision: the marker is one more member of the existing `SealedCaller` payload, not a second attribute.**
A separate `MethodInvocation` attribute (first design draft) would have needed System.Communication
4.13.0, a second `Seal`/`Open`/`Reseal` pair and a second place that can drift. The envelope already provides what the
marker needs: AES-GCM with the instance key ring (`enc:v2`), binding to purpose, tenant and execution RtId in the
associated data (and in the content), no plain-text acceptance, re-seal on requeue, and the provenance gate of (pipeline,
adapter, input). System.Communication therefore stays **4.12.0**; the old-instance behaviour is also the safe one (an
older controller ignores the unknown payload member, so it drops the marker: fail closed).

- **Enqueue.** `PipelineMethodInvoker` -> `StartExecutePipelineAsync(..., methodInvocation)` -> `QueuedCaller.Apply(...,
  methodInvocation)` seals the marker with the caller and the provenance. REST execute and `LeaseTriggerConsumer` (cron)
  pass null, so their payload has no marker. No public API accepts a marker.
- **Grant.** `QueuedCaller.TryOpen` returns the marker in `Opened.MethodInvocation` only when the whole envelope opens
  (tenant, execution RtId, pipeline, adapter, input) AND the marker is consistent: it has a `MethodId` and its
  `PipelineRtId` equals the pipeline the controller queued (and therefore the pipeline the lease runs). An inconsistent
  marker is dropped with a WARN (tenant and execution only, never the marker) and the lease is granted without it; an
  envelope that does not open at all refuses the whole execution, so a copied, edited or plain-text marker
  never reaches a member. `LeaseService` projects it into `LeaseDto.MethodInvocation`, independent of the caller. A
  hand-driven lease carries no work and no marker. The mesh adapter's guard additionally compares it with the pipeline
  it actually executes.
- **Requeue.** `QueuedCaller.Reseal` opens the value for the OLD execution RtId, keeps the marker (same work item, same
  invocation id and method) and seals it again for the retry's RtId; the ciphertext is never copied. A value that does
  not open for the old execution is not carried (the retry has no provenance and is refused at its grant); an
  inconsistent marker is dropped while the caller is kept. `StripToken` (at claim) keeps the marker.
- **What the controller binds.** The controller binds the marker to the tenant, the execution RtId (envelope) and the
  pipeline RtId (`marker.PipelineRtId` must equal the queued pipeline). It does **not** check `MethodId` against the
  replacing method of the type: that comparison, and the check against the pipeline the adapter actually executes,
  belong to the adapter guard.
- **Retry and `InvocationId`.** A retry carries the marker unchanged, so it has the same `InvocationId` as the interrupted
  attempt. A retried method run may therefore run again the inserts the interrupted attempt already made: method
  pipelines should be idempotent. The adapter guard does not require `InvocationId` uniqueness and must not start to
  (it would refuse every retry).
- **Fail closed.** No key ring: nothing is queued. Marker dropped for any reason: the adapter refuses a
  generic create of a type whose method replaces it, so the method call fails (availability only).
- **Single use.** Like the caller, the marker is covered by the single-use claim of the whole envelope: a
  completed method run whose writable status is reset to `Queued` is not leased a second time (see "A sealed execution is
  leased at most once").
- **Rollout (same as the queued-provenance envelope).** The marker is read only by an instance that has this change. During a mixed
  rollout a lease granted by an old instance carries no marker and a method run on a leased adapter fails closed until
  the old instance is gone. Roll out like the queued-provenance envelope: leasing kill switch off (`SetCommunicationLifecycle -le false`),
  drain or cancel queued leased items, roll **all** controller instances, switch leasing back on. Adapters need the
  guard of to enforce anything; an adapter without it ignores `LeaseDto.MethodInvocation`.

Tests: `LeaseServiceTests/LeaseCarriesTheWorkTests` (marker in the `LeaseDto`, none without, other pipeline / no method
id dropped, copied and plain-text refused, hand-driven lease), `LeaseServiceTests/LeaseOutcomeAndRequeueTests` (retry
keeps the marker sealed for the new RtId, copied marker dropped), `TriggerManagementServiceTests/
LeasedAdapterEnqueueTests` (method run seals the marker, REST and cron carry none).
