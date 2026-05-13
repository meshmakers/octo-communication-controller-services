# Octo Communication Controller Services - Developer Guide

This document provides comprehensive technical documentation for developers integrating with or extending the Octo Communication Controller Services.

## Table of Contents

- [Introduction](#introduction)
- [Architecture Overview](#architecture-overview)
- [Authentication & Authorization](#authentication--authorization)
- [REST API Reference](#rest-api-reference)
- [SignalR Hubs](#signalr-hubs)
- [Data Types & Identifiers](#data-types--identifiers)
- [State Management](#state-management)
- [Configuration](#configuration)
- [Integration Examples](#integration-examples)
- [Error Handling](#error-handling)
- [Troubleshooting](#troubleshooting)
- [Concept Documents](#concept-documents)

---

## Introduction

The Communication Controller Service is a central coordination hub for managing communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant Octo Mesh environment.

### Key Responsibilities

- **Adapter Management**: Registration, configuration deployment, and lifecycle management of communication adapters
- **Pool Management**: Organization and management of device groups via pool operators
- **Pipeline Orchestration**: Deployment, execution, and debugging of data pipelines
- **Real-time Communication**: Bidirectional SignalR connections for live configuration updates
- **Multi-Tenancy**: Complete tenant isolation at all API and data levels

### Service Endpoints

| Protocol | Endpoint Pattern | Description |
|----------|------------------|-------------|
| HTTPS | `https://{host}/system/v1/*` | System-level API |
| HTTPS | `https://{host}/{tenantId}/v1/*` | Tenant-level REST API |
| WSS | `wss://{host}/{tenantId}/adapterHub` | Adapter SignalR Hub |
| WSS | `wss://{host}/{tenantId}/poolHub` | Pool Operator SignalR Hub |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           External Clients                               │
│                                                                          │
│    ┌──────────────────┐    ┌──────────────────┐    ┌──────────────────┐ │
│    │   Edge Adapter   │    │   Mesh Adapter   │    │  Pool Operator   │ │
│    └────────┬─────────┘    └────────┬─────────┘    └────────┬─────────┘ │
└─────────────┼──────────────────────┼──────────────────────┼─────────────┘
              │                       │                       │
              │ SignalR (WSS)         │ SignalR (WSS)         │ SignalR (WSS)
              │                       │                       │
              ▼                       ▼                       ▼
┌─────────────────────────────────────────────────────────────────────────┐
│                   Communication Controller Service                       │
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐ │
│  │                         SignalR Hubs                                │ │
│  │                                                                      │ │
│  │   ┌─────────────────────────┐      ┌─────────────────────────┐     │ │
│  │   │      AdapterHub         │      │        PoolHub          │     │ │
│  │   │  /{tenantId}/adapterHub │      │   /{tenantId}/poolHub   │     │ │
│  │   └───────────┬─────────────┘      └───────────┬─────────────┘     │ │
│  └───────────────┼────────────────────────────────┼───────────────────┘ │
│                  │                                │                      │
│  ┌───────────────┴────────────────────────────────┴───────────────────┐ │
│  │                         Service Layer                               │ │
│  │                                                                      │ │
│  │  ┌────────────────┐  ┌────────────────┐  ┌────────────────────────┐ │ │
│  │  │ AdapterService │  │  PoolService   │  │ TriggerManagementSvc   │ │ │
│  │  └────────────────┘  └────────────────┘  └────────────────────────┘ │ │
│  │  ┌────────────────┐  ┌────────────────────────────────────────────┐ │ │
│  │  │PipelineDebugSvc│  │      CommunicationEventService             │ │ │
│  │  └────────────────┘  └────────────────────────────────────────────┘ │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
│                                    │                                      │
│  ┌─────────────────────────────────┴────────────────────────────────────┐ │
│  │                          Cache Layer                                  │ │
│  │     ┌────────────────────┐          ┌────────────────────┐           │ │
│  │     │    AdapterCache    │          │     PoolCache      │           │ │
│  │     └────────────────────┘          └────────────────────┘           │ │
│  └──────────────────────────────────────────────────────────────────────┘ │
│                                    │                                      │
│  ┌─────────────────────────────────┴────────────────────────────────────┐ │
│  │                       Repository Layer                                │ │
│  │                ┌─────────────────────────────┐                        │ │
│  │                │  CommunicationRepository    │                        │ │
│  │                └──────────────┬──────────────┘                        │ │
│  └───────────────────────────────┼──────────────────────────────────────┘ │
└──────────────────────────────────┼──────────────────────────────────────┘
                                   │
                                   ▼
                         ┌─────────────────┐
                         │    MongoDB      │
                         │ (Octo Runtime)  │
                         └─────────────────┘
```

### Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| **AdapterHub** | SignalR endpoint for adapter connections, registration, and configuration updates |
| **PoolHub** | SignalR endpoint for pool operator (in-cluster broker proxy) connections |
| **OperatorHub** | SignalR endpoint for the central Communication Operator — fans out `WorkloadDeployedAsync` / `WorkloadUndeployedAsync` for the Helm-based deploy flow |
| **AdapterService** | Business logic for adapter lifecycle, pipeline deployment, and state management |
| **PoolService** | Business logic for pool management and Helm-based workload fan-out |
| **TriggerManagementService** | Scheduled and manual pipeline execution triggers |
| **PipelineDebugService** | Caching and retrieval of pipeline debug information |
| **AdapterCache / PoolCache** | In-memory state management, synchronized across service instances |
| **CommunicationRepository** | Data access abstraction via Octo Runtime Engine |

---

## Authentication & Authorization

### Authentication

All API endpoints (except health check) require JWT Bearer authentication:

```http
Authorization: Bearer <jwt_token>
```

Tokens must be obtained from the configured OAuth2/OIDC authority (Central Authorization Services - CAS).

### Authorization Policies

| Policy | Required Scope | Description |
|--------|----------------|-------------|
| `SystemCommunicationApiPolicy` | `communication:system` | System-level operations (enable/disable tenants) |
| `TenantCommunicationApiReadWritePolicy` | `communication:tenant` | Full access to tenant resources |
| `TenantCommunicationApiReadOnlyPolicy` | `communication:tenant:read` | Read-only access to tenant resources |

### SignalR Authentication

SignalR hubs require the same JWT authentication. Pass the token via query string or headers:

```javascript
// JavaScript/TypeScript example
const connection = new signalR.HubConnectionBuilder()
    .withUrl(`https://{host}/{tenantId}/adapterHub`, {
        accessTokenFactory: () => getAccessToken()
    })
    .build();
```

For adapter connections, additional headers are required:

| Header | Description | Example |
|--------|-------------|---------|
| `adapter-rtId` | MongoDB ObjectId of the adapter | `6789a00000000000000090a2` |
| `adapter-ckTypeId` | Construction Kit Type ID | `System.Communication/Adapter` |

For pool operator connections:

| Header | Description | Example |
|--------|-------------|---------|
| `pool-name` | Well-known name of the pool | `production-pool` |

---

## REST API Reference

### Base URLs

- **System API**: `https://{host}/system/v1/`
- **Tenant API**: `https://{host}/{tenantId}/v1/`

### System API

#### Health Check

```http
GET /system/v1/communication/ping
```

**Authorization**: None (anonymous)

**Response**: `200 OK` with body `"Pong"`

---

#### Enable Tenant

Enables the communication controller for a specific tenant.

```http
POST /system/v1/communication/enable?tenantId={tenantId}
```

**Authorization**: `SystemCommunicationApiPolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `tenantId` | string | query | Yes | The tenant identifier |

**Responses**:
| Status | Description |
|--------|-------------|
| `204 No Content` | Tenant enabled successfully |
| `400 Bad Request` | Invalid tenant or already enabled |

---

#### Disable Tenant

Disables the communication controller for a specific tenant.

```http
POST /system/v1/communication/disable?tenantId={tenantId}
```

**Authorization**: `SystemCommunicationApiPolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `tenantId` | string | query | Yes | The tenant identifier |

**Responses**:
| Status | Description |
|--------|-------------|
| `204 No Content` | Tenant disabled successfully |
| `400 Bad Request` | Invalid tenant or not enabled |

---

### Tenant API - Adapter Endpoints

#### List All Adapters

```http
GET /{tenantId}/v1/adapter
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
[
  {
    "rtId": "6789a00000000000000090a2",
    "ckTypeId": "System.Communication/Adapter",
    "communicationState": "Online",
    "configurationState": "Configured",
    "configuration": "..."
  }
]
```

---

#### Get Adapter Configuration

```http
GET /{tenantId}/v1/adapter/{adapterRtEntityId}?adapterRtEntityId={adapterRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `adapterRtEntityId` | RtEntityId | query | Yes | Full adapter entity ID (e.g., `System.Communication/Adapter@6789a00000000000000090a2`) |

**Response**: `200 OK`
```json
{
  "adapterRtEntityId": "System.Communication/Adapter@6789a00000000000000090a2",
  "configuration": "...",
  "pipelines": [
    {
      "dataFlowRtId": "6789a00000000000000090b1",
      "pipelineRtEntityId": "System.Communication/Pipeline@6789a00000000000000090b2",
      "isDebuggingEnabled": false,
      "pipelineDefinition": "...",
      "configurations": []
    }
  ]
}
```

---

#### Deploy Adapter Configuration

Triggers deployment of the current configuration to a connected adapter.

```http
POST /{tenantId}/v1/adapter/deployUpdate?adapterRtEntityId={adapterRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `adapterRtEntityId` | string | query | Yes | Full adapter entity ID |

**Responses**:
| Status | Description |
|--------|-------------|
| `204 No Content` | Deployment initiated successfully |
| `400 Bad Request` | Adapter not connected or deployment failed |
| `404 Not Found` | Adapter not found or tenant not enabled |

---

### Tenant API - Data Pipeline Endpoints

#### Deploy Data Pipeline

Deploys a data pipeline to all associated adapters.

```http
POST /{tenantId}/v1/datapipeline/deploy?dataFlowRtId={dataFlowRtId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `dataFlowRtId` | OctoObjectId | query | Yes | MongoDB ObjectId of the data pipeline |

**Responses**:
| Status | Description |
|--------|-------------|
| `204 No Content` | Pipeline deployed successfully |
| `404 Not Found` | Pipeline or adapter not found |
| `422 Unprocessable Entity` | Deployment to adapter failed |

---

#### Undeploy Data Pipeline

Removes a data pipeline from all associated adapters.

```http
POST /{tenantId}/v1/datapipeline/undeploy?dataFlowRtId={dataFlowRtId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `dataFlowRtId` | OctoObjectId | query | Yes | MongoDB ObjectId of the data pipeline |

**Responses**:
| Status | Description |
|--------|-------------|
| `204 No Content` | Pipeline undeployed successfully |
| `404 Not Found` | Pipeline or adapter not found |

---

### Tenant API - Pipeline Endpoints

#### Get Pipeline Deployment Status

```http
GET /{tenantId}/v1/pipeline/status?pipelineRtEntityId={pipelineRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `pipelineRtEntityId` | RtEntityId | query | Yes | Full pipeline entity ID |

**Response**: `200 OK`
```json
{
  "rtEntityId": "System.Communication/Pipeline@6789a00000000000000090b2",
  "state": "Success",
  "statusMessage": null
}
```

**Deployment States**:
| State | Description |
|-------|-------------|
| `Success` | Pipeline is deployed and running |
| `Processing` | Deployment is pending or in progress |
| `Failed` | Deployment failed with errors |

---

#### Deploy Pipeline with Custom Definition

Deploys a pipeline with an optional custom definition (for debugging).

```http
POST /{tenantId}/v1/pipeline/deploy?adapterRtEntityId={adapterRtEntityId}&pipelineRtEntityId={pipelineRtEntityId}
Content-Type: text/plain

{pipeline_definition_yaml}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `adapterRtEntityId` | string | query | Yes | Target adapter entity ID |
| `pipelineRtEntityId` | string | query | Yes | Pipeline entity ID |

**Body**: Optional YAML pipeline definition (for debugging/override)

---

#### Execute Pipeline

Manually triggers a pipeline execution.

```http
POST /{tenantId}/v1/pipeline/execute?dataFlowRtId={dataFlowRtId}
Content-Type: text/plain

{optional_input_json}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Parameters**:
| Name | Type | Location | Required | Description |
|------|------|----------|----------|-------------|
| `dataFlowRtId` | OctoObjectId | query | Yes | Data pipeline MongoDB ObjectId |

**Body**: Optional JSON input for the pipeline

**Response**: `200 OK`
```json
"3fa85f64-5717-4562-b3fc-2c963f66afa6"
```

Returns a GUID representing the pipeline execution ID for tracking/debugging.

---

### Tenant API - Pipeline Debug Endpoints

#### Get Pipeline Executions

```http
GET /{tenantId}/v1/pipelinedebug/{pipelineRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
[
  "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "4fa85f64-5717-4562-b3fc-2c963f66afa7"
]
```

---

#### Get Latest Pipeline Execution

```http
GET /{tenantId}/v1/pipelinedebug/{pipelineRtEntityId}/latest
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
"3fa85f64-5717-4562-b3fc-2c963f66afa6"
```

---

#### Get Debug Point Nodes

```http
GET /{tenantId}/v1/pipelinedebug/{pipelineRtEntityId}/{pipelineExecutionId}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
[
  "node1",
  "node2",
  "transform/mapper"
]
```

---

#### Get Debug Point Data

```http
GET /{tenantId}/v1/pipelinedebug/{pipelineRtEntityId}/{pipelineExecutionId}/{nodeId}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
{
  "nodeId": "transform/mapper",
  "timestamp": "2026-01-21T10:30:00Z",
  "inputData": {...},
  "outputData": {...}
}
```

---

### Tenant API - Pool Endpoints

#### List All Pools

```http
GET /{tenantId}/v1/pool
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

**Response**: `200 OK`
```json
[
  {
    "rtId": "6789a00000000000000090c1",
    "wellKnownName": "production-pool",
    "communicationState": "Online",
    "deploymentState": "Deployed"
  }
]
```

---

#### Get Pool Configuration

```http
GET /{tenantId}/v1/pool/{poolRtId}?poolRtId={poolRtId}
```

**Authorization**: `TenantCommunicationApiReadOnlyPolicy`

---

#### Deploy All Adapters of Pool

```http
POST /{tenantId}/v1/pool/deployAllAdaptersOfPool?poolRtId={poolRtId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

#### Undeploy All Adapters of Pool

```http
POST /{tenantId}/v1/pool/undeployAllAdaptersOfPool?poolRtId={poolRtId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

#### Deploy Single Adapter to Pool

```http
POST /{tenantId}/v1/pool/deployAdapter?poolRtId={poolRtId}&adapterRtEntityId={adapterRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

#### Undeploy Single Adapter from Pool

```http
POST /{tenantId}/v1/pool/unDeployAdapter?poolRtId={poolRtId}&adapterRtEntityId={adapterRtEntityId}
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

### Tenant API - Trigger Endpoints

#### Deploy Triggers

Activates scheduled triggers for the tenant.

```http
POST /{tenantId}/v1/datapipelinetrigger/deploy
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

#### Undeploy Triggers

Deactivates scheduled triggers for the tenant.

```http
POST /{tenantId}/v1/datapipelinetrigger/undeploy
```

**Authorization**: `TenantCommunicationApiReadWritePolicy`

---

## SignalR Hubs

### AdapterHub

**Endpoint**: `wss://{host}/{tenantId}/adapterHub`

#### Required Headers

| Header | Description |
|--------|-------------|
| `adapter-rtId` | MongoDB ObjectId of the adapter |
| `adapter-ckTypeId` | CK Type ID (e.g., `System.Communication/Adapter`) |

#### Client-to-Server Methods

##### RegisterAdapterAsync

Registers the adapter with the service and retrieves the current configuration.

```typescript
// Signature
registerAdapterAsync(adapterRtEntityId: RtEntityId): Promise<AdapterConfigurationDto>

// Example
const config = await connection.invoke("RegisterAdapterAsync",
    "System.Communication/Adapter@6789a00000000000000090a2");
```

**Returns**: `AdapterConfigurationDto` containing adapter configuration and pipelines.

##### UnRegisterAdapterAsync

Unregisters the adapter from the service.

```typescript
// Signature
unRegisterAdapterAsync(adapterRtEntityId: RtEntityId): Promise<void>
```

##### SendDeploymentUpdateResultAsync

Reports the result of a configuration deployment.

```typescript
// Signature
sendDeploymentUpdateResultAsync(
    adapterRtEntityId: RtEntityId,
    deploymentResult: DeploymentResult
): Promise<void>

// Example
await connection.invoke("SendDeploymentUpdateResultAsync",
    "System.Communication/Adapter@6789a00000000000000090a2",
    {
        isSuccess: true,
        errorMessages: []
    });
```

##### SendDebugDataAsync

Sends debug data from a pipeline execution.

```typescript
// Signature
sendDebugDataAsync(
    pipelineRtEntityId: RtEntityId,
    pipelineExecutionId: Guid,
    debugPoint: DebugPointDto
): Promise<void>
```

#### Server-to-Client Methods

##### AdapterConfigurationUpdatedAsync

Called when a new configuration should be deployed.

```typescript
connection.on("AdapterConfigurationUpdatedAsync",
    (configuration: AdapterConfigurationDto) => {
        // Apply new configuration
        applyConfiguration(configuration);

        // Report result back
        connection.invoke("SendDeploymentUpdateResultAsync",
            configuration.adapterRtEntityId,
            { isSuccess: true, errorMessages: [] });
    });
```

##### PreUpdateTenantAsync

Called before a tenant configuration update. Adapters should prepare for disconnection.

```typescript
connection.on("PreUpdateTenantAsync", () => {
    // Prepare for disconnection
    prepareForUpdate();
});
```

##### ExecutePipelineAsync

Called to trigger a pipeline execution.

```typescript
connection.on("ExecutePipelineAsync",
    (pipelineRtEntityId: RtEntityId, executionId: Guid, input: string) => {
        // Execute pipeline
        executePipeline(pipelineRtEntityId, executionId, input);
    });
```

---

### PoolHub

**Endpoint**: `wss://{host}/{tenantId}/poolHub`

#### Required Headers

| Header | Description |
|--------|-------------|
| `pool-name` | Well-known name of the pool |

#### Client-to-Server Methods

##### RegisterPoolOperatorAsync

Registers a pool operator. Workloads managed by the pool are not returned here — they are delivered as `WorkloadDeployedAsync` events on the `/operatorHub` connection that the central Communication Operator holds open.

```typescript
// Signature
registerPoolOperatorAsync(poolName: string): Promise<void>

// Example
await connection.invoke("RegisterPoolOperatorAsync", "production-pool");
```

##### UnregisterPoolOperatorAsync

Unregisters the pool operator.

```typescript
// Signature
unregisterPoolOperatorAsync(poolName: string): Promise<void>
```

##### UpdateAdapterDeploymentStateAsync

Reports the deployment state of an adapter back to the controller (used by the in-cluster broker proxy).

```typescript
// Signature
updateAdapterDeploymentStateAsync(
    poolName: string,
    adapterRtEntityId: RtEntityId,
    deployed: boolean
): Promise<void>
```

#### Server-to-Client Methods

##### PreUpdateTenantAsync

Called when the tenant is about to be updated (e.g. CK model upgrade). The pool operator should disconnect and retry registration after some time.

```typescript
connection.on("PreUpdateTenantAsync", (tenantId: string) => {
    // disconnect, schedule reconnect
});
```

> **Note:** Adapter / Application deployment is no longer driven by `PoolHub` callbacks. The central Communication Operator subscribes to `WorkloadDeployedAsync` / `WorkloadUndeployedAsync` on `/operatorHub` and runs `helm upgrade --install` / `helm uninstall` per workload.

---

## Data Types & Identifiers

### RtEntityId

A composite identifier consisting of a CK Type ID and a MongoDB ObjectId.

**Format**: `{CkTypeId}@{RtId}`

**Examples**:
- `System.Communication/Adapter@6789a00000000000000090a2`
- `System.Communication/Adapter@6789a00000000000000090a3`
- `System.Communication/Pipeline@6789a00000000000000090b1`

### OctoObjectId

A 24-character hexadecimal MongoDB ObjectId.

**Example**: `6789a00000000000000090a2`

### CK Type IDs

| Type | CK Type ID |
|------|------------|
| Adapter | `System.Communication/Adapter` |
| Pipeline | `System.Communication/Pipeline` |
| Pool | `System.Communication/Pool` |
| Data Flow | `System.Communication/DataFlow` |

### DTOs

#### AdapterConfigurationDto

```typescript
interface AdapterConfigurationDto {
    adapterRtEntityId: RtEntityId;
    configuration: string;
    pipelines: PipelineConfigurationDto[];
}
```

#### PipelineConfigurationDto

```typescript
interface PipelineConfigurationDto {
    dataFlowRtId: OctoObjectId;
    pipelineRtEntityId: RtEntityId;
    isDebuggingEnabled: boolean;
    pipelineDefinition: string;
    configurations: ConfigurationDto[];
}
```

#### DeploymentResult

```typescript
interface DeploymentResult {
    isSuccess: boolean;
    errorMessages?: DeploymentErrorMessage[];
}

interface DeploymentErrorMessage {
    pipelineRtEntityId?: RtEntityId;
    errorMessage: string;
}
```

---

## State Management

### Communication States

| State | Value | Description |
|-------|-------|-------------|
| `Unregistered` | 0 | Adapter/Pool is not registered |
| `Online` | 1 | Connected via SignalR |
| `Offline` | 2 | Disconnected |

### Deployment States

| State | Value | Description |
|-------|-------|-------------|
| `Undeployed` | 0 | Not deployed |
| `Pending` | 1 | Deployment requested, waiting |
| `Deployed` | 2 | Successfully deployed |
| `Error` | 3 | Deployment failed |

### Configuration States

| State | Value | Description |
|-------|-------|-------------|
| `Pending` | 0 | Configuration update pending |
| `Configured` | 1 | Successfully configured |
| `Error` | 2 | Configuration failed |

### State Lifecycle

```
Adapter Connection Flow:
┌──────────────┐     ┌──────────┐     ┌────────────┐     ┌────────────┐
│ Unregistered │────▶│  Online  │────▶│ Registered │────▶│ Configured │
└──────────────┘     └──────────┘     └────────────┘     └────────────┘
       ▲                                                        │
       │                                                        │
       └────────────────────── Disconnect ──────────────────────┘
```

---

## Configuration

### appsettings.json

```json
{
  "CommunicationController": {
    "PublicUrl": "https://localhost:5015",
    "AuthorityUrl": "https://localhost:5003",
    "BrokerHost": "localhost",
    "BrokerPort": 5672,
    "BrokerVirtualHost": "/",
    "BrokerUser": "guest",
    "BrokerPassword": "guest",
    "MinLogLevel": "Information",
    "InstancePrefix": ""
  },
  "System": {
    "MongoDbConnectionString": "mongodb://localhost:27017",
    "DatabaseName": "OctoMesh"
  }
}
```

### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `PublicUrl` | string | `https://localhost:5015` | Public base URL of the service |
| `AuthorityUrl` | string | `https://localhost:5003` | OAuth2/OIDC authority URL |
| `BrokerHost` | string | `localhost` | RabbitMQ host |
| `BrokerPort` | ushort | `5672` | RabbitMQ port |
| `BrokerVirtualHost` | string | `/` | RabbitMQ virtual host |
| `BrokerUser` | string | `guest` | RabbitMQ username |
| `BrokerPassword` | string | `guest` | RabbitMQ password |
| `MinLogLevel` | LogLevel | `Warn` (Release) / `Trace` (Debug) | Minimum log level |
| `InstancePrefix` | string | `""` | Multi-instance deployment prefix |

### Environment Variables

All settings can be overridden via environment variables with the `OCTO_` prefix:

```bash
export OCTO_CommunicationController__PublicUrl="https://api.example.com"
export OCTO_CommunicationController__BrokerHost="rabbitmq.example.com"
```

---

## Integration Examples

### C# Adapter Client

```csharp
using Microsoft.AspNetCore.SignalR.Client;

public class AdapterClient
{
    private readonly HubConnection _connection;
    private readonly RtEntityId _adapterRtEntityId;

    public AdapterClient(string serviceUrl, string tenantId,
        string adapterRtId, string adapterCkTypeId, Func<Task<string>> tokenProvider)
    {
        _adapterRtEntityId = new RtEntityId(adapterCkTypeId, adapterRtId);

        _connection = new HubConnectionBuilder()
            .WithUrl($"{serviceUrl}/{tenantId}/adapterHub", options =>
            {
                options.AccessTokenProvider = tokenProvider;
                options.Headers.Add("adapter-rtId", adapterRtId);
                options.Headers.Add("adapter-ckTypeId", adapterCkTypeId);
            })
            .WithAutomaticReconnect()
            .Build();

        // Register server-to-client handlers
        _connection.On<AdapterConfigurationDto>("AdapterConfigurationUpdatedAsync",
            OnConfigurationUpdated);
        _connection.On("PreUpdateTenantAsync", OnPreUpdateTenant);
        _connection.On<RtEntityId, Guid, string>("ExecutePipelineAsync",
            OnExecutePipeline);
    }

    public async Task ConnectAsync()
    {
        await _connection.StartAsync();

        // Register adapter
        var configuration = await _connection.InvokeAsync<AdapterConfigurationDto>(
            "RegisterAdapterAsync", _adapterRtEntityId);

        await ApplyConfigurationAsync(configuration);
    }

    private async Task OnConfigurationUpdated(AdapterConfigurationDto configuration)
    {
        try
        {
            await ApplyConfigurationAsync(configuration);

            await _connection.InvokeAsync("SendDeploymentUpdateResultAsync",
                _adapterRtEntityId,
                new DeploymentResult { IsSuccess = true });
        }
        catch (Exception ex)
        {
            await _connection.InvokeAsync("SendDeploymentUpdateResultAsync",
                _adapterRtEntityId,
                new DeploymentResult
                {
                    IsSuccess = false,
                    ErrorMessages = new[]
                    {
                        new DeploymentErrorMessage { ErrorMessage = ex.Message }
                    }
                });
        }
    }

    private Task ApplyConfigurationAsync(AdapterConfigurationDto configuration)
    {
        // Apply pipeline configurations
        foreach (var pipeline in configuration.Pipelines)
        {
            // Load and configure pipeline
        }
        return Task.CompletedTask;
    }

    private Task OnPreUpdateTenant()
    {
        // Prepare for tenant update (stop pipelines, etc.)
        return Task.CompletedTask;
    }

    private Task OnExecutePipeline(RtEntityId pipelineRtEntityId,
        Guid executionId, string input)
    {
        // Execute pipeline and send debug data
        return Task.CompletedTask;
    }
}
```

### TypeScript/JavaScript Client

```typescript
import * as signalR from '@microsoft/signalr';

interface AdapterConfigurationDto {
    adapterRtEntityId: string;
    configuration: string;
    pipelines: PipelineConfigurationDto[];
}

interface PipelineConfigurationDto {
    dataFlowRtId: string;
    pipelineRtEntityId: string;
    isDebuggingEnabled: boolean;
    pipelineDefinition: string;
    configurations: any[];
}

class AdapterClient {
    private connection: signalR.HubConnection;
    private adapterRtEntityId: string;

    constructor(
        serviceUrl: string,
        tenantId: string,
        adapterRtId: string,
        adapterCkTypeId: string,
        tokenProvider: () => Promise<string>
    ) {
        this.adapterRtEntityId = `${adapterCkTypeId}@${adapterRtId}`;

        this.connection = new signalR.HubConnectionBuilder()
            .withUrl(`${serviceUrl}/${tenantId}/adapterHub`, {
                accessTokenFactory: tokenProvider,
                headers: {
                    'adapter-rtId': adapterRtId,
                    'adapter-ckTypeId': adapterCkTypeId
                }
            })
            .withAutomaticReconnect()
            .build();

        this.setupHandlers();
    }

    private setupHandlers(): void {
        this.connection.on('AdapterConfigurationUpdatedAsync',
            (config: AdapterConfigurationDto) => this.onConfigurationUpdated(config));

        this.connection.on('PreUpdateTenantAsync',
            () => this.onPreUpdateTenant());

        this.connection.on('ExecutePipelineAsync',
            (pipelineId: string, executionId: string, input: string) =>
                this.onExecutePipeline(pipelineId, executionId, input));
    }

    async connect(): Promise<void> {
        await this.connection.start();

        const configuration = await this.connection.invoke<AdapterConfigurationDto>(
            'RegisterAdapterAsync',
            this.adapterRtEntityId
        );

        await this.applyConfiguration(configuration);
    }

    private async onConfigurationUpdated(config: AdapterConfigurationDto): Promise<void> {
        try {
            await this.applyConfiguration(config);

            await this.connection.invoke('SendDeploymentUpdateResultAsync',
                this.adapterRtEntityId,
                { isSuccess: true, errorMessages: [] });
        } catch (error) {
            await this.connection.invoke('SendDeploymentUpdateResultAsync',
                this.adapterRtEntityId,
                {
                    isSuccess: false,
                    errorMessages: [{ errorMessage: String(error) }]
                });
        }
    }

    private async applyConfiguration(config: AdapterConfigurationDto): Promise<void> {
        console.log('Applying configuration:', config);
        // Implement configuration application logic
    }

    private onPreUpdateTenant(): void {
        console.log('Preparing for tenant update');
        // Implement pre-update logic
    }

    private onExecutePipeline(
        pipelineId: string,
        executionId: string,
        input: string
    ): void {
        console.log('Executing pipeline:', pipelineId, executionId);
        // Implement pipeline execution logic
    }

    async disconnect(): Promise<void> {
        await this.connection.invoke('UnRegisterAdapterAsync', this.adapterRtEntityId);
        await this.connection.stop();
    }
}
```

---

## Error Handling

### HTTP Error Responses

All API errors return a consistent error response format:

```json
{
    "errorMessage": "Description of the error"
}
```

### Common Error Codes

| Status | Meaning | Common Causes |
|--------|---------|---------------|
| `400 Bad Request` | Invalid request | Missing parameters, invalid format |
| `401 Unauthorized` | Authentication failed | Missing or invalid token |
| `403 Forbidden` | Authorization failed | Insufficient scopes |
| `404 Not Found` | Resource not found | Invalid tenant, adapter, or pipeline ID |
| `422 Unprocessable Entity` | Business logic error | Adapter not connected, deployment failed |
| `500 Internal Server Error` | Server error | Unexpected exception |

### SignalR Error Handling

```typescript
connection.on('close', (error) => {
    console.error('Connection closed:', error);
    // Implement reconnection logic
});

connection.on('reconnecting', (error) => {
    console.warn('Reconnecting:', error);
});

connection.on('reconnected', (connectionId) => {
    console.log('Reconnected:', connectionId);
    // Re-register adapter after reconnection
});
```

---

## Troubleshooting

### Common Issues

#### "Adapter not loaded" Error

**Symptoms**: HTTP 400/500 when calling deploy endpoints

**Causes**:
1. Adapter is not connected via SignalR
2. Adapter connected with wrong `RtEntityId` (different CK Type ID)
3. Tenant cache was cleared but adapter didn't reconnect

**Solution**:
- Verify adapter is connected (check logs for "adapter online" message)
- Ensure `adapter-rtId` and `adapter-ckTypeId` headers match the database entry
- Check that the full `RtEntityId` matches (e.g., `Adapter@...`)

#### "Tenant not enabled" Error

**Symptoms**: All API calls fail with 404

**Solution**:
1. Call `POST /system/v1/communication/enable?tenantId={tenantId}` with system scope
2. Verify tenant exists in the Octo Mesh database

#### SignalR Connection Fails

**Symptoms**: WebSocket connection refused or times out

**Checklist**:
1. Verify JWT token is valid and not expired
2. Check required headers (`adapter-rtId`, `adapter-ckTypeId` or `pool-name`)
3. Verify tenant ID in URL is correct
4. Check network/firewall allows WebSocket connections

#### Pipeline Deployment Stuck in "Pending"

**Symptoms**: Pipeline status remains "Processing"

**Causes**:
1. Adapter is not connected
2. Adapter hasn't sent `SendDeploymentUpdateResultAsync`
3. Adapter rejected configuration

**Solution**:
- Check adapter logs for configuration application errors
- Verify adapter sends deployment result after receiving configuration

### Logging

The service uses NLog with structured logging. Key log sources:

| Logger | Information |
|--------|-------------|
| `AdapterService` | Adapter lifecycle events |
| `PoolService` | Pool operator events |
| `AdapterHub` | SignalR connection events |
| `PoolHub` | Pool SignalR events |
| `CommunicationRepository` | Database operations |

Enable trace logging for detailed diagnostics:

```json
{
  "CommunicationController": {
    "MinLogLevel": "Trace"
  }
}
```

---

## Version History

| Version | Date | Changes |
|---------|------|---------|
| 1.0 | 2026-01 | Initial release |

---

## Concept Documents

- [Data Point Mapping](concepts/DataPointMapping.md) — Architecture for mapping external data points to OctoMesh entities. Covers `DataPointMapping`, `DataPoint`, `MappingTarget` types/records, pipeline nodes (`BuildMappingTargets`, `ApplyDataPointMappings`, `MapToRecordArray`, `DeployPipeline`), expression support, and the end-to-end data flow.
- [Pipeline Execution Metrics](concepts/PipelineExecutionMetrics.md) — Pipeline execution tracking and statistics.
- [Testing Strategy](concepts/TestingStrategy.md) — Unit and integration testing approach.

---

## Support

For issues or questions, contact the development team or refer to the internal documentation.
