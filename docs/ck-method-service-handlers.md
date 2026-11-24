---
description: CK method service handlers and the REST facades over the methods hosted by the controller
applies_to: **/Methods/Service/**, **/TenantApi/v1/Controllers/**
---

### CK method service handlers (AB#8595 / AB#8596 / AB#9050, System.Communication 4.17.0 / 4.18.0 / 4.19.0)

System.Communication 4.17.0 declares the adapter, workload and adapter pool operations of the REST API as CK methods, 4.18.0
the pipeline, data flow and workload chart-version operations, 4.19.0 (AB#9050) the deployment site, workload deploy /
undeploy, adapter pool scale and pipeline trigger schedule operations
(`handler: service`), and the controller handles them with the typed handler SDK of octo-common-services
(`AddCkMethodHandlers`): one shared queue per method, announcement, heartbeat, caller token validation, the declared
authorization and redaction are the host's. Since AB#9048 / AB#9049 the REST endpoints are facades of these methods (next
section); routes, bodies and status codes are unchanged.
Code: `src/CommunicationControllerServices/Methods/Service/`. User guide: `octo-documentation` ->
`octo-documentation/src/octo-mesh-documentation/docs/technologyGuide/communication/methods.md` (en + de).

| Method | Kind | REST endpoint it mirrors | Handler | Timeout / idempotent |
|---|---|---|---|---|
| `Adapter.DeployUpdate-1` | instance | `POST Adapter/deployUpdate?adapterRtEntityId=` | `AdapterDeployUpdateHandler` | 90 s / yes |
| `DeployableWorkload.Wake-1` | instance (adapter, application, pool) | `POST Adapter/{workloadRtId}/wake` | `DeployableWorkloadWakeHandler` | 90 s / yes |
| `Adapter.ReconcileServiceAccount-1` | instance | `POST Adapter/{adapterRtId}/serviceAccount/reconcile` | `AdapterReconcileServiceAccountHandler` | 60 s / yes |
| `Adapter.RotateServiceAccountSecret-1` | instance | `POST Adapter/{adapterRtId}/serviceAccount/rotateSecret` | `AdapterRotateServiceAccountSecretHandler` | 60 s / no |
| `AdapterPool.RefreshMirrors-1` | static | `POST AdapterPool/mirrors/refresh` | `AdapterPoolRefreshMirrorsHandler` | 120 s / yes |
| `AdapterPool.PublishMirrors-1` | static | `POST AdapterPool/mirrors/publish` | `AdapterPoolPublishMirrorsHandler` | 120 s / yes |
| `AdapterPool.GrantLease-1` | instance | `POST AdapterPool/{adapterPoolRtId}/lease` | `AdapterPoolGrantLeaseHandler` | 30 s / no |
| `AdapterPool.CancelQueuedExecution-1` | instance | `DELETE AdapterPool/{adapterPoolRtId}/queue/{executionId}` | `AdapterPoolCancelQueuedExecutionHandler` | 15 s / no |
| `Pipeline.Deploy-1` | instance | `POST Pipeline/deploy?adapterRtEntityId=&pipelineRtEntityId=` (body: definition) | `PipelineDeployHandler` | 200 s / yes |
| `Pipeline.Execute-1` | instance | `POST Pipeline/execute?pipelineRtId=&isDryRun=` (body: input) | `PipelineExecuteHandler` | 30 s / no |
| `Pipeline.SetDebugging-1` | instance | `PATCH Pipeline/{pipelineRtId}/debug` | `PipelineSetDebuggingHandler` | 300 s / yes |
| `Pipeline.MoveToAdapter-1` | instance (one pipeline) | `PATCH Pipeline/move-to-adapter` | `PipelineMoveToAdapterHandler` | 200 s / yes |
| `DataFlow.Deploy-1` | instance | `POST DataFlow/deploy?dataFlowRtId=` | `DataFlowDeployHandler` | 300 s / yes |
| `DataFlow.Undeploy-1` | instance | `POST DataFlow/undeploy?dataFlowRtId=` | `DataFlowUndeployHandler` | 300 s / yes |
| `DeployableWorkload.SetChartVersion-1` | instance (adapter, application, pool) | `PATCH Workload/{workloadRtId}/chart-version` | `DeployableWorkloadSetChartVersionHandler` | 15 s / yes |
| `DeploymentSite.Deploy-1` (AB#9050) | instance | `POST DeploymentSite/deploy?deploymentSiteRtId=` | `DeploymentSiteDeployHandler` | 60 s / yes |
| `DeploymentSite.Undeploy-1` (AB#9050) | instance | `POST DeploymentSite/undeploy?deploymentSiteRtId=` | `DeploymentSiteUndeployHandler` | 120 s / no |
| `DeployableWorkload.Deploy-1` (AB#9050) | instance (adapter, application, pool) | `POST DeploymentSite/workloads/deploy?workloadRtId=` | `DeployableWorkloadDeployHandler` | 90 s / yes |
| `DeployableWorkload.Undeploy-1` (AB#9050) | instance (adapter, application, pool) | `POST DeploymentSite/workloads/undeploy?workloadRtId=` | `DeployableWorkloadUndeployHandler` | 60 s / no |
| `AdapterPool.Scale-1` (AB#9050) | instance | `POST DeploymentSite/workloads/adapter-pool/scale?workloadRtId=&replicas=` | `AdapterPoolScaleHandler` | 30 s / yes |
| `PipelineTrigger.DeployAll-1` (AB#9050) | static | `POST PipelineTrigger/deploy` | `PipelineTriggerDeployAllHandler` | 120 s / yes |
| `PipelineTrigger.UndeployAll-1` (AB#9050) | static | `POST PipelineTrigger/undeploy` | `PipelineTriggerUndeployAllHandler` | 120 s / yes |

- **Adding a method (AB#8596 and later).** Declare it in the model (`handler: service` is the default), add a handler class in
  `Methods/Service/` that calls the same service as the REST endpoint, and add one row to `CommunicationMethodHost.Handlers`.
  The unit test `Methods/Service/CommunicationMethodHostTests` fails until every declared method of the three types has a row
  and its contract (role, timeout, idempotency) is listed; extend its `Expected` table and the integration test
  `Methods/CommunicationMethodHostTests` (REST vs. method on the same substitutes).
- **Deployment methods (AB#9050).** `DeploymentSite.Deploy`, `DeployableWorkload.Deploy` and `AdapterPool.Scale` run the
  Communication-enabled gate of AB#4255 (`CommunicationEnabledGate`, shared with the REST code) and answer
  `COMMUNICATION_DISABLED` (REST 409) while Communication is disabled; the undeploys and the trigger methods stay open. Only
  the service's own refusal (`DeploymentSiteServiceException`) becomes the declared error with the REST text; any other
  exception is `METHOD_HANDLER_FAILED` (REST 500 as before). The undeploys are declared not idempotent (a repeated call
  answers "not deployed"). `PipelineTrigger.DeployAll` / `UndeployAll` are static (every trigger of the calling tenant) and
  leave `Deploy-1` free for a later per-trigger method. The deploys are async candidates for F3.6 (the operator reports the
  rollout later in `DeploymentState`). Data beyond the target: `docs/rest-method-facades.md`, "Deployment endpoints".
- **Roles.** The REST policies ask only for the scope `octo_api`. A method without `roles` admits administrators only
  (default-deny of `CkMethodAuthorizationEvaluator`), so every method declares `roles: [CommunicationManagement]` - the role of
  the Studio's integration section and of every pipeline service account. Widening the list later is a Minor, narrowing it a
  Major. `ReconcileServiceAccount` materialises declared roles only when the validated caller also holds `UserManagement`
  (REST parity).
- **No secret in a result.** `RotateServiceAccountSecret` returns client id, well-known name, `wasCreated`,
  `requiresPipelineRedeploy` and the REST message (`AdapterController.BuildRotationMessage`), never the secret; the result is
  therefore not `sensitive`. Reconcile and rotation failures answer the declared error without the exception text (REST echoes
  it); the exception is logged with the invocation id. Redaction test: `Methods/Service/RotateServiceAccountSecretMethodRedactionTests`
  (real provisioning service: the generated secret is in no result, log line or event, on success and on failure).
- **Timeouts and async candidates (AB#8596).** All methods are synchronous; no method sets `execution.mode` (omitted =
  sync, the key alone would raise `minEngineVersion`). `Pipeline.Deploy` / `MoveToAdapter` budget one wake (60 s) plus one
  push (120 s). `DataFlow.Deploy` / `Undeploy` and `Pipeline.SetDebugging` (it re-pushes the data flow) wake and push
  adapter after adapter and use the maximum 300 s; they are the candidates for `execution.mode: async` once F3.6
  (AB#5694) is available - a Major per method (`Deploy-2`, row M19).
- **Sensitive parameters (AB#8596).** `Pipeline.Deploy.pipelineDefinition` (a definition can carry credentials) and
  `Pipeline.Execute.input` are `sensitive`: the host keeps them out of logs, audit and traces. The handlers never log them
  and pass every failure message through `CommunicationMethodSupport.Redact` (the value as a whole and each line of 8+
  characters, plus the caller token), so a validation message that quotes a line does not echo it. `Execute.input` of a
  non-dry-run execution on a **Leased** adapter is stored in the tenant's `PipelineExecution` (`InputData`) and stays there
  after the run (a retry copies it), as with REST.
- **Failure messages (AB#8596, AB#8716).** Every Pipeline / DataFlow error the model declares with "Parameter message" always
  carries `message` (`CommunicationMethodSupport.FailureMessage`). It is the text of the controller's own exceptions
  (`CallerSafeMessage`: `AdapterServiceException`, `AdapterHubCallbackException`, `CommunicationRepositoryException`,
  `WorkloadLifecycleServiceException`, `TriggerManagementServiceException` - the texts REST answers and the Studio shows),
  redacted; for any other exception a fixed text that names the operation and the invocation id (`"The execution could not
  be started; see the communication controller log (invocation <id>)."`) - its own text stays in the log. The redeploy
  `warning` of `MoveToAdapter` still uses `CallerSafeMessage` only. When the execute command gets no answer
  (`DistributionTimeoutException`), `TriggerManagementService` throws `AdapterDidNotAnswer`, which names the adapter as probably offline
  and the outcome as unknown (a timeout does not prove the start failed; also in the REST answer and the error event). Guard: `IntegrationTests/Methods/CommunicationMethodHostTests.FailureMessages`
  compares the errors with "Parameter message" in the model with the covered list and drives each failure path.
- **Parse failures (AB#8596).** `PipelineSchemaValidator` reports a definition that does not parse with a fixed text plus
  line/column (`ParseFailureMessage`), never the parser's text: a duplicate key or broken scalar could quote a credential into
  the logs, the REST answer and the method error. This also changes the REST error text of that case (position kept).
- **Bounded validation (AB#9667).** The definition of a deploy is caller-supplied, so `PipelineSchemaValidator` refuses what
  is too large with one finding `PIPELINE_DEFINITION_LIMIT_EXCEEDED: <limit>` instead of evaluating it
  (`PipelineDefinitionLimits`, the same limits as the MCP tool of AB#9666): more than 1,000,000 characters (before parsing),
  a YAML document the shared `YamlToJsonConverter` refuses (`YamlDocumentLimitExceededException`: more than 200,000 nodes
  once aliases are expanded, more than 256 levels, a recursive or forward alias - checked before the recursive YAML load,
  so a deep document cannot overflow the stack), JSON deeper than 64 levels (Newtonsoft's reader limit inside NJsonSchema,
  which threw into the deploy path before), node lists nested deeper than 24 levels, more than 2,000 nodes. At most 50
  schema findings are returned plus a "... and n more" line. The messages name the limit and never quote the definition.
  A level of If, ForEach or Group adds two JSON levels, a level of Switch four (`cases`, case, `transformations`, node),
  so about 16 nested Switch nodes get the JSON-depth finding before the node-list one; both refuse.
  🔴 **The limits do not depend on `EnablePipelineSchemaValidation` or on an adapter schema** (Kassandra round 1 of
  AB#9667): the flag defaults to false and no deployment sets it, and the gates after the schema validation parse the
  same definition with YamlDotNet, which binds an alias to the shared instance, and walk its nodes recursively (a small
  document of nested aliases means an exponential walk). So `PipelineDefinitionLimits.CheckDefinition` (length,
  converter budget, node checks; malformed YAML is left to the caller) runs unconditionally: first in
  `AdapterService.ValidatePipelineDefinition` (finding as `PipelineSchemaValidationFailed`), on the stored definition
  of a deploy without a supplied one and of every pipeline of a data-flow deploy (`PipelineDefinitionLimitExceeded`),
  fail-closed in the elevation gate (a refused definition must not read as "nothing to authorize"), in
  `PipelineDefinitionService` before every YamlDotNet parse (a refused definition answers like an unparsable one, which
  bounds the REST endpoints `parse-node` and `parse-all-nodes`; `update-node` edits lines and parses no definition), and in
  `CommunicationRepository.ParseTargetPipelineRtIds`. A new reader of a caller-supplied definition calls it first.
  Tests: `Services/AdapterServiceTests/DeployPipelineDefinitionLimitsTests` (flag off, flag on without schema, stored
  definition, data-flow deploy, too many nodes) and `Services/PipelineDefinitionServiceLimitsTests`.
  🔴 **NJsonSchema 11 does not follow the nested `transformations` reference of the generator's schema** (`$defs`, draft
  2020-12): a node inside a control node is not validated at deploy, only root-level nodes are. That is why the
  evaluation is linear here while JsonSchema.Net in the MCP grew about tenfold per nesting level; a validator that starts
  following the reference must use the MCP's dispatch on `type` (`PipelineSchemaCompiler.Discriminate`) and keep
  `PipelineSchemaValidatorBoundsTests` (4 and 8 nested If@1, time and allocation budget) green. Tests:
  `Services/PipelineSchemaValidatorBoundsTests` (generator-shaped schema in `GeneratorShapedPipelineSchema`, copied from
  the MCP tests).
- **Move retries (AB#8596).** `MoveToAdapter` with `redeploy: true` redeploys also when the move is a no-op (REST skips it),
  so a retry after a timed-out first call does not report success without the deploy. Tests:
  `CommunicationMethodHostTests.Pipeline` (integration, `AssertNeverEchoed`), `PipelineMethodSupportTests` (unit).
- **What stays REST (AB#8596).** The tenant-level endpoints of `CommunicationController` are no type operations:
  `enable` / `disable` toggle the communication capability of the tenant (role `TenantManagement`; they install or remove
  the very model the method would be declared in), `GET`/`PUT lifecycle` is tenant configuration, `validate-expression` is
  a stateless utility without an entity, and `encrypt-value` is deprecated (AB#5537, use `ValueOverride.SecretValue`) and
  takes a secret, so it gets no new surface.
- **Execute caller.** `Pipeline.Execute` passes the validated principal (`PipelineMethodInvoker.BuildCaller`, trust level 2)
  and the caller token for delegation like REST; it passes no method-invocation marker (that is for `handler: pipeline`).
  REST accepts the read-only scope for execute; the method requires `octo_api` (the host's scope rule) and the role.
- **Audit texts.** The REST controllers and the handlers share the audit text builders
  (`PipelineController.BuildMoveAuditMessage` / `BuildDebugAuditMessage`, `WorkloadController.BuildChartVersionAuditMessage`);
  the methods write `source: User via CK method` or `source: CI/CD via CK method` (chart version).
- **Differences to REST, all stricter:** an id that names no adapter is `METHOD_TARGET_NOT_FOUND` for `DeployUpdate`,
  `Reconcile...` and `Rotate...`; invalid lease parameters are `METHOD_VALIDATION_FAILED` (`adapterRtId`, `ttlSeconds` 1-3600,
  `borrowerTenantId`); the audit events say `source: User via CK method`. AB#8596: a pipeline that does not exist is
  `METHOD_TARGET_NOT_FOUND` for `Pipeline.Deploy` / `Execute` / `SetDebugging` (REST 400/404), an unknown or malformed
  `adapterRtId` / `targetAdapterRtId` and an invalid `chartVersion` are `METHOD_VALIDATION_FAILED`; `SetChartVersion` checks
  the workload first (`METHOD_TARGET_NOT_FOUND`) and answers a failed write with `WORKLOAD_CHART_VERSION_UPDATE_FAILED` (REST
  answers 404 for both); `Pipeline.Deploy` without
  `adapterRtId` deploys to the assigned adapter (`PIPELINE_ADAPTER_NOT_ASSIGNED` when there is none).
- **Token.** `AddControllerCkMethodCallerTokenValidation` (shared with the pipeline handler, registered once) validates the
  forwarded token against `CommunicationController:AuthorityUrl` (`Methods:Token` overrides).
- **Startup.** The SDK validates the registrations at order 15 against the CK cache of the **system tenant** (filled by the
  default configuration at order 10, which applies the System.Communication blueprint to it). A system tenant without
  System.Communication fails the startup naming the method. Switch: `Methods:Communication:Enabled=false`
  (`OCTO_METHODS__COMMUNICATION__ENABLED`) - no handler, no queue; the REST endpoints keep working and calls answer
  `METHOD_HANDLER_NOT_AVAILABLE`. Since AB#9048 the REST endpoints then take the guarded direct path (next section).

### REST facades over the methods (AB#9048 / AB#9049, F5.3 AB#5701)

The 22 endpoints of the table above (15 since AB#9048 / AB#9049, 7 since AB#9050) call their method through `ICommunicationMethodFacade.ServeAsync`
(`Methods/Service/CommunicationMethodFacade.cs`; envelope to REST answer: `CommunicationRestFacade.cs`). Full description and
the parity table: `docs/rest-method-facades.md`.

- **Method path.** Hosted + declared in the tenant's model + the caller passes the declared authorization (evaluated with the
  pipeline's own `ICkMethodAuthorizationEvaluator`): `ICkMethodDispatcher` -> in-process transport (AB#8411), detached from the
  request, one Method audit event (transport `in-process`). Cancellation token = `IHostApplicationLifetime.ApplicationStopping`,
  never `RequestAborted`: a client that disconnects never stops a started operation (execute, deploy).
- **Guarded direct path** (`Reason=role-missing|scope-missing|switch-off|method-not-declared|input-not-routable`): the endpoint's
  previous code behind `CkMethodCallerTenantCheck`, one audit event (transport `fallback`, outcome `SUCCESS` or
  `REST_<status>`) and one WARN line without target id. There is no unguarded direct path; no principal is refused.
- 🔴 **Rights (decision B, Odin 2026-11-18).** REST authorizes on the scope only (`TenantCommunicationApiReadWritePolicy`);
  the methods declare `CommunicationManagement`. A caller with the scope but without the role keeps REST on the direct path
  (`Reason=role-missing`, measure it before requiring the role - follow-up story). Do not "fix" this by routing every caller
  through the method: that narrows REST for callers such as CD clients.
- 🔴 **Caller inside a handler** (Kassandra round 1 of AB#9048). A handler has no `HttpContext`; services that judge "the
  caller" through `IHttpContextAccessor` (the elevation gate `AdapterService.EnsurePipelineElevationAuthorizedAsync`, AB#5128,
  and `IdentityClientReader`, AB#5027) would take their system path. Every handler opens `CommunicationMethodCaller.Begin(context)`
  first and those services fall back to `CommunicationMethodCaller.Current`. A new handler must open it too; a new service
  that reads `HttpContext.User` or the bearer must fall back to it. Tests: `DeployPipelineElevationGateTests` (method caller),
  `CommunicationRestFacadeTests.ADeployInsideTheHandler_SeesTheCaller_NotTheSystemPath`.
- **Deprecation (AB#9051).** The 22 facade endpoints carry `[OctoRestDeprecated(Wave = CommunicationRestDeprecation.Wave3,
  Link = CommunicationRestDeprecation.MethodsLink)]` (wave `CommunicationWave3`, `TenantApi/v1/CommunicationRestDeprecation.cs`):
  `Link` header, OpenAPI `deprecated`, counter `octo.rest.deprecated.calls`; `Deprecation` only once
  `RestDeprecation:Waves:CommunicationWave3:Since` is set at rollout; never `Sunset` until Gerald decides it. A new facade endpoint
  gets the attribute and a row in `CommunicationRestDeprecationTests` (it pins exactly the marked routes); an endpoint that stays
  REST gets none (list with reasons: `docs/rest-method-facades.md`, "Deprecation").
- **Registration.** `AddCommunicationCkMethodHost` registers the facade, `AddCkMethodAudit`, the evaluator and the CK-cache
  definition source always (switch off: direct path only) and `AddCkMethodDispatcher` when hosted;
  `AddCommunicationMethodDiscovery` adds the registry feed. A test that builds the host needs an `IDistributionEventHubService`
  (the dispatcher's startup service) - see `CommunicationRestFacadeTests`.
- **Status codes.** The deploy / debug / data-flow failures carry the additive error parameter `reason`
  (`adapter-rejected` 422, `adapter-service` 404, `unexpected` 400, `CommunicationMethodSupport.FailureParameters`), so the
  facade keeps the REST status codes. A move list is one method call per pipeline; a refusal of the caller ends it with 403.
- **Controller unit tests** attach the real facade with the switch off (`DirectRestFacade.Attach`, both test projects), so they
  keep testing the REST code itself; `CommunicationMethodHostTests` compares that REST code with the method.
