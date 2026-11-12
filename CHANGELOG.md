# Changelog

## Unreleased

- AB#7245 New: secret defaults follow the tenants and the deploy state of their Application. A tenant loaded after the
  deploy (service start, Enable, tenant update) receives the defaults its own and its ancestors' Applications provide
  (`ProvisionForTargetAsync`, never fails the load), and a default no Application provides any more is removed;
  removals are conditional on what was read (incl. `RtVersion`), so a concurrent rewrite survives, and a key is never left
  empty while its replacement fails. **Undeploying an Application withdraws its defaults** from its tenant and subtree,
  where another deployed source takes the key over without a gap. An Application whose redeploy was refused or whose
  Helm rollout failed keeps the defaults its running release projected, as stored (declarations edited after that release
  are not projected until a deploy succeeds); one refused before it was ever deployed offers
  nothing. `LastDeploymentError` of a refused deploy keeps its text (`[tenant] Cannot deploy workload 'x': ...`), so the
  Studio's field marks still work. New endpoint `POST {tenantId}/v1/secretDefaults/reconcile`
  (`TenantCommunicationApiReadWritePolicy`) reconciles the tenant and its subtree and answers one result per tenant
  (status and counts, never a value); use it after deleting an Application. Events about a source's defaults are linked to
  the Application in its own tenant, and the counts of a withdrawal name only that Application's defaults.

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
