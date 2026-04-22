# Data Point Mapping - Concept & Architecture

## Overview

The Data Point Mapping system enables configurable data flow from external systems (Loxone, MQTT, OPC-UA, Modbus, etc.) into OctoMesh domain model entities. It provides a generic, protocol-independent architecture for mapping source data points to target entity attributes with optional expression-based transformations.

## CK Model Types & Records

### Entity: `System.Communication/DataPointMapping`

A configuration entity that defines a single mapping from a source data point to a target entity attribute.

| Attribute | Type | Description |
|-----------|------|-------------|
| `Name` | String | Display name of the mapping |
| `Enabled` | Boolean | Whether this mapping is active |
| `SourceAttributePath` | String | Name of the source data point (e.g. `tempActual`, `currentValue`). When empty or `currentValue`, the default value of the source entity is used. When set to a specific state name, the corresponding sub-state is polled. |
| `MappingExpression` | String | Optional mXparser expression for value transformation. The variable `value` contains the source value. Supports C-style ternary (`value > 0 ? value : 0`) which is auto-converted to `if()`. |
| `TargetAttributePath` | String | Attribute path on the target entity to write the mapped value to (e.g. `Temperature`, `ChargingPower`). |

**Associations:**
- `MapsFrom` (outbound) → Source entity (e.g. `Loxone/Control`, `MQTT/Topic`)
- `MapsTo` (outbound) → Target entity (e.g. `EnergyIQ/BatteryStorage`, `EnergyIQ/Space`)

### Record: `System.Communication/DataPoint`

A generic data point record stored as a `RecordArray` on source entities. Represents a named data point with an external identifier and optionally its current value.

| Attribute | Type | Description |
|-----------|------|-------------|
| `Name` | String | Logical name of the data point (e.g. `tempActual`, `humidity`, `activeMode`) |
| `ExternalId` | String | External identifier used to acquire this data point's value (e.g. Loxone State UUID, MQTT topic path, OPC-UA NodeId) |
| `CurrentValue` | String | Current value of the data point (updated by polling pipelines) |
| `LastUpdate` | DateTime | Timestamp of the last value update |

**Usage across adapters:**

| Adapter | Source Entity | DataPoint.Name | DataPoint.ExternalId |
|---------|--------------|----------------|---------------------|
| Loxone | `Loxone/Control` (States attribute) | `tempActual`, `humidity` | Loxone State UUID |
| MQTT | `MQTT/Topic` (DataPoints attribute) | `temperature`, `pressure` | MQTT sub-topic path |
| OPC-UA | `OpcUa/Node` (Variables attribute) | `motorSpeed`, `voltage` | OPC-UA Property NodeId |
| Modbus | `Modbus/Slave` (Registers attribute) | `holdingReg40001` | Register address |

### Record: `System.Communication/MappingTarget`

A resolved mapping target produced by the `BuildMappingTargets@1` pipeline node. Stored as a `RecordArray` on configuration entities to tell edge adapters what to poll/subscribe.

| Attribute | Type | Description |
|-----------|------|-------------|
| `SourceIdentifier` | String | Identifier of the source entity in the external system (e.g. Loxone Control UUID). Used by the store pipeline to find the source entity in the DB. |
| `Name` | String | Optional state name (e.g. `tempActual`). When set, indicates this target polls a specific sub-state rather than the default value. Used for routing to the correct DataPointMapping. |
| `ExternalId` | String | External ID to poll/subscribe (e.g. Loxone State UUID). This is what the edge adapter actually queries. |

**Why both `SourceIdentifier` and `ExternalId`?**

These are the same when polling a source entity's default value, but **diverge when polling a specific sub-state**:

```
Example: IRoomControllerV2 with tempActual mapping

  SourceIdentifier: "18a29fbb-0320-b0fb-..."  ← Control UUID (to find the entity in DB)
  ExternalId:       "18a29fbb-031f-b0ce-..."  ← State UUID for tempActual (to poll from Loxone)
  Name:             "tempActual"               ← Routes to the correct DataPointMapping

Without SourceIdentifier: Store pipeline can't find the Control (State UUID ≠ Control UUID)
Without ExternalId: Edge adapter polls the wrong UUID (gets Operating Mode instead of Temperature)
```

For other protocols:
- **MQTT**: `SourceIdentifier` = device ID, `ExternalId` = specific topic path
- **OPC-UA**: `SourceIdentifier` = device NodeId, `ExternalId` = property NodeId
- **Modbus**: `SourceIdentifier` = slave ID, `ExternalId` = register address

### Entity: `System.Communication/ServiceAccountConfiguration`

OAuth2 client credentials for service-to-service authentication. Used by pipeline nodes (e.g. `DeployPipeline@1`) to make authenticated REST API calls.

| Attribute | Type | Description |
|-----------|------|-------------|
| `IssuerUri` | String | OAuth2/OpenID Connect authorization server URL |
| `ClientId` | String | OAuth2 client ID |
| `ClientSecret` | String | OAuth2 client secret |
| `TenantId` | String | Tenant scope for the token (sent as `acr_values`) |

## Pipeline Nodes (MeshAdapter)

### `BuildMappingTargets@1` (Transform)

Resolves all active DataPointMappings into external identifiers for data acquisition. Generic for any adapter type.

**Configuration:**
| Parameter | Description | Example |
|-----------|-------------|---------|
| `sourceCkTypeId` | CK type of source entities | `Loxone/Control` |
| `sourceIdentifierAttribute` | Attribute holding the external ID | `LoxoneUuid` |
| `statesAttribute` | RecordArray attribute with DataPoint records | `States` |
| `stateKeyAttribute` | Name attribute in each DataPoint record | `Name` |
| `stateValueAttribute` | ExternalId attribute in each DataPoint record | `ExternalId` |
| `defaultAttributePath` | The default data point name (no state lookup needed) | `currentValue` |

**Output:** List of `MappingTarget` records written to `targetPath`.

### `ApplyDataPointMappings@1` (Transform)

Evaluates DataPointMappings for a source entity, applies optional expressions, produces update items for target entities.

**Configuration:**
| Parameter | Description |
|-----------|-------------|
| `sourceRtIdPath` | JSON path to source entity RtId |
| `sourceCkTypeIdPath` | JSON path to source entity CkTypeId |
| `sourceValuePath` | JSON path to the polled value |
| `sourceStateNamePath` | Optional: JSON path to incoming state name. Filters mappings by `SourceAttributePath`. |

### `MapToRecordArray@1` (Transform)

Converts a JSON key/value map into a CK RecordArray. Generic utility node.

**Configuration:**
| Parameter | Description | Example |
|-----------|-------------|---------|
| `ckRecordId` | Target record type | `System.Communication/DataPoint` |
| `keyAttributeName` | Record attribute for map key | `Name` |
| `valueAttributeName` | Record attribute for map value | `ExternalId` |

### `DeployPipeline@1` (Load)

Deploys a specific pipeline within the same data flow via the Communication Controller REST API. Acquires an OAuth2 token from a `ServiceAccountConfiguration` entity.

**Safety:**
- Cannot deploy the currently executing pipeline (prevents self-restart loop)
- Target pipeline must belong to the same data flow

## Data Flow Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│  Configure Polling (Mesh Pipeline - manual trigger)                  │
│                                                                      │
│  BuildMappingTargets@1 → reads DataPointMappings                    │
│    → resolves source entities + state lookup                         │
│    → writes MappingTarget records to Configuration                   │
│  DeployPipeline@1 → redeploys edge pipeline with updated config      │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Edge Polling (Edge Pipeline - cyclic trigger)                       │
│                                                                      │
│  Reads MappingTarget records from GlobalConfiguration                │
│  For each target: polls ExternalId → produces                        │
│    {controlUuid (=SourceIdentifier), stateName (=Name), value}       │
│  Sends to Mesh via ToPipelineDataEvent                               │
└──────────────────────────────────────────────────────────────────────┘
         │
         ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Store Control States (Mesh Pipeline - event trigger)                │
│                                                                      │
│  Finds source entity by SourceIdentifier                             │
│  Updates CurrentValue + LastStateUpdate                              │
│  ApplyDataPointMappings@1:                                           │
│    → finds DataPointMappings for this source                         │
│    → filters by stateName if present                                 │
│    → evaluates MappingExpression (mXparser)                          │
│    → writes to target entity attribute                               │
└──────────────────────────────────────────────────────────────────────┘
```

## Expression Support

Mapping expressions use mXparser syntax with the variable `value` containing the source value:

| Expression | Description | Input → Output |
|-----------|-------------|----------------|
| `value * 2` | Scale by factor | `21.5` → `43.0` |
| `if(value < 0, abs(value), 0)` | Absolute value when negative | `-1773` → `1773` |
| `value > 0 ? value : 0` | C-style ternary (auto-converted) | `-500` → `0` |
| `value / 100` | Percentage to fraction | `78` → `0.78` |

Values with units (e.g. `"-1773.0 W"`, `"27.0 %"`) are automatically parsed to extract the numeric part.
