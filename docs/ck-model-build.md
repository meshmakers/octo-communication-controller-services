---
description: Building the System.Communication Construction Kit model and its generated types
applies_to: src/SystemCommunicationCkModel/ConstructionKit/**
---

### Construction Kit Model

The `SystemCommunicationCkModel` project contains YAML model definitions that generate C# types:

```bash
# Build the CK model (generates code and publishes NuGet package)
dotnet build src/SystemCommunicationCkModel/SystemCommunicationCkModel.csproj

# The generated types are in src/SystemCommunicationCkModel/Generated/
```

Model definitions are in `src/SystemCommunicationCkModel/ConstructionKit/`:
- `ckModel.yaml` - main model definition
- `types/` - entity type definitions
- `associations/` - relationship definitions
- `enums/` - enumeration definitions
- `attributes/` - attribute definitions
- `records/` - record definitions

#### Platform secret defaults (System.Communication 4.15.0, AB#7243)

System.Communication 4.15.0 defines the model contract (record, attribute, type). The projection into tenants is the controller's job (AB#7244, AB#7245) and is not part of this model version; see "Secret defaults (AB#7219)" below.

| Element | Kind | Generated class / member | Rule |
|---------|------|--------------------------|------|
| `SecretDefaultProjection` | record (final, key `Key`) | `RtSecretDefaultProjectionRecord`: `Key`, `SecretValue` (Secret), `AllowedTenantIds` (optional), `Description` (optional) | No scope field: the target set is the Application's own tenant plus its whole subtree; `AllowedTenantIds` only narrows |
| `SecretDefaults` | optional attribute (RecordArray) on `Application` only | `RtApplication.SecretDefaults` | Not a `ValueOverride`: never reaches Helm values, the pod or the app's Kubernetes Secret |
| `SecretDefault` | type (final), derived from `System/Entity`, not from a Configuration type | `RtSecretDefault`: `Name`, `Value` (Secret), `SourceTenantId`, `SourceWorkloadRtId`, `LastProjectedAt` | Provisioned by the controller, never author-set; `Value` is platform-only (no tenant pipeline, user or role may reveal it; `access: Hidden` since 4.16.0, see below); all attributes except `Value` are `RuntimeState`; `Value`, `SourceTenantId` and `SourceWorkloadRtId` are optional (the controller always sets them, but export drops Secret and RuntimeState values, so a required one would make an exported entity unimportable). Because it is not a Configuration type the controller never ships it to adapters as pipeline configuration |

A Secret attribute takes ownership `Secret` and nothing else (`SecretAttributeValidator` in the engine). Model tests:
`tests/CommunicationControllerService.Tests/Services/SecretDefaultModelTests.cs`.

#### System.Communication on ckLanguage 2 (4.16.0, AB#8594)

System.Communication 4.16.0 is the first `ckLanguage: 2` release of the model, a **Minor** against 4.15.0 (Feature AB#5697
F4.3, Epic AB#5584; rules: `octo-construction-kit-engine/docs/ck-semver-rules.md`). What a later change must keep in mind:

- **`derivable: Any` on every type and record.** ckLanguage 2 flips the default to `Model`. A pre-existing element without the
  explicit key is a MAJOR (row T6) and breaks every model that derives from it (message 113: Loxone, System.Ai, adapters). A
  **new** type or record defaults to `Model`; declare `derivable: Any` when another model should derive from it.
- **System interfaces** (System floor `[2.7,3.0)`): `DataFlow` and `LentAdapterPool` implement `Named-1` + `Describable-1`,
  `DeployableEntity` (and so every pipeline, trigger, adapter and workload) `Describable-1`, `PipelineTrigger` and
  `DataPointMapping` `Enableable-1`. An implementing type must keep the member required where the interface requires it
  (`Name`, `Enabled`; message 97) and under the interface's name (message 98); removing an interface is a MAJOR (row T3).
  Not implemented: `Pipeline` (`Enabled` optional), `Named-1` on types with an optional `Name`, `SecretDefault`, `Hierarchical-1`.
- **Access.** `SecretDefault.Value` is `access: Hidden` and its definition `SecretDefaultValue` is `securitySensitive: true`
  (row T7a, acknowledged in 4.16.0). GraphQL, REST, MCP, import (message 139) and export cannot read or write it. The
  controller writes and reveals it through `CommunicationRepository` (`UpsertSecretDefaultAsync`, repository reads), which the
  access rules do not guard. Every other attribute is `ReadWrite`: Studio writes the configuration credentials through the
  generic GraphQL update, and a `ReadOnly`/`MethodOnly` on an attribute that is not `securitySensitive` in every published
  4.x version is a MAJOR (row T7). The T7a exception is used up: a further tightening in 4.x is a MAJOR. The method stories
  (Adapter/Pipeline/Pool methods) can tighten credentials only together with a method that writes them, in 5.0.
- **Engine.** The model itself needs `minEngineVersion` 3.5.1, but the compiler stamps the maximum over the model and its
  dependencies: since System.Bot's async `Dump` method (AB#8445) the compiled model carries 3.5.67 (AB#8773); the model is published under `ck-models/v3/` only. Keep it there:
  an explicit `authorization.dataAccess` on any method raises it to 3.5.40 (`CkEngineVersion.DataAccessMinEngineVersion`), a
  `sensitive` result to 3.5.36, `execution.mode` to 3.5.67. The methods of 4.17.0 therefore leave `dataAccess` at its default
  (`Write`) instead of spelling it out.
- **Acknowledgements live for one release - of the published baseline.** The compile gate diffs against the newest
  **published** version (CI: the private GitHub catalog). A main merge publishes (`publishSystemCkModels`, default true since AB#8718; before that only a manual
  main CI did): 4.16.0 was published on 2026-11-09 with the `SecretDefault.Value` acknowledgement, so 4.17.0 diffs against
  4.16.0 and carries no acknowledgement (keeping it failed main CI with `OCTO-CK204`, AB#8595). Check the private catalog
  (Pages mirror `ck-models/v3/s/System.Communication/4/catalog.json`) before you keep or drop an entry. Locally the gate diffs
  against the lane's `local-catalog`; a local build of an unpublished version there gives a false `OCTO-CK204` - remove that
  version from the local catalog instead of the acknowledgement.
- Tests: `tests/CommunicationControllerService.Tests/CkModels/SystemCommunicationCkLanguage2Tests.cs` (embedded model: version,
  ckLanguage, minEngineVersion, derivable, interfaces, the only tightened attribute, no acknowledgement in 4.17.0) and
  `tests/CommunicationControllerServices.IntegrationTests/Repository/SystemCommunicationCkLanguage2PersistenceTests.cs`
  (re-import, the CK v2 keys survive the MongoDB round trip into the CK cache).

#### Publishing the CK model: automatic from main (AB#7432, default true since AB#8718)

A merge to `main` publishes `System.Communication` to the private CK catalog automatically (AB#8718, decision 2026-11-10). The pipeline
parameter `publishSystemCkModels` (boolean, **default true**; gate introduced as default false by AB#7343/AB#7432/AB#7433) feeds
`publishOnMain` of `validate-and-publish-ck-versions.yml` (`tpl-v1.8.0` and later; pinned: `tpl-v1.11.0`). ValidateVersion, the schema compile and the cascade check
run on every build and refuse a breaking change declared as minor; an already-published version is a no-op. Default true because CK v2
range retention (AB#4696) and the completed re-pin (AB#5688, AB#6708) removed the exact-pin breakage that motivated default false.
catalog from an engine merge before the re-pin gate).

**To build main without publishing:** Azure DevOps -> pipeline `octo-communication-controller-services-CI` -> *Run pipeline* -> branch `main` -> untick
*Publish System CK models to the CK catalogs (main)*; CLI: `az pipelines run --id 101 --branch main --parameters publishSystemCkModels=false`.
`r*` tags (release train) publish as before; `dev/*`/`test/*` builds never publish to the shared catalogs.
