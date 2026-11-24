---
description: "Pipeline endpoints: reassignment to another adapter, debug toggle and the execute-pipeline endpoint key"
applies_to: **/Controllers/PipelineController.cs, **/Controllers/PipelineDebugController.cs, **/Services/PipelineDebugService.cs
---

## Pipeline Reassignment (`PATCH /pipeline/move-to-adapter`)

When a new adapter (e.g. one provisioned from a fresh Blueprint) replaces
an older adapter, the existing `RtPipeline` entities can be re-pointed at
the new adapter without recreating them. `PipelineController.MovePipelinesToAdapter`
takes a bulk list of pipeline ids plus a target adapter id and walks each
one through `ICommunicationRepository.MovePipelineToAdapterAsync`.

Repository semantics (`MovePipelineToAdapterAsync`):
- Atomic per pipeline: in a single transaction, the outbound `Pipeline.Executes`
  edge to the source adapter is deleted and the equivalent edge to the
  target adapter is inserted via `AssociationUpdateInfo.CreateDelete` /
  `CreateInsert` + `ApplyChangesAsync`.
- **Source and target adapter must have the exact same `CkTypeId`.** A
  pipeline only knows how to run nodes its adapter implementation
  understands, so moving a pipeline onto an adapter of a different concrete
  subtype is rejected (`AdapterTypeMismatchForMove`). If you want a wider
  policy (allow any `Adapter`-derived subtype), loosen the equality check
  here — but mind that the operator's helm path will not magically
  reinterpret incompatible node configurations.
- Refuses to move a pipeline that currently has no `Executes` edge
  (`PipelineHasNoAdapter`) — moving an unassigned pipeline would silently
  assign it, which is too easy to misuse.
- No-op when the pipeline already points at the target — returns success
  with old==new so the caller does not need to special-case it.

Controller wraps the repository for bulk + best-effort redeploy:
- One repository call per pipeline; per-pipeline failures collected and
  reported as `MovePipelineResultDto { Success=false, ErrorMessage=... }`
  without aborting the rest of the batch.
- When `Redeploy=true` and the pipeline actually moved (i.e. not a
  no-op), the controller fires `IAdapterService.DeployPipelineAsync` on
  the new adapter immediately afterwards. A redeploy failure does **not**
  roll the move back — the pipeline already points at the new adapter
  and the operator can hit "Deploy" manually once the adapter is back
  online. The redeploy error surfaces in `ErrorMessage` as a warning
  while `Success` stays `true`.
- Writes one audit event per pipeline via `ICommunicationEventService`
  with the source tag `(source: User)`.

Tests:
- `Controllers/PipelineControllerMoveToAdapterTests` (TUnit) — empty
  list, invalid target id, single happy path, redeploy success,
  redeploy failure (warning path), no-op when already on target,
  repository exception, mixed bulk batch.

## Pipeline Debug Toggle (`PATCH`/`GET /pipeline/{id}/debug`)

`PipelineController` exposes explicit control of per-pipeline debug capture
(the `RtPipeline.IsDebuggingEnabled` flag), used by `octo-cli`
(`SetPipelineDebug` / `GetPipelineDebug`).

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `PATCH` | `{tenantId}/v1/pipeline/{pipelineRtId}/debug` body `{ "enabled": true|false }` | read-write | Persists `IsDebuggingEnabled` and, when the owning adapter is online, re-pushes config so it takes effect immediately. Returns `SetPipelineDebugResultDto { Enabled, AppliedToRunningAdapter }`. |
| `GET`   | `{tenantId}/v1/pipeline/{pipelineRtId}/debug` | read-only | Returns the persisted `PipelineDebugStateDto { Enabled }`. |

**Implementation:** `IAdapterService.SetPipelineDebuggingAsync` persists via
`ICommunicationRepository.SetPipelineDebuggingEnabledAsync`, then re-pushes the
owning data flow with `DeployDataFlowAsync`. (Historic note: before the persisted-flag change,
`DeployPipelineAsync` force-enabled debug, which is why this path avoided it;
since then all deploy paths preserve the persisted flag as-is.)
If the adapter is offline, `DeployDataFlowAsync` throws `AdapterServiceException`
(`AdapterNotLoaded`); the service swallows it, leaving the flag persisted
(`AppliedToRunningAdapter = false`, applies on next deploy). An `Information`
audit event tagged `(source: User)` is written per toggle.

**Deploy never touches debug.** `POST /pipeline/deploy` used to
force-enable `IsDebuggingEnabled` on every call — so the Studio editor's routine
"Deploy" (e.g. right after importing a pipeline) and the move-to-adapter redeploy
silently switched debug capture on, permanently. Observed on production: the Loxone
event pipeline ran with debug for months. Since the persisted-flag change, deploying pushes the
persisted `IsDebuggingEnabled` as-is: a pipeline in debug stays in debug across
redeploys, a pipeline without debug stays clean. Debug is toggled exclusively via
this PATCH endpoint / `SetPipelineDebuggingAsync`; the Studio's Enable/Disable
Debug buttons call it directly (frontend-libraries
`CommunicationService.setPipelineDebugging`).

**Debug-enabled pipelines are surfaced on every push.** A lingering
debug flag on a compute-heavy pipeline OOM-killed the FDA adapter without anyone
knowing debug capture was active (debug retains per-iteration snapshots — memory
bounds fixed SDK-side). `SendConfigurationAndWaitForResultAsync`
— the single choke point for every configuration push (adapter config, pipeline
deploy, data-flow deploy) — now writes a Warning audit event + NLog warning for
each pipeline in the pushed configuration whose `IsDebuggingEnabled` is true, so
sticky debug state is visible in the Studio event log instead of silent.

Tests: `Services/AdapterServiceTests/SetPipelineDebuggingAsyncTests` (online
enable/disable, offline persist-only, not-found paths),
`Services/AdapterServiceTests/DebugEnabledDeployWarningTests` (warning event on
debug-enabled push, silence otherwise) and
`Controllers/PipelineControllerDebugTests`.

## Execute-Pipeline Endpoint Key

`FromExecutePipelineCommand@1` registers a distribution-event-hub receive endpoint whose
address **must be keyed by the pipeline rtId**, not the DataFlow rtId. Both sides of the bus
build the address and must stay in sync:

- **Consumer** — `FromExecutePipelineCommandNode.StartAsync` (octo-communication-sdk,
  `Sdk.Pipeline`): `…-pipeline-{PipelineRtEntityId.RtId}`.
- **Sender** — `TriggerManagementService.StartExecutePipelineAsync`:
  `…-pipeline-{pipelineRtId}` (no DataFlow lookup needed).

The address format is
`{PipelineQueueNames.ExecutePipelineCommand}-{tenantId}-pipeline-{pipelineRtId}` (lower-cased).

Historically the key was `…-data-flow-{dataFlowRtId}` (the original key). That collided when a
single DataFlow held more than one `FromExecutePipelineCommand@1` pipeline — MassTransit
rejected the duplicate (`A receive endpoint with the same key was already added`), so only the
first pipeline deployed and the rest went to `DeploymentState = Error`. It was also
semantically wrong: `ExecutePipeline` targets one specific pipeline, so a DataFlow-scoped queue
with competing consumers would round-robin the message. Keying by pipeline rtId is the correct
scope. Regression guard: `MeshAdapter.Sdk.Tests` →
`FromExecutePipelineCommandNodeTests.StartAsync_KeysEndpointByPipelineRtId_NotDataFlowRtId`.

> Wire-contract note: controller + mesh-adapter + SDK must ship together (release train).
> Sibling `FromSendNotificationNode` still uses DataFlow-scoped keying and has the same latent
> collision if a DataFlow holds two `FromSendNotification@1` pipelines — tracked separately.
