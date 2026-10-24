# Changelog

## 4.11.0 - 2026-10-24

_Required bump level: minor._

### Changed

- Attribute 'ExternalId-1': isRuntimeState changed 'true' → 'false' _(minor — blueprint re-apply behavior changes)_
- Attribute 'ExternalId-1': ownership changed 'RuntimeState' → 'SeedOwned' _(minor — blueprint re-apply and export behavior change)_

`DataPoint.ExternalId` is exported again (AB#7446): the data point identity of seed-owned RecordArrays such as
`Loxone/Control.States` was dropped by `ExportRtByDeepGraph` / `ExportRtByQuery`. `CurrentValue`, `LastUpdate` and
`SourceIdentifier` stay runtime state. Dependents with range-retained dependencies keep resolving; no migration.
