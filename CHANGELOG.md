# Changelog

## Unreleased

- AB#9599 Chore: NuGet dependencies raised within the .NET 10 line: NLog.Web.AspNetCore 6.3.0 and the test packages (Microsoft.NET.Test.Sdk 18.12.0, xunit.v3 4.0.3, xunit.runner.visualstudio 4.0.1, coverlet.collector 10.1.1, FluentAssertions 8.11.0, Testcontainers.MongoDb 4.17.0). TUnit stays on 1.65.68 (1.87.12 makes the NLog-capturing tests flaky); the 11.0.0 runtime-aligned packages move with AB#9580.

- AB#9308 New: System.Communication 4.20.0 adds the optional Secret attribute `AiConfiguration.ProviderEndpoint`: the AI provider
  endpoint (for example Claude via Microsoft Foundry) and the key issued for it as ONE sealed JSON value
  `{"baseUrl": "https://...", "apiKey": "..."}`; `apiKey` may be omitted or empty for keyless endpoints (local Ollama, vLLM).
  The URL cannot change without the key being supplied in the same write, so a writer who cannot read the key cannot redirect it.
  Set either `ApiKey` (the provider's default endpoint) or `ProviderEndpoint`, not both. A consumer that reads `ProviderEndpoint`
  never sends `ApiKey` to its `baseUrl`. If only `ProviderEndpoint` is set and it cannot be read, `LlmQuery@1` fails because no key
  is available; it never falls back to another endpoint. `AnthropicAiQuery@1` and older adapters read only `ApiKey`.
  `ProviderEndpoint` is read by `LlmQuery@1` from the octo-mesh-adapter release that contains the matching adapter change on.
  Ownership `Secret` (kept on re-apply, not exported;
  seeds and blueprints must omit it). Additive (a Minor against 4.19.0), no access changes, no migration. JSON key of the
  entity attribute: `providerEndpoint`.

- AB#9051 New: the 22 communication endpoints that are facades of System.Communication methods (AB#9048, AB#9049, AB#9050) are
  deprecated in wave `CommunicationWave3`: every response carries `Link: <…/technologyGuide/communication/methods>;
  rel="deprecation"`, the OpenAPI operations are `deprecated`, and `octo.rest.deprecated.calls` counts the calls per endpoint and
  client id. The `Deprecation` header follows once `restDeprecation.waves.CommunicationWave3.since` is set at the first production
  delivery; there is no `Sunset` header and no sunset date. Response headers only; routes, bodies and status codes are unchanged.
  The endpoints that stay REST and why: `docs/rest-method-facades.md`, "Deprecation".
- AB#9127 Fix: sealed pipeline-execution values (`ExecutionEnvelope`, `PipelineExecution.SealedCaller`) are bound to
  purpose, tenant and execution through the associated data of the key ring (engine `ISecretAttributeProtector.Protect /
  Unprotect` with a `SecretPurpose`). A value produced through a Secret attribute no longer opens as a sealed execution
  value, and a sealed value does not open as a Secret attribute value. Values sealed in the former form (binding only
  in the content) are refused at once (`Unreadable`) and never re-sealed: executions queued, or leased and
  interrupted, at the time of the upgrade are failed at their grant and must be run again. Needs the engine package
  with `SecretPurpose`.

- AB#9050 New: System.Communication 4.19.0 declares the deployment operations as CK methods - `DeploymentSite.Deploy-1` /
  `Undeploy-1`, `DeployableWorkload.Deploy-1` / `Undeploy-1`, `AdapterPool.Scale-1` and the static
  `PipelineTrigger.DeployAll-1` / `UndeployAll-1` (role `CommunicationManagement`) - and the seven endpoints
  `DeploymentSite/deploy`, `undeploy`, `workloads/deploy`, `workloads/undeploy`, `workloads/adapter-pool/scale`,
  `PipelineTrigger/deploy` and `undeploy` are facades of them, like the 15 of AB#9048 (method path in process, guarded direct
  path with `Reason=role-missing` etc., one Method audit event per call). Routes, bodies and status codes are unchanged:
  409 while Communication is disabled (`COMMUNICATION_DISABLED` on the method), 400 with the service's text for a refusal,
  204 / 200 with the warning for a workload deploy, 200 with the effective count for a scale. Behaviour changes: a token
  whose `tenant_id` does not name the route tenant is answered 403 `tenant-not-allowed` on every path; a failing trigger
  deploy or undeploy answers the caller-safe text of the method (the controller's own exception text, otherwise a fixed
  text, with the invocation id on the method path) instead of any exception text, on both paths; an unexpected exception of a site, workload or scale operation
  answers 500 without its text as before, now as `METHOD_HANDLER_FAILED`; the method timeout bounds the call (504: deploys
  60/90 s, undeploys 120/60 s, scale 30 s, triggers 120 s); a started operation no longer depends on the HTTP request.
  Parity table: `docs/rest-method-facades.md`.

- AB#9048 / AB#9049 New: the adapter, adapter pool, pipeline, data-flow and workload operation endpoints (15, Feature F5.3
  AB#5701) are facades of the System.Communication CK methods: a caller with `CommunicationManagement` is served by the
  method in process (one Method audit event, transport `in-process`, detached from the request); every other caller that
  passes the REST policy keeps the previous REST code behind the caller-tenant check, with one audit event (transport
  `fallback`) and a WARN line `Reason=role-missing|scope-missing|switch-off|method-not-declared|input-not-routable`. Routes,
  bodies and status codes are unchanged; REST is not removed and gets no `Sunset` header. Behaviour changes: a token whose
  `tenant_id` does not name the route tenant (or has none) is answered 403 `tenant-not-allowed` on every path, also while
  the HTTP tenant gate only logs; failures of reconcile, rotate, deploy, debug, data flow and execute answer the method's
  text instead of the text of an unknown exception; `move-to-adapter` with `redeploy` redeploys a no-op move too and writes
  one audit event per pipeline; the execute caller handed to the pipeline carries subject, tenant and roles (no e-mail or
  name); a started operation no longer depends on the HTTP request (a client that disconnects does not stop it); the
  method timeout bounds the call (504). The deploy, debug and data-flow method errors carry the additive parameter
  `reason` (`adapter-rejected`, `adapter-service`, `unexpected`). Parity table: `docs/rest-method-facades.md`.
  Every System.Communication method handler now hands its validated caller and forwarded token to the services it calls
  (`CommunicationMethodCaller`): the elevation gate of a pipeline deploy (AB#5128) and the identity-client lookups (AB#5027)
  judge that caller instead of taking the system path. This keeps the REST rule on the method path and also fixes the method
  calls through GraphQL (since AB#8596): an elevated deploy, data-flow deploy, debug toggle or move with redeploy without
  `UserManagement` is refused there as over REST.

- AB#7247 Docs: step 9 of the secret-default verify README (Trace sweep) also sets `Logging__LogLevel__Default=Trace`
  and `Logging__LogLevel__Microsoft.AspNetCore=Trace` for the Mesh Adapter and removes them on restore. The adapter host
  keeps the `appsettings.json` filters (`Default` = `Information`) in front of NLog, so `OCTO_LOG_LEVEL` /
  `OCTO_LOG_LEVEL_ROOT` alone showed no Debug or Trace line (found on kind). The Communication Operator is not affected:
  its NLog provider drops those filters. The restore no longer relies on the next release deploy, which keeps variables
  the chart does not render. On kind the sweep waits for the rollout after `set env`, and a value the deployment set
  before the sweep is noted (read one variable at a time, so no other variable of the deployment is printed) and
  restored instead of removed. That check now comes before the first `set env`, counts the names per service (two for
  the Operator, four for the Mesh Adapter), and stops for a name the deployment takes from a ConfigMap or Secret
  (`valueFrom`), so the sweep never overwrites or deletes such a reference. Every `set env` of the sweep and the restore
  targets the service's container with `-c <container>` (a sidecar never gets the variables), the check reads that
  container only, and the restore waits for `rollout status --timeout=5m` as the raise does.

- AB#7245 Fix: the reconcile endpoint `POST {tenant}/v1/secretDefaults/reconcile` now stores the same Information event
  (`Secret defaults reconciled: n created, n updated, n removed, n kept.`) in every tenant where it wrote or removed a
  default, as the tenant load did. The event moved from the tenant-load hook into `ProvisionForTargetAsync`, so both
  paths use one implementation and a load stores it once; a failing event store no longer turns a successful reconcile
  into an Error event. The hand-over of a withdrawal stays without target events (the source's `withdrawn` event reports
  it). The removal pass of a target reconcile skips the keys its write pass wrote in the same run, so a default whose
  source fields a tenant edited is restored without a futile conditional delete and the misleading "was written again
  since it was read" log. The controller log line of the endpoint also names the updated and unchanged counts.

- AB#7247 Test: the secret-default sample proves that a tenant admin cannot read a projected default.
  `verify/prove-no-reveal.sh` tries the typed and the generic GraphQL root, `ExportRtByDeepGraph`, `ExportRtByQuery`,
  `RevealSecret@1` as caller and as service account, `GetPipelineConfigByCkTypeId@1` and `VerifyCaptcha@1` through
  `ExecutePipeline`, reads each probe's outcome from the persisted `PipelineExecution` entity (the debug cache has no
  status) and searches answers, exports, debug points and execution history for the sentinel; the new blueprint
  `SecretDefaultDemoProbe-1.0.0` seeds the probe query and pipelines (the CAPTCHA probe stores its verdict with
  `SetPipelineExecutionResult@1`). The verify README gains a
  shared-environment section (context prefix `OCTO_CONTEXT_PREFIX`, `DeployDeploymentSite`, the route through
  `ExecutePipeline`) and a Trace log sweep. Unit tests `RestoredSecretDefaultReconcileTests`: a SecretDefault restored
  into a tenant that no Application of its source reaches, or naming no existing Application, is removed by the next
  reconcile.

- AB#9121 Fix: the deployment-state writes of pipelines and pipeline triggers (`SetPipelineDeploymentStateAsync`,
  `SetPipelineTriggerDeploymentStateAsync`) repeat a transient MongoDB write conflict (code 112 or
  `TransientTransactionError`) up to two times with the AB#6435 `StateWriteRetry`, like the communication-state writes.
  On prod-2 (tenant sbeg, 2026-11-15) tenant setup failed because setting a trigger to `Pending` raced the adapter's
  re-registration. These writes have no timestamp guard, so a retried write is last-writer-wins and can land after the
  write that won the conflict (50-300 ms later), as an uncontended write would. Other errors are still not retried.

- AB#9030 Fix: the hub refusal and LogOnly inventory log lines of `/operatorHub`, `/{tenant}/adapterHub` and
  `/adapterPoolHub` now name where the connection came from: request path, remote address, the `X-Forwarded-For`
  chain, the user agent and, for a bearer token that was presented but not accepted, the unverified `client_id`
  inside it. Caller-controlled text is stripped of control characters and capped at 200 characters; the token is never
  logged. Runbook `hub-authorization-enforce-rollout.md` section 4 says to identify the client before any rollback.

- AB#7245 Fix: a SecretDefault a tenant deleted with the generic delete (which archives it) is restored by the next
  reconcile. The upsert found the archived entity under the key's RtId and updated it in place, so it stayed archived:
  the tenant lost the default for good, and every reconcile wrote it again and stored a new event. The tombstone is now
  erased and the default inserted again (`Created`); concurrent writers still end with exactly one entity. A conditional
  removal that meets an archived default erases it as well (`NotFound`) instead of keeping it as `Changed`.

- AB#8982 Fix: blueprint apply (install and update), tenant load and workload deploy now also reconcile every standalone
  `ServiceAccountConfiguration` that pipelines link via `Uses` (per-pipeline overrides such as `accounting-ai-prompt`):
  its identity client is created with the declared roles, so `DeployDataFlow` no longer needs a manual
  `POST {tenantId}/v1/serviceAccount/reconcile?configurationRtId=…` first. Adapter accounts are reconciled first (they are
  the `MayActAs` actors); each linked account is isolated, a failure becomes a tenant error event. Accounts with a foreign
  `IssuerUri`/`TenantId` are skipped, and the automatic paths never re-key an account that already declares a ClientId (also
  not on impersonation or when its actors could not be looked up): the identity client keeps its secret, so an
  impersonation switch still waiting for its redeploy is not cut short. Fresh secrets for such accounts come from
  the manual reconcile or the rotate endpoint. The adapter health `GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/health` reports these accounts
  in the new `LinkedConfigurations` list (impersonation judged with that adapter as the actor) and rolls them up
  through the check `linkedConfigurations` (`linked-configuration-unhealthy`). Known limit: these accounts have no
  background retry (the AB#8145 retry is per adapter); after a transient identity failure the health shows it, and the next tenant load, blueprint apply or
  workload deploy, or the manual reconcile endpoint, heals it.

- AB#7245 New: secret defaults follow the tenants and the deploy state of their Application. A tenant loaded after the
  deploy (service start, Enable, tenant update) receives the defaults its own and its ancestors' Applications provide
  (`ProvisionForTargetAsync`, never fails the load), and a default no Application provides any more is removed;
  removals are conditional on what was read (incl. `RtVersion`), so a concurrent rewrite survives. A failed replacement
  keeps the old value only while its source still reaches the tenant; revocation wins otherwise (tenant left
  `AllowedTenantIds`, moved to another parent, key withdrawn, Application undeployed), and the key stays empty until the
  replacement can be written. **Undeploying an Application withdraws its defaults** from its tenant and subtree,
  where another deployed source takes the key over without a gap. An Application whose redeploy was refused or whose
  Helm rollout failed keeps the defaults its running release projected, as stored (declarations edited after that release
  are not projected until a deploy succeeds); one refused before it was ever deployed offers
  nothing. `LastDeploymentError` of a refused deploy keeps its text (`[tenant] Cannot deploy workload 'x': ...`), so the
  Studio's field marks still work. New endpoint `POST {tenantId}/v1/secretDefaults/reconcile`
  (`TenantCommunicationApiReadWritePolicy`) reconciles the tenant and its subtree and answers one result per tenant
  (status and counts, never a value); use it after deleting an Application. Events about a source's defaults are linked to
  the Application in its own tenant, and the counts of a withdrawal name only that Application's defaults. A value that
  cannot be revealed on the controller's host (no key ring, strict mode against clear text) fails only its own key, on the
  deploy and on the reconcile. Known limits: a first install whose rollout failed keeps its defaults until it is undeployed
  or deployed successfully; an Application in Error offers nothing to a tenant created meanwhile; and while an Application
  is in Error, a copy a tenant edited stays edited and a deleted copy is not restored until a deploy succeeds.
  An invalid entry (no value, duplicated key) is not projected, but its `AllowedTenantIds` still apply: a tenant it
  excludes loses the default it holds, and of duplicates a tenant keeps it only while every duplicate allows it. The
  Error events of a deploy are best effort: an event store that fails stops neither the other keys nor the sweep.

- AB#7244 New: deploying an Application projects its `SecretDefaults` (System.Communication 4.15.0) into the
  platform-owned `System.Communication/SecretDefault` of its own tenant and its whole subtree, narrowed by
  `AllowedTenantIds`, never siblings. The value is revealed once in the source and encrypted by the engine in each target;
  it never becomes a Helm value, a log line or an event. The source closer to the root wins; a tenant that is unavailable
  or lacks the model is skipped with a Warning event. The deploy guard refuses an invalid declaration (empty or malformed
  key, duplicate key, missing `SecretValue`) before anything reaches Helm.

- AB#8716 Fix: every System.Communication method error declared with a `message` parameter carries it.
  `Pipeline.Execute` answered `PIPELINE_EXECUTION_START_FAILED` without `message`, because `TriggerManagementServiceException`
  was not on the caller-safe list; it is now (redacted of input and token). For an exception of unknown origin the handlers
  answer a fixed text with the invocation id instead of dropping the parameter (Deploy, Execute, SetDebugging, DataFlow
  Deploy/Undeploy). An execute command without answer now fails with "Adapter '<name>' did not answer ... in time: it is probably
  offline or has not deployed the pipeline. The outcome of the execution is unknown." - also in the REST answer and the error
  event (before: "Failed to execute data flow ...").
- AB#8596 New: System.Communication 4.18.0 (Minor) declares the Pipeline, DataFlow and workload chart-version operations of
  the REST API as CK methods handled by the controller: `Pipeline.Deploy`, `Pipeline.Execute`, `Pipeline.SetDebugging`,
  `Pipeline.MoveToAdapter` (one pipeline per call), `DataFlow.Deploy`, `DataFlow.Undeploy` and
  `DeployableWorkload.SetChartVersion`, with three result records shaped like the REST answers. Roles
  `[CommunicationManagement]`; `pipelineDefinition` and `input` are `sensitive` parameters and are also masked in failure
  messages; `minEngineVersion` stays 3.5.1. The tenant-level endpoints (enable/disable, lifecycle, validate-expression,
  encrypt-value) stay REST. REST endpoints unchanged; their audit texts moved into shared helpers
  (`PipelineController.BuildMoveAuditMessage` / `BuildDebugAuditMessage`, `WorkloadController.BuildChartVersionAuditMessage`),
  pinned by tests. A definition that does not parse is reported with a fixed text plus line/column instead of the parser's
  text (it could quote a credential; also changes that REST error text). Details: CLAUDE.md "CK method service handlers".

- AB#8595 New: System.Communication 4.17.0 (Minor) declares the adapter, workload and adapter pool operations of the REST
  API as CK methods, and the controller handles them (`handler: service`, typed handler SDK of octo-common-services):
  `Adapter.DeployUpdate`, `Adapter.ReconcileServiceAccount`, `Adapter.RotateServiceAccountSecret`, `DeployableWorkload.Wake`,
  `AdapterPool.RefreshMirrors` / `PublishMirrors` (static), `AdapterPool.GrantLease` and `AdapterPool.CancelQueuedExecution`,
  with four result records shaped like the REST answers. Every method declares `roles: [CommunicationManagement]`, an
  explicit timeout and idempotency, no sensitive marker (the rotation result carries no secret, like REST) and the default
  `dataAccess`, so `minEngineVersion` stays 3.5.1. The REST endpoints are unchanged. The `SecretDefault.Value`
  acknowledgement is removed: 4.16.0 is published and already carries it, so CI's compile gate (diff against the
  published 4.16.0) failed with `OCTO-CK204` on main. Switch:
  `Methods:Communication:Enabled` (default true). Details: CLAUDE.md "CK method service handlers".

- AB#8594 New: System.Communication 4.16.0 is the first `ckLanguage: 2` release of the model, a Minor against 4.15.0.
  Every type and record declares `derivable: Any` (dependents keep deriving), `DataFlow`, `LentAdapterPool`,
  `DeployableEntity`, `PipelineTrigger` and `DataPointMapping` implement the System interfaces `Named-1`, `Describable-1`
  and `Enableable-1` they already satisfied (System floor 2.5 → 2.7), and `SecretDefault.Value` is `access: Hidden`
  (acknowledged first-release exception, row T7a): no GraphQL, REST, MCP, import or export path reads or writes it, the
  controller keeps writing it through its repository. `minEngineVersion` 3.5.1, `ck-models/v3/` only. A merge does not
  publish the model (`publishSystemCkModels`). Details: model `CHANGELOG.md`, CLAUDE.md "System.Communication on
  ckLanguage 2". The integration test `SystemCommunication411DependentsTests` now also maps the System.Bot closure pin of
  the published fixtures to the embedded System.Bot (3.6.0 since AB#8593).

- AB#8582 Fix: the service-account health check no longer reports every adapter account as Unhealthy because of
  `FileManagement`. The identity service grants that role by design to every `octo-pipeline-sa-*` client (AB#6467) and
  the controller's reconcile never removes it, so it is now expected: the new named list
  `PipelineServiceAccountProvisioningService.IdentityGrantedRoleNames` (next to `BaselineRoleNames`) holds the roles
  identity grants. They are tolerated, not required - an account without `FileManagement` (System.Files not installed)
  is not drift either - and only for clients with the `octo-pipeline-sa-` prefix; on a custom client id the role stays
  unexpected. Any other role that was not declared is still reported as superfluous. The rights analysis gives the same
  answer: a declared `FileManagement` is a baseline role, not superfluous, and a type that `FileManagement` grants is
  covered.

- AB#8563 Fix: pipeline service accounts hold `StreamDataReader` in addition to their declared roles, so pipelines that
  read stream data (EC allocation, billing, simulation) keep working when the asset repository enforces the stream-data
  read role (AB#8559, `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT=Enforce`, error `StreamDataReadRoleRequired`).
  The role is a baseline (`PipelineServiceAccountProvisioningService.BaselineRoleNames`) added when a declarative account
  (`AssignedRoleNames` set; every account created since CK 3.32.0) is reconciled; it is not written into
  `AssignedRoleNames` and does not replace declared roles. Existing accounts get it on the next system-initiated
  reconcile (tenant start or `clearCache`, workload deploy), without a manual step. The service-account health check and
  the rights analysis treat it as expected. **Operator note:** legacy accounts without `AssignedRoleNames` (provisioned
  before CK 3.32.0) are not touched - grant `StreamDataReader` to them by hand
  (`octo-cli AddClientToRole --clientId octo-pipeline-sa-<adapterRtId> --roleName StreamDataReader`) before switching
  the asset repository to `Enforce`.
