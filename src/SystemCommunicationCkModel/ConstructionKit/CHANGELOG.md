# Changelog

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
