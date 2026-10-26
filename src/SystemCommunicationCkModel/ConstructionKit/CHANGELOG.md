# Changelog

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
