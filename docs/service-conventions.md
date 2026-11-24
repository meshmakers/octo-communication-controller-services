---
description: "Service conventions: registration, error handling, configuration, exception pattern, caches, HTTP context, API structure"
applies_to: "**/Caches/**, **/Program.cs, **/Services/*ServiceException.cs, **/Repository/CommunicationRepository.cs"
---

### Service Registration

Services expose multiple interfaces (e.g., cache as `IAdapterCache` and `IAdapterCachePublish`). Use extension methods:
- `AddSingletonMultipleInterfaces<TImpl, TInterface1, TInterface2>()`
- `AddScopedMultipleInterfaces<TImpl, TInterface1, TInterface2>()`

### Error Handling

Services throw custom exceptions (e.g., `AdapterServiceException`, `DeploymentSiteServiceException`) with static factory methods for specific error scenarios. Always log errors with NLog before throwing.

### Configuration

Configuration is bound to strongly-typed options classes:
- `OctoSystemConfiguration` from `System` section
- `CommunicationControllerOptions` from `CommunicationController` section
- Environment variables prefixed with `OCTO_` override appsettings

**Pipeline Execution Configuration Options:**
```csharp
// CommunicationControllerOptions
public int PipelineExecutionRetentionDays { get; set; } = 3;     // Orphan hard-cap: unconditional delete (executions whose pipeline is gone)
public int PipelineExecutionRetentionHours { get; set; } = 1;    // hours before terminal executions are folded into buckets + erased
public int StatisticsUpdateIntervalMinutes { get; set; } = 60;   // Statistics aggregation interval
public bool StoreInputData { get; set; } = false;                // Whether to store pipeline input data
public int MaxInputDataLength { get; set; } = 10000;             // Max length of stored input data
public int PipelineExecutionTimeoutHours { get; set; } = 24;     // Legacy connection-unaware timeout (no longer used by the reaper)
public int PipelineExecutionStuckGraceMinutes { get; set; } = 15;         // grace before a stuck execution is failed
public int PipelineExecutionStuckCheckIntervalMinutes { get; set; } = 5;  // reaper cadence
public int WorkloadStateMetricsIntervalMinutes { get; set; } = 5;         // workload-state metrics sweep + startup grace
```

### Deployment State Management

Adapters and Pools track deployment state:
- `RtDeploymentStateEnum.Pending` - not deployed
- `RtDeploymentStateEnum.Deployed` - active
- Communication state tracked separately via `RtCommunicationStateEnum`

### Workload Deployment Progress (live signal)

`IOperatorHub.ReportWorkloadDeploymentProgressAsync(WorkloadDeploymentProgressDto)`
is a second hub channel parallel to `ReportWorkloadDeploymentStatusAsync`.
The operator pushes diagnostic snapshots while `helm upgrade --install` is
still in flight, so the UI surfaces ImagePullBackOff / FailedScheduling /
CrashLoopBackOff within seconds instead of waiting for helm's atomic
timeout (default 5 min) to elapse.

`OperatorHub.ReportWorkloadDeploymentProgressAsync` (mirrors the validation
and entity-routing of `ReportWorkloadDeploymentStatusAsync`) writes
`DeploymentState=Pending` + `StatusMessage=progress.Message` via
`Set{Adapter,Application}DeploymentStateAsync`. **Never writes Deployed
or Error** — those states are the exclusive output of the terminal
`ReportWorkloadDeploymentStatusAsync` path, since helm may still recover
(transient registry outage, image becomes available, etc.). The
`ApplyDeploymentErrorTracking` helper clears `LastDeploymentError` for
`Pending` which is correct: progress is in-flight context belonging on
the live `StatusMessage`, not the persistent error-history pair.

Repository exceptions are swallowed (same contract as
`ReportWorkloadDeploymentStatusAsync`) — progress is best-effort, a
write failure must not break the hub for the rest of the connection's
traffic.


## Exception Handling Pattern

Services use a factory method pattern for exceptions with private constructors and static factory methods:

```csharp
// Wrong - don't throw generic exceptions
throw new AdapterServiceException("Tenant not enabled");

// Correct - use factory methods
throw AdapterServiceException.TenantNotEnabled(tenantId);
```

Each service has a dedicated exception class: `AdapterServiceException`, `DeploymentSiteServiceException`, `PipelineDebugServiceException`, `TriggerManagementServiceException`, `PipelineExecutionServiceException`, `CommunicationRepositoryException`.


## Cache Architecture

Caches use a dual-interface pattern for separation of concerns:
- **Read interface** (e.g., `IAdapterCache`): Query cached state
- **Publish interface** (e.g., `IAdapterCachePublish`): Load and publish configuration changes

Both interfaces are implemented by the same singleton, registered via:
```csharp
services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();
```

## HTTP Context Extensions

Access tenant and adapter info from request context via extension methods in `Constants.cs`:
- `HttpContext.GetTenantId()` - from route value
- `HttpContext.GetPoolName()` - from "pool-name" header
- `HttpContext.GetAdapterRtEntityId()` - constructs `RtEntityId` from "adapter-rtId" and "adapter-ckTypeId" headers

## API Structure

Controllers are split by scope:
- **System API** (`SystemApi/v1/Controllers/`): System-level, non-tenant operations. The
  `CommunicationController` here now only exposes `ping`; its `enable`/`disable` actions were
  **removed** — tenant enable/disable is tenant-scoped only. `DiagnosticsController`
  (system-scoped) still uses `SystemCommunicationApiPolicy`.
- **Tenant API** (`TenantApi/v1/Controllers/`): Tenant-scoped operations for adapters, pools, pipelines, and tenant enable/disable

Routes follow pattern: `{tenantId:tenantId}/v{version:apiVersion}/[controller]`

Tenant enable/disable lives **only** on the Tenant API:
- `{tenantId}/v1/communication/enable` / `.../disable` (tenant read from route)
- The legacy `system/v1/communication/enable?tenantId=X` variant no longer exists.
  The SDK dropped its `system/v1` fallback and now requires a tenant on the client options.

### Service Architecture

The service follows a layered architecture:
- **Hubs Layer** (`src/CommunicationControllerServices/Hubs/`) - SignalR hubs for real-time communication
- **Service Layer** (`src/CommunicationControllerServices/Services/`) - Core business logic (`AdapterService`,
  `DeploymentSiteService`, `PipelineDebugService`, `TriggerManagementService`, `PipelineExecutionService`,
  `CommunicationEventService`)
- **Repository Layer** (`src/CommunicationControllerServices/Repository/`) - Data access via MongoDB Runtime Engine
- **Cache Layer** (`src/CommunicationControllerServices/Caches/`) - In-memory state synchronized across nodes via hub
  callbacks
- **Consumers** (`src/CommunicationControllerServices/Consumers/`) - Message bus event consumers for tenant lifecycle
  management
- **Background Services** (`src/CommunicationControllerServices/BackgroundServices/`) - Periodic maintenance tasks


### Configuration Notes

- `InternalsVisibleTo` configured for tests and DynamicProxy (for NSubstitute proxy generation)
- Target framework: .NET 10.0
- NLog configuration in `src/CommunicationControllerServices/nlog.config`
- SignalR configured with 100MB max message size
- Three build configurations: `Debug`, `Release`, `DebugL` (local development with version 999.0.0)

### Components and project layout

1. **Caches**: In-memory synchronized state for Adapters and Pools across service nodes
2. **Repository Layer**: `CommunicationRepository` - abstracts MongoDB persistence via Octo Runtime Engine
3. **Construction Kit Model**: YAML-based model definitions in `SystemCommunicationCkModel` that generate C# types

#### Project structure notes

- Main service: `src/CommunicationControllerServices/`
- Construction Kit model: `src/SystemCommunicationCkModel/`
- Resource strings: `src/CommunicationControllerServices.Resources/`
- Unit tests (TUnit): `tests/CommunicationControllerService.Tests/`
- Integration tests (xUnit): `tests/CommunicationControllerServices.IntegrationTests/`
- CI/CD: `azure-pipelines.yml` (root; uses shared `octo-pipeline-templates`)
- Output directory: `bin/$(Configuration)/`
