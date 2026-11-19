# REST facades over the System.Communication methods (AB#9048, AB#9049, AB#9050)

Feature F5.3 (AB#5701, Epic AB#9221 CK v2). The 22 adapter, pool, pipeline, data-flow, workload, deployment-site and
pipeline-trigger operation endpoints of the communication controller are facades of the System.Communication CK methods the
controller hosts (AB#8595, AB#8596; the seven deployment endpoints and their methods since AB#9050, System.Communication
4.19.0). One implementation (the method handler) serves GraphQL and REST; routes, request and response bodies and status
codes of REST stay as they were. REST is not removed and has no `Sunset` header.

Code: `src/CommunicationControllerServices/Methods/Service/CommunicationMethodFacade.cs` (the facade),
`CommunicationRestFacade.cs` (envelope to REST answer), the controllers under `TenantApi/v1/Controllers/`.
Tests: `tests/CommunicationControllerServices.IntegrationTests/Methods/CommunicationRestFacadeTests.cs`.

## How a call is served

Every endpoint hands its call to `ICommunicationMethodFacade.ServeAsync` with the method it stands for, a mapping of the
method envelope to its REST answer, and its previous direct code.

1. **Method path.** When the handlers are hosted (`Methods:Communication:Enabled`, default true), the tenant's CK model
   declares the method and the caller passes the method's declared authorization (role `CommunicationManagement`, scope
   `octo_api`), the call is one CK method call through `ICkMethodDispatcher`. The handler is hosted by this service, so
   the call runs in this process (in-process transport, AB#8411), detached from the HTTP request (no request state reaches
   the handler), through the invocation pipeline: caller tenant (`CkMethodCallerTenantFilter`), declared authorization,
   redaction, **exactly one Method audit event** (transport `in-process`). The call runs under the host's lifetime, not the
   request: a client that disconnects never stops an operation that has started; the method's declared timeout bounds it.
2. **Guarded direct path.** Otherwise the endpoint's previous code runs behind the same checks the method path makes before
   the handler: the caller tenant (`CkMethodCallerTenantCheck`, the rule of the method transports: the token's `tenant_id`
   must name the route tenant; service clients on `TenantAuthorization:CrossTenantServiceClientIds`; the parent-tenant rule),
   then **exactly one Method audit event** with transport `fallback` (also for a refusal or a fault) and one WARN line:

   ```
   {Operation} in tenant {TenantId} ran on the fallback of {MethodId} (Reason={Reason}), invocation {InvocationId}: {Outcome}
   ```

   The line never names the target id, parameters, user or token. `Reason` is one of:

   | Reason | When |
   |---|---|
   | `role-missing` | the caller passed the REST policy but holds no role the method declares |
   | `scope-missing` | the caller has the read-only scope only (`Pipeline/execute` accepts it in REST) |
   | `switch-off` | `Methods:Communication:Enabled=false` |
   | `method-not-declared` | the tenant's System.Communication is older than the method |
   | `input-not-routable` | the REST input is no method call (an id that is no ObjectId / RtEntityId); the direct code answers 400 as before |

   The audit outcome of a direct answer is `SUCCESS` below status 400, otherwise `REST_<status>` (`REST_ITEM_FAILED` for a
   failed pipeline of a move list).

**Rights (decision B, Odin 2026-11-18).** The REST policy (`octo_api` scope) is not changed and no right is widened or
narrowed: a caller with the scope but without `CommunicationManagement` keeps the REST behaviour on the guarded direct
path, measured by `Reason=role-missing`. A follow-up story makes the endpoints require the role once that count is zero.
The communication types have no data permissions of their own, so neither path checks one.

## The caller inside the handler

A handler runs without an HTTP request (detached on the in-process path, none on the bus). Two services judge "the caller
of the request" through the ambient HTTP request: the elevation gate of a pipeline deploy (AB#5128: a pipeline with a node
under `Identity=ServiceAccount` or `System` needs `UserManagement`) and the identity-client lookups (AB#5027, they forward
the caller's bearer). Without a caller both take their system path (elevation allowed, lookup skipped), which would widen a
right for every REST call routed through the method. So every handler opens `CommunicationMethodCaller` first, from the
validated caller of the call (subject, roles) and its forwarded token, and the services read it when there is no HTTP
request. The facades forward the request's bearer with every method call. Background paths open no scope and stay system
paths. This also closes the same gap for GraphQL callers of the methods (since AB#8596): an elevated deploy without
`UserManagement` is refused there too.

## Parity table

| REST endpoint | Method | Differences (the method wins unless the REST contract changes) |
|---|---|---|
| `POST Adapter/deployUpdate?adapterRtEntityId=` | `Adapter.DeployUpdate-1` | an unknown adapter answers 400 as before, with the method's text |
| `POST Adapter/{workloadRtId}/wake` | `DeployableWorkload.Wake-1` | none; the REST call waits within the method timeout (90 s) |
| `POST Adapter/{adapterRtId}/serviceAccount/reconcile` | `Adapter.ReconcileServiceAccount-1` | a failure answers 400 without the exception text (it stays in the log, named by the invocation id) |
| `POST Adapter/{adapterRtId}/serviceAccount/rotateSecret` | `Adapter.RotateServiceAccountSecret-1` | a failure answers 400 without the exception text; the answer never carries a secret (both paths) |
| `POST AdapterPool/mirrors/refresh` | `AdapterPool.RefreshMirrors-1` (static) | none |
| `POST AdapterPool/mirrors/publish` | `AdapterPool.PublishMirrors-1` (static) | none |
| `POST AdapterPool/{adapterPoolRtId}/lease` | `AdapterPool.GrantLease-1` | invalid parameters answer 400 with the field (`borrowerTenantId`, `ttlSeconds`) |
| `DELETE AdapterPool/{adapterPoolRtId}/queue/{executionId}` | `AdapterPool.CancelQueuedExecution-1` | none (409 and 404 keep their texts) |
| `POST Pipeline/deploy?adapterRtEntityId=&pipelineRtEntityId=` | `Pipeline.Deploy-1` | 422 / 404 / 400 kept through the error parameter `reason`; a 400 answers the method's text instead of an unknown exception text; a missing pipeline answers 404 |
| `POST Pipeline/execute?pipelineRtId=&isDryRun=` | `Pipeline.Execute-1` | the execution id at once as before; the pipeline caller carries subject, tenant and roles (no e-mail or name); the start is bounded by the method timeout (30 s) |
| `PATCH Pipeline/move-to-adapter` | `Pipeline.MoveToAdapter-1`, once per pipeline | one call and one audit event per pipeline; with `redeploy` a no-op move redeploys too (idempotent; REST skipped it); a token of another tenant answers 403 for the whole list |
| `PATCH Pipeline/{pipelineRtId}/debug` | `Pipeline.SetDebugging-1` | 422 / 404 / 400 kept through `reason` |
| `POST DataFlow/deploy?dataFlowRtId=` | `DataFlow.Deploy-1` | 422 / 404 / 400 kept through `reason` |
| `POST DataFlow/undeploy?dataFlowRtId=` | `DataFlow.Undeploy-1` | 422 / 404 / 400 kept through `reason` |
| `PATCH Workload/{workloadRtId}/chart-version` | `DeployableWorkload.SetChartVersion-1` | an invalid version answers the REST text; the audit event says `source: CI/CD via CK method` |
| `POST DeploymentSite/deploy?deploymentSiteRtId=` | `DeploymentSite.Deploy-1` | none: 409 while Communication is disabled (`COMMUNICATION_DISABLED`), 400 with the service's text for a refusal (`DEPLOYMENT_SITE_DEPLOY_FAILED`: unknown or Edge site); timeout 60 s |
| `POST DeploymentSite/undeploy?deploymentSiteRtId=` | `DeploymentSite.Undeploy-1` | none: open while Communication is disabled, 400 with the service's text (`DEPLOYMENT_SITE_UNDEPLOY_FAILED`); timeout 120 s (it uninstalls the hosted workloads first) |
| `POST DeploymentSite/workloads/deploy?workloadRtId=` | `DeployableWorkload.Deploy-1` | none: 204, or 200 with `warning` (the method result `warning`); 409 / 400 as for the site; timeout 90 s (one service account provisioning of up to 30 s) |
| `POST DeploymentSite/workloads/undeploy?workloadRtId=` | `DeployableWorkload.Undeploy-1` | none: open while Communication is disabled, 400 with the service's text (`WORKLOAD_UNDEPLOY_FAILED`); timeout 60 s |
| `POST DeploymentSite/workloads/adapter-pool/scale?workloadRtId=&replicas=` | `AdapterPool.Scale-1` | none: a negative count answers the REST text 400 (method: `METHOD_VALIDATION_FAILED`, field `replicas`), 409 / 400 as for the site, 200 with the effective count; a workload that is no adapter pool is the service's refusal (400) as before; timeout 30 s |
| `POST PipelineTrigger/deploy` | `PipelineTrigger.DeployAll-1` (static) | a failure answers 400 with the caller-safe text of the method (the controller's own exception text, otherwise a fixed text; on the method path it names the invocation id) instead of any exception text, on both paths; timeout 120 s |
| `POST PipelineTrigger/undeploy` | `PipelineTrigger.UndeployAll-1` (static) | as `deploy` |

On both paths a token whose `tenant_id` does not name the route tenant (or has none) is answered `403` with
`METHOD_FORBIDDEN` / `tenant-not-allowed` and nothing is called. Platform errors map to the documented status codes of
`CkMethodFacadeResults` (403, 404, 400, 504 for the method timeout, 503 when no handler serves it, 500 otherwise) with an
`ErrorResponse` body that names the code, never a parameter value. A call beyond the method's deadline answers `504`
(`METHOD_TIMEOUT`); REST had no deadline before.

## Deployment endpoints (AB#9050)

- **Communication-enabled gate.** `DeploymentSite.Deploy`, `DeployableWorkload.Deploy` and `AdapterPool.Scale` create or change
  operator-managed cluster resources and refuse while Communication is disabled for the tenant (AB#4255). The gate lives in
  `CommunicationEnabledGate` and runs in the handler (method path, also for GraphQL callers) and in the REST code (direct
  path); both answer the same text. The undeploys and the trigger operations stay open, so remediation always works.
- **Unexpected exceptions.** The site, workload and scale handlers turn only the service's own refusal
  (`DeploymentSiteServiceException`, the text REST answered) into their declared error; any other exception is
  `METHOD_HANDLER_FAILED` (REST: 500, as before; the text stays in the log). The trigger handlers turn every failure into
  their declared error with the caller-safe text (REST answered every failure 400).
- **Idempotency.** The deploys, the scale and the trigger operations are idempotent. The undeploys are not: a second call
  after a successful one answers "not deployed".
- **Async candidates.** The deploys return once the operator was told and the state was written; the operator reports the
  rollout in `DeploymentState` later. Waiting for that report would exceed the accept bound, so `DeploymentSite.Deploy` /
  `Undeploy` and `DeployableWorkload.Deploy` are candidates for `execution.mode: async` once F3.6 (AB#5694) is available.
- **Data beyond the target** (for AB#8420 / AB#7040): `DeploymentSite.Undeploy` undeploys every workload the site hosts;
  `DeployableWorkload.Deploy` reads the hosting site, the Helm repository configuration, the secret defaults and value
  overrides, provisions the adapter's pipeline service account (identity client) and the standalone accounts its pipelines
  use; for an adapter pool it refreshes the `LentAdapterPool` mirrors in the borrowing tenants, for an application it
  projects the application's secret defaults into the tenants it serves; `AdapterPool.Scale` writes the requested count
  through the workload lifecycle; `PipelineTrigger.DeployAll` / `UndeployAll` write the deployment state of every trigger
  of the tenant and the bot service's recurring jobs of the tenant's schedule group. All of it is what the REST endpoints
  did, with the same services; the two cross-tenant writes (pool mirrors, secret default projection) are the service's own
  provisioning paths, not widened by the method.

## Error parameter `reason` (additive)

The deploy, debug and data-flow failures (`PIPELINE_DEPLOY_REJECTED`, `PIPELINE_DEPLOY_FAILED`,
`PIPELINE_DEBUG_CHANGE_FAILED`, `DATA_FLOW_DEPLOY_FAILED`, `DATA_FLOW_UNDEPLOY_FAILED`) carry the parameter `reason` beside
`message`: `adapter-rejected` (REST 422), `adapter-service` (REST 404) or `unexpected` (REST 400). It is a fixed token,
never text of the exception; GraphQL callers see it too. The CK model is unchanged.
