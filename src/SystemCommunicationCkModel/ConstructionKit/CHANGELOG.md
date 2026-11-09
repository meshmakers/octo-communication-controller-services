# Changelog

## 4.18.0 - 2026-11-09

_Required bump level: minor._

### Added

- Method 'DataFlow-1/Deploy-1' added _(minor — purely additive method)_
- Method 'DataFlow-1/Undeploy-1' added _(minor — purely additive method)_
- Method 'DeployableWorkload-1/SetChartVersion-1' added _(minor — purely additive method)_
- Method 'Pipeline-1/Deploy-1' added _(minor — purely additive method)_
- Method 'Pipeline-1/Execute-1' added _(minor — purely additive method)_
- Method 'Pipeline-1/SetDebugging-1' added _(minor — purely additive method)_
- Method 'Pipeline-1/MoveToAdapter-1' added _(minor — purely additive method)_
- Attribute 'MethodResultAppliedToRunningAdapter-1' added _(minor — purely additive element)_
- Attribute 'MethodResultDebuggingEnabled-1' added _(minor — purely additive element)_
- Attribute 'MethodResultExecutionId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultNewAdapterRtId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultOldAdapterRtId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultStartedAt-1' added _(minor — purely additive element)_
- Attribute 'MethodResultWarning-1' added _(minor — purely additive element)_
- Record 'PipelineDebugResult-1' added _(minor — purely additive element)_
- Record 'PipelineExecutionStartResult-1' added _(minor — purely additive element)_
- Record 'PipelineMoveResult-1' added _(minor — purely additive element)_

The Pipeline, DataFlow and workload chart-version operations of the REST API as CK methods (AB#8596, Feature AB#5697 F4.3),
handled by the communication controller (`handler: service`); the REST endpoints are unchanged. Every method declares
`roles: [CommunicationManagement]`. `Pipeline.Deploy.pipelineDefinition` and `Pipeline.Execute.input` are `sensitive`
parameters (never logged, audited or traced); no result is sensitive and no method sets `execution.mode` (omitted = sync),
so `minEngineVersion` stays 3.5.1. `DataFlow.Deploy`, `DataFlow.Undeploy` and `Pipeline.SetDebugging` use the maximum
timeout of 300 s and are candidates for `execution.mode: async` once F3.6 (AB#5694) is available. The tenant-level
operations of `CommunicationController` (enable/disable, lifecycle, validate-expression, encrypt-value) stay REST service
endpoints. No access changes and no acknowledgement.

## 4.17.0 - 2026-11-09

_Required bump level: minor._

### Added

- Method 'Adapter-1/DeployUpdate-1' added _(minor — purely additive method)_
- Method 'Adapter-1/ReconcileServiceAccount-1' added _(minor — purely additive method)_
- Method 'Adapter-1/RotateServiceAccountSecret-1' added _(minor — purely additive method)_
- Method 'AdapterPool-1/RefreshMirrors-1' added _(minor — purely additive method)_
- Method 'AdapterPool-1/PublishMirrors-1' added _(minor — purely additive method)_
- Method 'AdapterPool-1/GrantLease-1' added _(minor — purely additive method)_
- Method 'AdapterPool-1/CancelQueuedExecution-1' added _(minor — purely additive method)_
- Method 'DeployableWorkload-1/Wake-1' added _(minor — purely additive method)_
- Attribute 'MethodResultAdaptersRelinked-1' added _(minor — purely additive element)_
- Attribute 'MethodResultClientId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultConfigurationWellKnownName-1' added _(minor — purely additive element)_
- Attribute 'MethodResultGranted-1' added _(minor — purely additive element)_
- Attribute 'MethodResultIsNoOp-1' added _(minor — purely additive element)_
- Attribute 'MethodResultLeaseId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultLendersUnreadable-1' added _(minor — purely additive element)_
- Attribute 'MethodResultMemberId-1' added _(minor — purely additive element)_
- Attribute 'MethodResultMessage-1' added _(minor — purely additive element)_
- Attribute 'MethodResultMirrorsCreatedOrUpdated-1' added _(minor — purely additive element)_
- Attribute 'MethodResultMirrorsRemoved-1' added _(minor — purely additive element)_
- Attribute 'MethodResultOutcome-1' added _(minor — purely additive element)_
- Attribute 'MethodResultRequiresPipelineRedeploy-1' added _(minor — purely additive element)_
- Attribute 'MethodResultRoleChangesSkipped-1' added _(minor — purely additive element)_
- Attribute 'MethodResultStatusMessage-1' added _(minor — purely additive element)_
- Attribute 'MethodResultTenantsReconciled-1' added _(minor — purely additive element)_
- Attribute 'MethodResultWasCreated-1' added _(minor — purely additive element)_
- Record 'AdapterPoolLeaseResult-1' added _(minor — purely additive element)_
- Record 'AdapterPoolMirrorSyncResult-1' added _(minor — purely additive element)_
- Record 'ServiceAccountReconcileResult-1' added _(minor — purely additive element)_
- Record 'ServiceAccountSecretRotationResult-1' added _(minor — purely additive element)_

The adapter, workload and adapter pool operations of the REST API as CK methods (AB#8595, Feature AB#5697 F4.3), handled by
the communication controller (`handler: service`). The REST endpoints are unchanged. Every method declares
`roles: [CommunicationManagement]` (the REST policy asks only for the scope `octo_api`; a method without roles admits
administrators only). No result is `sensitive`: `RotateServiceAccountSecret` returns the client id and what to do next, never
the secret, like the REST endpoint. No access changes and no acknowledgement: the `SecretDefault.Value` tightening was
released (and acknowledged) in 4.16.0, so it is not part of this release's diff against the published 4.16.0.

## 4.16.0 - 2026-11-09

_Required bump level: minor._

### Added

- Implemented interface 'DataFlow-1/System@2/Named-1' added _(minor — type implements an additional interface)_
- Implemented interface 'DataFlow-1/System@2/Describable-1' added _(minor — type implements an additional interface)_
- Implemented interface 'DataPointMapping-1/System@2/Enableable-1' added _(minor — type implements an additional interface)_
- Implemented interface 'DeployableEntity-1/System@2/Describable-1' added _(minor — type implements an additional interface)_
- Implemented interface 'LentAdapterPool-1/System@2/Named-1' added _(minor — type implements an additional interface)_
- Implemented interface 'LentAdapterPool-1/System@2/Describable-1' added _(minor — type implements an additional interface)_
- Implemented interface 'PipelineTrigger-1/System@2/Enableable-1' added _(minor — type implements an additional interface)_

### Changed

- Model 'System.Communication': ckLanguage changed '1' → '2' _(minor — CK language version raised; older engines reject the model with a clear error)_
- Dependency range 'System': range changed '[2.5,3.0)' → '[2.7,3.0)' _(minor — dependency range changed within the same major (row D2/D3))_
- Dependency range 'System': floor changed '2.5.0' → '2.7.0' _(minor — dependency floor raised — tenants need the newer dependency version (row D1))_
- Attribute 'SecretDefaultValue-1': securitySensitive changed 'false' → 'true' _(minor — security-sensitivity marker set or cleared, no data or API change (row A3))_

### Behavioural changes

- Type attribute 'SecretDefault-1/Value': access changed 'ReadWrite' → 'Hidden' _(minor — access/security: access tightened on an attribute marked securitySensitive in the first ckLanguage 2 release of this classic model — accepted security exception (once per model), requires acknowledge (row T7a) — requires acknowledge)_

### Acknowledged changes

- Type attribute 'SecretDefault-1/Value': access changed 'ReadWrite' → 'Hidden' _(minor — acknowledged: AB#8594: SecretDefault.Value is platform-only (AB#7243); only the controller writes it through its repository, no client reads or writes it through GraphQL, REST, MCP, import or export, so Hidden closes the generic API path without breaking a caller)_

First ckLanguage 2 release of System.Communication (AB#8594, Feature AB#5697 F4.3, Epic AB#5584). Every type and record
declares `derivable: Any` explicitly: ckLanguage 2 changes the default to `Model`, and without the explicit value every type
and record would be a MAJOR change (row T6, `OCTO-CK200`) and the adapter and AI models deriving from them would stop compiling
(message 113). `visibility` stays `Public`, attribute `access` stays `ReadWrite` except `SecretDefault.Value`.

### Compatibility

- The compiled model equals the published 4.15.0 after the version, `ckLanguage`, `minEngineVersion`, `derivable`, `implements`,
  `securitySensitive`, `access`, `compatibility` and the dependency closure/ranges are removed (same normalised SHA-256). Ids,
  attributes, defaults, ownership, indexes and association roles are unchanged.
- `minEngineVersion` 3.5.1 (the CK v2 minimum), published under `ck-models/v3/` only.
- System floor 2.5 → 2.7 (the System interfaces exist from 2.7): Minor, because 4.15.0 already resolved System 2.8.0 (row D1).
- Interfaces only where the type already carried the members with the required multiplicity. Left out: `Pipeline` for
  `Enableable-1` (`Enabled` is optional), `DeployableEntity`, `EnergyCommunityConfiguration` and `DataPointMapping` for `Named-1`
  (`Name` is optional), `SecretDefault` (platform-only entity, kept out of cross-type interface queries), `Hierarchical-1`
  (`DataFlow`, the parent of pipelines and triggers, has no `ParentChild` role of its own).
- Access: only `SecretDefault.Value` is tightened (Hidden, row T7a, the once-per-model exception). The configuration credentials
  (`Password`, `ClientSecret`, `PrivateKey`, `PrivateKeyPassphrase`, `BotToken`, `ApiKey`, `AdminPassword`, `SecretValue`,
  `SecretDefaultProjection.SecretValue`) stay `ReadWrite`: Refinery Studio writes them through the generic GraphQL update. The
  service-written state (`CommunicationState`, `DeploymentState`, last-error and statistics attributes) stays `ReadWrite`: these
  attributes are not `securitySensitive`, so `ReadOnly` would be a MAJOR (row T7). No archive-stored attribute is Hidden (AB#6247).
- Dependents: `Loxone-5.4.0` and `System.Ai-4.7.0` are Compatible (range retention, no rebuild or bump); the exact-pinned
  legacy majors (`Loxone-4.11.0`, `System.Ai-3.13.0`) stay on System.Communication 3.x (NotInRange).

## 4.15.0 - 2026-11-03

_Required bump level: minor._

### Added

- Type 'SecretDefault-1' added _(minor — purely additive element)_
- Type attribute 'Application-1/SecretDefaults' added (System.Communication/SecretDefaults-1) _(minor — purely additive optional attribute)_
- Attribute 'SecretDefaultAllowedTenantIds-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultDescription-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultKey-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultLastProjectedAt-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultSourceTenantId-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultSourceWorkloadRtId-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaultValue-1' added _(minor — purely additive element)_
- Attribute 'SecretDefaults-1' added _(minor — purely additive element)_
- Record 'SecretDefaultProjection-1' added _(minor — purely additive element)_

Platform secret defaults (AB#7243): an `Application` can declare `SecretDefaults` (record `SecretDefaultProjection`: `Key`,
`SecretValue` (Secret), optional narrowing-only `AllowedTenantIds`, optional `Description`; no scope field, the target set is
always the Application's own tenant plus its whole subtree). The new final type `SecretDefault` (derived from
`${System}/Entity`, deliberately not from a Configuration type) is the platform-owned place in a served tenant: provisioned by
the controller, never author-set, `Value` platform-only (no tenant pipeline, user or role may reveal it); every attribute
except `Value` is runtime state. `Value`, `SourceTenantId` and `SourceWorkloadRtId` are optional although the controller
always sets them: export drops Secret and runtime-state values, so a required one would make an exported entity unimportable. `SecretDefaults` is not a ValueOverride and never reaches Helm values or the pod. This
version defines the model contract (record, attribute, type); the projection into tenants is the controller's job (AB#7244, AB#7245) and is not part of this model version.

Compatibility: additive only (new record, new type, new optional attribute on `Application`), a minor bump over 4.14.0.
Dependents with a range `System.Communication-[4.x,5.0)` are unaffected, an Application without `SecretDefaults` imports
unchanged, and no migration file is needed. Exact-pinned (v1) models and externally built adapters may need a rebuild after
the catalog publish; the maintainer who releases checks this. No CK engine change.

### Changed

- Dependency 'System': version changed '2.7.0' → '2.8.0' _(none — resolved dependency System moved 2.7.0 -> 2.8.0 inside its declared range [2.5,3.0) of a range-retaining model; the closure is compiler-set metadata and the model itself is unchanged, no bump needed (row D8))_
- Type 'Application-1': description changed 'A tenant-specific web application deployed by the Communication Operator via Helm. Examples: energy-community, voest-app, maco-app. Most configuration (URIs, env vars, secrets) lives in ValuesYaml / Values inherited from DeployableWorkload; only operator-relevant fields are exposed as typed attributes. Ingress (IngressEnabled + Hostname) is inherited from DeployableWorkload.' → 'A tenant-specific web application deployed by the Communication Operator via Helm. Examples: energy-community, voest-app, maco-app. Most configuration (URIs, env vars, secrets) lives in ValuesYaml / Values inherited from DeployableWorkload; only operator-relevant fields are exposed as typed attributes. Ingress (IngressEnabled + Hostname) is inherited from DeployableWorkload. Optional SecretDefaults (AB#7243) declare secrets the platform provides to every tenant the application serves, never through Helm values.' _(patch — purely documentational change)_

## 4.14.0 - 2026-11-01

_Required bump level: minor._

### Added

- Enum 'PipelineExecutionFailureKind' (`Failed` = 0, `OutcomeUnknown` = 1) _(minor — additive new enum)_
- Attribute 'FailureKind' (Enum, optional, isRuntimeState) on 'PipelineExecution' _(minor — additive optional attribute)_

A failed pipeline execution says what it means for its writes (AB#8037, rule R3): the adapter reports
`OutcomeUnknown` when a write was not acknowledged (MongoDB write-concern error, `UnknownTransactionCommitResult`)
and may have taken effect, and a pipeline method then answers `METHOD_OUTCOME_UNKNOWN` + `correlationId` instead of
`METHOD_HANDLER_FAILED`, like a service method. Compatibility: additive only; existing executions and executions
reported by older adapters have no value (treated as `Failed`). Dependents with range-retained dependencies keep
resolving; no migration (schema-only bridge).

## 4.13.0 - 2026-10-26

_Required bump level: minor._

### Added

- Type 'MqttConfiguration' (final, derived from `${System}/Configuration`) _(minor — additive new type)_

Connection to an MQTT broker (AB#7506): `Host`, `Port`, `Username`, `Password` (Secret, optional - a Secret is never
seeded) and `IsSslEnabled`, reusing the shared attribute definitions, exactly like `LoxoneConfiguration`. A pipeline
attaches it via `System.Communication/Uses`, and the controller ships it with the password revealed to the adapter, so
the broker password no longer sits in the pipeline YAML. Compatibility: additive only, no existing type or attribute
changes; adapters and blueprints that do not use the type are unaffected. After the rollout run ClearCache per tenant;
externally built adapters are re-released if their pinned range moves.

## 4.12.0 - 2026-10-25

_Required bump level: minor._

### Added

- Attribute 'SealedCaller' (String, optional, isRuntimeState) on 'PipelineExecution' _(minor — additive optional attribute)_

The invoker of a queued work item (principal, trust level, delegation token) is carried as one sealed envelope bound to
the tenant and the execution (AB#7472). The seven plain `Caller*` attributes of `PipelineExecution` stay in the model
but are deprecated: the controller no longer writes or reads them. Existing dependents keep resolving. The same
envelope is the provenance of the work item (AB#7473): the controller seals every execution it queues, also without a
caller, and an execution queued before the upgrade (or written through a generic surface) is failed at lease grant
instead of leased.

## 4.11.0 - 2026-10-24

_Required bump level: minor._

### Changed

- Attribute 'ExternalId-1': isRuntimeState changed 'true' → 'false' _(minor — blueprint re-apply behavior changes)_
- Attribute 'ExternalId-1': ownership changed 'RuntimeState' → 'SeedOwned' _(minor — blueprint re-apply and export behavior change)_

`DataPoint.ExternalId` is exported again (AB#7446): the data point identity of seed-owned RecordArrays such as
`Loxone/Control.States` was dropped by `ExportRtByDeepGraph` / `ExportRtByQuery`. `CurrentValue`, `LastUpdate` and
`SourceIdentifier` stay runtime state. Dependents with range-retained dependencies keep resolving; no migration.
