---
description: "CK method pipeline handler: how a System.Communication method is served by a pipeline, invocation, result mapping"
applies_to: **/Methods/**
---

### CK method pipeline handler (AB#6879)

The controller is the handler of every CK method declared `execution.handler: pipeline` (CK language 2). A call
arrives as a `CkMethodRequest` on the method queue (wire contract: `octo-common-services/docs/method-wire-contract.md`),
the controller starts the pipeline that implements the method and answers with a `CkMethodResponse`. Code:
`src/CommunicationControllerServices/Methods/`. Guide for model authors and pipeline authors:
`octo-documentation` -> `octo-documentation/src/octo-mesh-documentation/docs/technologyGuide/constructionKits/ckLanguage2/pipelineMethodHandlers.md`.

**Why the controller.** It is where `DeployPipeline` and the execute command live: `StartExecutePipelineAsync`
(`TriggerManagementService`) sends the `ExecutePipelineRequest` (with the invoker as `ExecutePipelineCaller` and the raw
token) to the `FromExecutePipelineCommand` endpoint of the pipeline, and the adapter reports the execution (status,
`OutputData` from `SetPipelineExecutionResult@1`) into the `PipelineExecution` store of the tenant. The mesh adapter
runs pipelines but knows no methods, tenants' CK models or bus handler registry; the asset repository is the gateway
(caller side). No new CK element, no change in the adapter or the SDK.

| Class | Role |
|---|---|
| `PipelineMethodCatalog` | Reads the CK caches of the communication-enabled tenants (`IAdapterCache.GetEnabledTenantIds`): methods with `handler: pipeline`, and any method by qualified id. |
| `PipelineMethodInvoker` | One call: contract version, deadline, **caller token validation**, tenant/method checks, refusal of sensitive parameters and of a sensitive result, parameter presence, pipeline lookup (`GetPipelineByWellKnownNameAsync`, must be `Deployed`), start, poll the execution until the deadline, map. Never throws for a failing request. |
| `PipelineMethodResultMapper` | Pure mapping execution status + `OutputData` to the envelope (see below). |
| `PipelineMethodBusConsumer` | Bus shell: duplicate guard (possible duplicate delivery or repeated invocation id: `METHOD_OUTCOME_UNKNOWN`, not started again), never throws to the bus. The guard is the shared `CkMethodSeenInvocations.Decide` of `Meshmakers.Octo.Services.Methods` (OD-3 option A, AB#7818/AB#7822); the controller keeps no seen-invocation memory of its own. The helper clamps the deadline before adding the retention, so a foreign sender's `DeadlineUtc = DateTime.MaxValue` cannot overflow (regression test in `PipelineMethodBusConsumerTests`). |
| `PipelineMethodHost` | Reconcile: for each pipeline method a shared command endpoint (`CkMethodQueueNames.For`, faults suppressed), `WaitUntilReadyAsync`, then `CkMethodHandlerAnnounced`; withdraws a method whose pipeline is gone, `CkMethodHandlerWithdrawn` on shutdown. |
| `PipelineMethodHostInitializer` (order 40) / `PipelineMethodHeartbeatService` / `PipelineMethodDiscoveryConsumer` | First reconcile after the tenants started; re-check and re-announce every `HeartbeatInterval` (15 s, TTL 45 s); announce on `CkMethodHandlerDiscoveryRequested`. |

**Why not `AddCkMethodHandlers` of the SDK.** The SDK registers handler types statically at service configuration
time and validates them against the system tenant's CK cache. The methods of the controller are declared by tenant CK
models and become servable when a pipeline is deployed, i.e. at run time. The host therefore uses the same contract,
queue names, shared endpoints and announcement events, but reconciles dynamically (every heartbeat).

**Binding.** The pipeline implementing a method is the pipeline whose `RtWellKnownName` equals the qualified method id,
for example `Acme.Assets/Device.UpdateFirmware-1`, in the call's tenant, in deployment state `Deployed`. A method is
announced when at least one tenant has such a pipeline. If one tenant lacks it, the call to that tenant is answered
`METHOD_HANDLER_NOT_AVAILABLE` (`reason` `pipeline-not-deployed`, within one database lookup); the dispatcher cannot tell
tenants apart in the announcement.

**Pipeline input** (JSON, the data root of the pipeline): `{"parameters": {...}, "target": {"ckTypeId", "rtId"},
"method": {"id", "invocationId", "correlationId"}}`. The caller is not in the data: it is the verified principal of the
execution (`ExecutePipelineCaller` with `SubjectId`, `TenantId`, `Roles`; no `Name`) and the raw token is forwarded for
delegation, as for the manual execute endpoint. Use `callerBinding: BindingRequired` on the trigger so that the pipeline
never runs anonymously.

**The principal comes from the validated token, never from the `Caller` member (token validation).** The method queue is
not authenticated per message: anyone who can publish a `CkMethodRequest` can put an administrator into its `Caller`
member. `PipelineMethodInvoker` therefore validates `CallerAccessToken` before it looks at the method, the parameters or
the pipeline, with the shared `ICkMethodCallerTokenValidator` of octo-common-services (package
`Meshmakers.Octo.Services.Methods`, section `Methods:Token`): issuer, signature (keys from the identity JWKS), expiry
(30 s skew), scope `octo_api`, and the tenant rule of `TenantAuthorizationMiddleware` (`tenant_id` equals the request tenant;
a token without `sub` is a service token, needs `client_id`). Registered by `AddPipelineMethodHandler`
(`AddCkMethodCallerTokenValidation`); `Methods:Token:Authority` defaults to `CommunicationController:AuthorityUrl`, the
authority of this service's own HTTP API, so nothing has to be configured. The `ExecutePipelineCaller` is built from the
validated principal only (`PipelineMethodInvoker.BuildCaller`): `SubjectId` = `sub`, or the `client_id` of a service token
(client credentials), `Roles` from the token, `TenantId` the validated tenant, `TrustLevel` **`Strong` (2)**, like a bearer
request of the HTTP API. `request.Caller` is never used for any decision.

| Token | Answer | Pipeline started |
|---|---|---|
| none / empty | `METHOD_FORBIDDEN`, `reason=token-missing` | no |
| expired (otherwise genuine) | `METHOD_FORBIDDEN`, `reason=token-expired` (retry with a fresh token) | no |
| malformed, `alg: none`, foreign issuer or signature, no `octo_api`, service token without `client_id` | `METHOD_FORBIDDEN`, `reason=token-invalid` | no |
| token of another tenant | `METHOD_FORBIDDEN`, `reason=tenant-not-allowed` | no |
| signing keys cannot be loaded (identity unreachable) | `METHOD_HANDLER_FAILED` (the caller did nothing wrong) | no |
| valid | the pipeline runs as the token subject | yes |

The check runs first so a refused caller learns nothing about the methods of the tenant. Nothing from the token (value,
claim, key id) or from the forged `Caller` reaches a log line or the answer; the log names method, invocation, tenant and
the fixed reason. Callers have forwarded their token since AB#5126; jobs use client credentials. The manual execute
endpoint is unchanged (it authenticates by the bearer scheme). Tests: `Methods/PipelineMethodCallerTokenTests` (real validator over
a fake identity service and tokens signed with a throw-away key: one test per refusal asserting that
`StartExecutePipelineAsync` is never called, subject of the token beats the forged `Caller`, service token = `client_id`, log
capture), `Methods/PipelineMethodWiringTests`, integration `PipelineMethodHandlerTests`.

Concept page (marker, who sets it, lease sealing, fail-closed cases, residual risks, rollout order): `docs/concepts/pipeline-method-invocation-marker.md`.

**Method-invocation marker (AB#7460, Feature AB#6884 F3.7, decision note AB#7120).** `PipelineMethodInvoker` is the only
producer of `ExecutePipelineMethodInvocation` (octo-sdk, `Meshmakers.Octo.Communication.Contracts.MessageObjects`): it
builds it from the accepted request (`MethodId` qualified, `InvocationId`, `CkTypeId`) plus the `PipelineRtId` of the
bound pipeline (`PipelineMethodInvoker.BuildMethodInvocation`) and hands it to the new optional parameter
`methodInvocation` (default null) of `ITriggerManagementService.StartExecutePipelineAsync`, which sets it on the
`ExecutePipelineRequest` it publishes (the one place that builds that request). The adapter lets a type whose method
`replacesGenericCreate` be created only in this run (AB#7120). **No public API accepts the marker**: REST
`POST .../pipeline/execute` binds only the query `pipelineRtId`, `isDryRun` and the raw body as pipeline input (a body
with a `methodInvocation` member is plain input), and cron (`LeaseTriggerConsumer`) passes nothing; both leave it null.
No SignalR hub method and no GraphQL field takes the request or the marker. Accepted residual risk: a process with
RabbitMQ publish rights can forge it on the bus, as it can forge `Caller`. **Leased path (AB#7461):** a run queued for a
leased adapter carries the marker sealed inside the queued execution's `SealedCaller` envelope and hands it to the pool
member in `LeaseDto.MethodInvocation` (see "Method-invocation marker on the leased path (AB#7461)" below). Tests:
`Methods/PipelineMethodInvokerTests` (all four fields), `Services/TriggerManagementServiceTests/StartExecutePipelineAsyncTests`,
`Controllers/PipelineControllerExecuteTests` (REST, incl. a body with a `methodInvocation` member), `Consumers/LeaseTriggerConsumerTests` (cron).

**Pipeline output** (`SetPipelineExecutionResult@1` -> `OutputData`):

| OutputData | Execution status | Response |
|---|---|---|
| `{"value": X}`, X fits the result type | Completed | success, `resultJson` = `{"value":X}` |
| empty, method without result | Completed | success without result |
| `{"error": {"code": "C", "parameters": {...}}}`, C declared by the method (or `METHOD_VALIDATION_FAILED`, `METHOD_TARGET_NOT_FOUND`, `METHOD_FORBIDDEN`) | Completed | error C with the parameters (values: string, number, boolean) |
| anything else (no JSON, no `value` for a method with result, wrong type, undeclared code) | Completed | `METHOD_HANDLER_FAILED` with `correlationId`, `field`, `reason=invalid-result` |
| any | Failed with `FailureKind = OutcomeUnknown` (AB#8037) | `METHOD_OUTCOME_UNKNOWN` with `methodId` + `correlationId` (rule R3: a write was not acknowledged and may have taken effect); the error message is logged, never returned |
| any | Failed (other or no `FailureKind`) / Interrupted / Cancelled | `METHOD_HANDLER_FAILED` with `correlationId`; the execution's error message is logged (token masked, 500 characters), never returned |
| not final before the deadline | Running / Queued / not yet written | `METHOD_TIMEOUT`; the pipeline may still finish (not cancelled) |
| the pipeline cannot be started (adapter offline, caller binding refused) | none | `METHOD_HANDLER_NOT_AVAILABLE`, `reason=pipeline-start-failed` |

**Unacknowledged writes in a pipeline method (AB#8037, System.Communication 4.14.0).** The pipeline runs in the adapter, so only
the adapter sees the exception of an ApplyChanges write that MongoDB did not acknowledge (write-concern error,
`UnknownTransactionCommitResult`). It classifies it with the engine's `UnacknowledgedWriteClassifier` (via octo-sdk
`PipelineExecutionFailureKinds.Of`, the same classifier the CK method host of common-services uses for service handlers) and
reports `PipelineExecutionEndDto.FailureKind` / `LeaseResultDto.FailureKind`. The controller stores it on the new optional
runtime-state attribute `PipelineExecution.FailureKind` (enum `PipelineExecutionFailureKind`: `Failed` = 0, `OutcomeUnknown` = 1)
on every completion path - `CompleteExecutionAsync`, `BatchCompleteExecutionsAsync`, the interrupted-result report and the lease
release (`ApplyLeaseOutcomeAsync`) - only for `Failed` (`PipelineExecutionFailureKindMapping.ToCk`; an unknown future value is
stored as `Failed`). `PipelineMethodResultMapper.Map` answers `METHOD_OUTCOME_UNKNOWN` + `correlationId` for `Failed` +
`OutcomeUnknown`; everything else is unchanged. The decision reads only the structured field, never `ErrorMessage` (no free-text
markers). An execution reported by an adapter built before AB#8037 has no kind and stays `METHOD_HANDLER_FAILED`. Tests:
`PipelineMethodResultMapperTests` (`*OutcomeUnknown*`), `PipelineMethodInvokerTests` (`*UnacknowledgedWrite*`,
`*WithoutTheStructuredKind*`), `CompleteExecutionAsyncTests` / `BatchCompleteExecutionAsyncTests` (`*FailureKind*`),
`LeaseOutcomeAndRequeueTests` (`*UnacknowledgedWrite*`).

**Failure kind on the REST pipeline execution (AB#8156).** An operator without a method call sees the kind too: `PipelineDebugController`
(`GET .../PipelineDebug/{pipelineRtId}` history and `.../latest`, read by octo-cli `GetPipelineExecutions` /
`GetLatestPipelineExecution`) returns `PipelineExecutionDataDto.FailureKind` (octo-sdk, `PipelineExecutionFailureKind?`, JSON
`"failureKind": "OutcomeUnknown" | "Failed"`). `PipelineDebugController.ToExecutionData` maps the persisted execution through
`PipelineExecutionFailureKindMapping.FromCk`: the kind of a `Failed` execution, `null` for every other status. An execution
from the in-memory debug cache has no status, so the controller takes the kind of its persisted twin (list: same execution id;
latest: one `GetPipelineExecutionAsync` lookup, a failed lookup only logs). The field is additive; `PipelineExecutionDto`
(the controller's own model) carries the same kind. **Known limitation:** executions that were interrupted (controller restart,
adapter lost) or buffered offline carry no kind - `FailureKind` is `null` and a method caller gets `METHOD_HANDLER_FAILED`.
Tests: `PipelineDebugControllerFailureKindTests`.

**Sensitive parameters and a sensitive result are refused.** A method with a parameter `sensitive: true` (or of value type
`Secret`), or with a result marked `sensitive: true`, is **not served by a pipeline** until the retention of pipeline input and
output is decided (open decisions OD-3, OD-4, OD-9, `methodConsistency.md`): the execution entity stores `InputData` and
`OutputData`. Such a method is not announced, an ERROR names method, tenant and parameter names (never values; for a result only
the word "result") once per change, and a call is answered `METHOD_HANDLER_NOT_AVAILABLE` with
`reason=sensitive-parameters-not-supported` (a sensitive parameter, checked first) or `reason=sensitive-result-not-supported`
(a sensitive result, additive token, wire contract rule 1) without reaching a pipeline. The compiler already refuses
`handler: pipeline` together with a sensitive result (rule 134); the refusal in `PipelineMethodDefinition.RefusalReason` is the
second gate for a hand-crafted or older definition. Lifting this needs the decisions, not a code switch.

**Startup.** By default a pipeline method that cannot be served (no deployed pipeline of that name, sensitive
parameters or result) is logged as ERROR and re-checked on every heartbeat; a misconfigured tenant never takes the controller
down. `PipelineMethods:FailStartupOnInvalidMethod=true` (`OCTO_PIPELINEMETHODS__FAILSTARTUPONINVALIDMETHOD`) makes the
startup throw with a message that names every offending method (the check at startup only).

Configuration (section `PipelineMethods`): `Enabled` (true), `HeartbeatInterval` (15 s), `Ttl` (45 s),
`EndpointReadyTimeout` (30 s), `PollInterval` (100 ms, grows by 50 % up to `MaxPollInterval` 750 ms),
`FailStartupOnInvalidMethod` (false).

**Limits to know.** The outcome is read from the execution store (works across controller nodes, no in-process signal).
Asynchronous methods (operation handle, F3.6) are not part of this increment. A method id must have the same
handler kind in every tenant (the queue is per method id, not per tenant). A pipeline on a Leased adapter is queued by
`StartExecutePipelineAsync`; the call then waits for the lease like any manual execution and can end in
`METHOD_TIMEOUT` when the timeout is short - prefer a dedicated adapter for pipeline methods.

Tests: `tests/CommunicationControllerService.Tests/Methods/` (mapper, invoker, host, bus consumer, catalog, sample
pipeline) and `tests/CommunicationControllerServices.IntegrationTests/Methods/PipelineMethodHandlerTests.cs`
(real MongoDB tenant, real `TriggerManagementService` and execution store; the adapter is played by a stub of the
execute command that reports the execution like the adapter hub does - the pipeline runtime itself is not part of
this repository). The sample pipeline `Methods/SamplePipelines/Device.UpdateFirmware-1.pipeline.yaml` is the one printed
in the guide and is the pipeline definition of the integration tests.
