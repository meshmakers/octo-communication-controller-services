# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is the **Octo Communication Controller Services** - an ASP.NET Core web service that manages communication adapters and pools for data ingress and egress in an Octo Mesh instance. The service acts as a central hub for coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant environment.

## Development Standards

**MANDATORY for all code changes:**

1. **Unit Tests Required**: Every new feature, bug fix, or code change must include corresponding unit tests in `tests/CommunicationControllerService.Tests/`. Use TUnit framework with NSubstitute for mocking.

2. **Integration Tests Required**: Changes affecting repository operations, database interactions, or multi-component workflows must include integration tests in `tests/CommunicationControllerServices.IntegrationTests/`. Use xUnit framework with FluentAssertions.

3. **Documentation Updates Required**: This developer documentation (CLAUDE.md) must be updated when:
   - Adding new architectural patterns or components
   - Introducing new services, repositories, or significant classes
   - Changing test infrastructure or fixtures
   - Adding new configuration options
   - Modifying API structures or authentication/authorization

4. **All Tests Must Pass**: Before completing any task, run both unit tests (`dotnet test`) and integration tests to ensure all tests pass.

## Key Architecture Concepts

### Core Components

1. **Adapters**: External clients that execute data pipelines. They connect via SignalR and receive pipeline configurations from the service.
2. **Pools**: Groups of managed devices/entities. Pool operators register via SignalR to handle device communication.
3. **SignalR Hubs**: Real-time bidirectional communication channels:
   - `AdapterHub` at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
   - `PoolHub` at `/{tenantId}/poolHub` - manages pool operator connections
4. **Caches**: In-memory synchronized state for Adapters and Pools across service nodes
5. **Repository Layer**: `CommunicationRepository` - abstracts MongoDB persistence via Octo Runtime Engine
6. **Construction Kit Model**: YAML-based model definitions in `SystemCommunicationCkModel` that generate C# types

### Service Architecture

The service follows a layered architecture:
- **Hubs Layer** (`src/CommunicationControllerServices/Hubs/`) - SignalR hubs for real-time communication
- **Service Layer** (`src/CommunicationControllerServices/Services/`) - Core business logic (`AdapterService`, `PoolService`, `PipelineDebugService`, `TriggerManagementService`, `PipelineExecutionService`, `CommunicationEventService`)
- **Repository Layer** (`src/CommunicationControllerServices/Repository/`) - Data access via MongoDB Runtime Engine
- **Cache Layer** (`src/CommunicationControllerServices/Caches/`) - In-memory state synchronized across nodes via hub callbacks
- **Consumers** (`src/CommunicationControllerServices/Consumers/`) - Message bus event consumers for tenant lifecycle management
- **Background Services** (`src/CommunicationControllerServices/BackgroundServices/`) - Periodic maintenance tasks

### Multi-Tenancy

All operations are tenant-scoped. Routes use a custom `tenantId` constraint. The MongoDB-backed repository supports per-tenant data isolation via `ISystemContext.FindTenantRepositoryAsync(tenantId)`.

### Authentication & Authorization

- JWT Bearer authentication with scope-based authorization
- Three policies:
  - `SystemCommunicationApiPolicy` - full system access
  - `TenantCommunicationApiReadWritePolicy` - tenant full access
  - `TenantCommunicationApiReadOnlyPolicy` - tenant read-only
- Scopes defined in `CommonConstants` from the Contracts library

### External Dependencies

- **Meshmakers.Octo.*** packages - Octo platform libraries for runtime, infrastructure, observability
- Version controlled via `$(OctoVersion)` in `Directory.Build.props`
- MongoDB for persistence (via Octo Runtime Engine MongoDb)
- RabbitMQ for messaging (via Octo Infrastructure DistributionEventHub)

### System Events

The service uses the Octo Notification system to log important business events for auditing and monitoring. Events are stored per-tenant in MongoDB.

**Event Service Architecture:**
- `ICommunicationEventService` / `CommunicationEventService` - Helper service that handles scoped `IEventRepository` access for singleton services
- Events use `RtEventSourcesEnum.CommunicationService` as the source identifier

**Logged Events:**

| Component | Event | Level | Description |
|-----------|-------|-------|-------------|
| Service Startup | Service started | Information | Logged on application start |
| AdapterService | Adapter registered | Information | Adapter connected and registered |
| AdapterService | Adapter unregistered | Information | Adapter disconnected |
| AdapterService | Adapter online/offline | Information | Adapter connection state changed |
| AdapterService | Configuration deployed | Information | Pipeline configuration sent to adapter |
| AdapterService | Configuration failed | Error | Adapter deployment failed with errors |
| AdapterService | Data pipeline deployed/undeployed | Information | Pipeline deployment to adapters |
| AdapterService | Tenant pre/post-update | Information | Tenant lifecycle events |
| PoolService | Pool operator registered/unregistered | Information | Pool operator connection state |
| PoolService | Adapter deployed/undeployed to pool | Information | Adapter assignment to pools |
| PoolService | Tenant pre/post-update | Information | Tenant lifecycle events |
| TriggerManagementService | Pipeline execution started | Information | Manual pipeline trigger |
| TriggerManagementService | Pipeline execution failed | Error | Pipeline execution errors |
| TriggerManagementService | Trigger schedule updated | Information | Scheduled triggers updated |
| PipelineExecutionService | Pipeline execution failed | Error | Adapter reports failed execution |
| PipelineExecutionService | Pipeline execution cancelled | Information | Adapter reports cancelled execution |
| PipelineExecutionService | Old executions cleaned up | Information | Retention cleanup completed |
| TenantManagementConsumer | Tenant update failed | Error | Errors during tenant lifecycle |
| Hubs | Operation failed | Error | Hub operation errors |

**Usage in Services:**
```csharp
// Inject ICommunicationEventService in singleton services
public class MyService(ICommunicationEventService eventService)
{
    public async Task DoSomethingAsync(string tenantId)
    {
        // Store information event
        await eventService.StoreInformationEventAsync(tenantId, "Operation completed.");

        // Store error event
        await eventService.StoreErrorEventAsync(tenantId, $"Operation failed: {error}");
    }
}
```

## Development Commands

### Build & Test

```bash
# Restore packages
dotnet restore Octo.CommunicationController.sln

# Build solution
dotnet build Octo.CommunicationController.sln --configuration Release

# Run all unit tests
dotnet test --configuration Release

# Run a specific test class
dotnet test --filter "FullyQualifiedName~AdapterServiceTests"

# Run a specific test method
dotnet test --filter "FullyQualifiedName~RegisterAdapterTests.ShouldRegisterNewAdapter"
```

### Run the Service

```bash
# Run locally (uses appsettings.Development.json)
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj

# Run with specific configuration
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj --configuration Debug
```

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

### Docker

```bash
# Build Docker image
docker build -f src/CommunicationControllerServices/Dockerfile -t octo-communication-controller .
```

## Testing Framework

Tests use **TUnit** (not xUnit or NUnit) with **NSubstitute** for mocking:
- Base test classes define common setup and mocked dependencies (e.g., `AdapterServiceTestsBase`)
- Test methods use `[Test]` attribute
- Assertions use TUnit's `Assert.That()` fluent API
- Mock setup uses NSubstitute's fluent API

Example test structure:
```csharp
public class MyTests : MyTestsBase
{
    [Test]
    public async Task ShouldDoSomething()
    {
        // Arrange - mocks configured in base class
        _mockRepository.GetAsync(Arg.Any<string>()).Returns(expectedResult);

        // Act
        var result = await _service.DoSomethingAsync();

        // Assert
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Value).IsEqualTo(expected);
    }
}
```

**Mocking System Events:**
When testing services that use `ICommunicationEventService`, create a mock in the test base class:
```csharp
protected readonly ICommunicationEventService CommunicationEventService = Substitute.For<ICommunicationEventService>();
```

## Integration Testing

Integration tests are located in `tests/CommunicationControllerServices.IntegrationTests/` and use **xUnit** with **FluentAssertions** (unlike unit tests which use TUnit).

### Fixture Architecture

Integration tests use a hierarchical fixture structure:

1. **`ServiceCollectionFixture`** - Base fixture providing DI container setup with all required services
2. **`DatabaseFixture`** - Extends ServiceCollectionFixture, adds MongoDB connection via Testcontainers
3. **`CommunicationControllerFixture`** - Main fixture that initializes system tenant, test tenant, and CK cache

### CK Model Dependencies

The System.Communication CK model depends on:
- `System` (base model)
- `System.Bot` (required dependency)

All three models must be imported in order before tests can work with typed entities like `RtPool`, `RtAdapter`, etc.

### Tenant Initialization for Tests

**CRITICAL:** To properly initialize a tenant for integration testing, you must:
1. Import CK models in dependency order (System → System.Bot → System.Communication) within a transaction
2. **Commit the transaction BEFORE loading the CK cache** (LoadCacheForTenantAsync creates a separate MongoDB session that cannot see uncommitted data)
3. **Unload any previously loaded tenant cache** before reloading (the cache loader skips tenants already in the cache)
4. Load the CK cache using `ITenantRepository.LoadCacheForTenantAsync(cacheService)` with the shared `ICkCacheService` from DI

The extension method `InitializeTenantForTestingAsync` in `src/CommunicationControllerServices/Extensions/TenantInitializationExtensions.cs` handles this:

```csharp
// In fixture initialization:
var ckCacheService = GetService<ICkCacheService>();
await systemContext.InitializeTenantForTestingAsync(TestTenantId, ckCacheService);
```

**Implementation details of `InitializeTenantForTestingAsync`:**
```csharp
// 1. Import CK models in a transaction (System → System.Bot → System.Communication)
using (var importSession = await systemContext.GetAdminSessionAsync())
{
    // ... import models in dependency order ...
    await importSession.CommitTransactionAsync();
}

// 2. Unload stale cache (if tenant was loaded during imports)
if (ckCacheService.IsTenantLoaded(tenantId))
{
    ckCacheService.Unload(tenantId);
}

// 3. Load the CK cache AFTER the import transaction is committed
var tenantRepository = await systemContext.FindTenantRepositoryAsync(tenantId);
await tenantRepository.LoadCacheForTenantAsync(ckCacheService);
```

**Why this matters:** `LoadCacheForTenantAsync` internally creates a new MongoDB session (via `TenantRepository.RefreshCkCacheServiceAsync`) that cannot see uncommitted data from other transactions. If the cache is loaded before the import transaction commits, newly imported models (like System.Communication) will be silently missing from the cache. Additionally, `ModelLoaderService.LoadAsync` has an `IsTenantLoaded` guard that skips loading if the tenant is already cached, so any stale cache must be unloaded first.

### Running Integration Tests

```bash
# Run all integration tests (requires Docker for MongoDB Testcontainers)
dotnet test --project tests/CommunicationControllerServices.IntegrationTests/CommunicationControllerServices.IntegrationTests.csproj

# Run a specific integration test
dotnet test --project tests/CommunicationControllerServices.IntegrationTests/CommunicationControllerServices.IntegrationTests.csproj --filter "FullyQualifiedName~CommunicationRepositoryTests"
```

### Service Registration in Tests

**CRITICAL - Registration Order:** CK models MUST be registered BEFORE `AddMongoDbRuntimeRepository()`. This ensures BSON class maps are available when MongoDB is initialized for typed entity deserialization.

```csharp
// CORRECT order in ServiceCollectionFixture:

// 1. Add CK models FIRST (order matters: base models first, then dependent models)
// IMPORTANT: CK models must be registered before AddMongoDbRuntimeRepository()
// to ensure BSON class maps are available for typed entity deserialization
Services.AddCkModelSystemV2();
Services.AddCkModelSystemBotV2();
Services.AddCkModelSystemCommunicationV2();
Services.AddCkModelSystemNotificationV2();

// 2. Add runtime engine with MongoDB AFTER CK models
Services.AddRuntimeEngine()
    .AddMongoDbRuntimeRepository();
```

**Common Error:** If you see `InvalidCastException: Unable to cast object of type 'RtEntity' to type 'RtPool'` (or similar), it typically means:
1. CK models were registered after MongoDB repository initialization, OR
2. The CK cache was not properly loaded for the tenant

## Important Patterns

### Service Registration

Services expose multiple interfaces (e.g., cache as `IAdapterCache` and `IAdapterCachePublish`). Use extension methods:
- `AddSingletonMultipleInterfaces<TImpl, TInterface1, TInterface2>()`
- `AddScopedMultipleInterfaces<TImpl, TInterface1, TInterface2>()`

### Error Handling

Services throw custom exceptions (e.g., `AdapterServiceException`, `PoolServiceException`) with static factory methods for specific error scenarios. Always log errors with NLog before throwing.

### Configuration

Configuration is bound to strongly-typed options classes:
- `OctoSystemConfiguration` from `System` section
- `CommunicationControllerOptions` from `CommunicationController` section
- Environment variables prefixed with `OCTO_` override appsettings

**Pipeline Execution Configuration Options:**
```csharp
// CommunicationControllerOptions
public int PipelineExecutionRetentionDays { get; set; } = 3;     // Days to keep execution records
public int StatisticsUpdateIntervalMinutes { get; set; } = 60;   // Statistics aggregation interval
public bool StoreInputData { get; set; } = false;                // Whether to store pipeline input data
public int MaxInputDataLength { get; set; } = 10000;             // Max length of stored input data
public int PipelineExecutionTimeoutHours { get; set; } = 24;     // Hours after which running executions are marked as failed
```

### Deployment State Management

Adapters and Pools track deployment state:
- `RtDeploymentStateEnum.Pending` - not deployed
- `RtDeploymentStateEnum.Deployed` - active
- Communication state tracked separately via `RtCommunicationStateEnum`

### Pool Communication State Transitions

The `CommunicationState` of an `RtPool` (`Unregistered` / `Online` / `Offline`)
is written from two paths and the **write order vs. cache mutation matters**:

| Trigger | Code path | State written | Notes |
|---|---|---|---|
| Operator's SignalR `OnConnectedAsync` | `PoolHub.OnConnectedAsync` → `PoolService.SetCommunicationStateOnlineAsync(tenantId, poolName, connectionId)` | `Online` | Adds the pool to `_poolCache` if missing. |
| Operator's `RegisterPoolOperatorAsync` invocation | `PoolHub.RegisterPoolOperatorAsync` → `PoolService.RegisterPoolOperatorAsync` → `SetCommunicationStateOnlineAsync(tenantId, poolRtId)` | `Online` | Workloads are deployed via the `WorkloadDeployedAsync` flow on the `/operatorHub`, not via the pool-hub adapter list (which no longer exists). |
| Operator's `UnregisterPoolOperatorAsync` invocation (graceful undeploy) | `PoolHub.UnregisterPoolOperatorAsync` → `PoolService.UnregisterPoolOperatorAsync` | `Unregistered` | **Must write the state before `PoolTenant.RemovePool`** — otherwise the `OnDisconnectedAsync` that follows finds nothing in the cache and silently no-ops. |
| Operator's SignalR connection drops without an `UnregisterPoolOperatorAsync` (crash, network) | `PoolHub.OnDisconnectedAsync` → `PoolService.SetCommunicationStateOfflineAsync(tenantId, poolName)` | `Offline` | The hub must pass `poolName`, never `Context.ConnectionId` — the two-arg overload looks the pool up by name in `_poolCache.PoolsByName`. When `UnregisterPoolOperatorAsync` already ran, the pool is gone from the cache and this becomes a clean no-op. |

Tests for this state machine live in
`tests/CommunicationControllerService.Tests/Services/PoolServiceTests/`:
- `UnregisterPoolOperatorAsyncTests` pins the write-before-remove ordering.
- `SetCommunicationStateOfflineAsyncTests` pins that the method only writes
  when the lookup key is a real pool name (not a SignalR connection id).

### Cloud Pool Deploy Tracking (for the PreDeleteTenant cascade)

The `OperatorConnectionManager` keeps an in-memory map
`tenantId → Set<poolName>` of Cloud pools that have been notified to operators
as deployed but not yet undeployed. `NotifyPoolDeployedAsync` adds an entry,
`NotifyPoolUndeployedAsync` removes one.

`PoolService.UndeployAllCloudPoolsAsync` reads from this map (via
`GetDeployedPoolsForTenant`) rather than the tenant repository. Reason:
`TenantManagementConsumer.ConsumeAsync(PreDeleteTenant)` fires in parallel
with `PreUpdatePreDeleteTenantConsumer` (in `octo-common-services`), which
unloads the CK-cache for the tenant. A repository-based lookup races with
that unload and throws `CommunicationRepositoryException: Failed to get
pools` — and the operator is never told to clean up, leaving the
`CommunicationPool` CR and broker secret orphaned in the cluster.

Caveat: the map is process-local, so it survives only as long as the
controller pod. If the controller restarts between deploy and tenant delete,
the cascade has nothing to undeploy. Tracking restart-survival via an
operator-side `RegisterOperatorAsync` reverse-sync is the existing TODO on
`OperatorConnectionManager.GetDeployedPools()`.

Tests:
- `Hubs/OperatorConnectionManagerTests` — tracking add/remove, tenant
  isolation, bucket cleanup when empty.
- `Services/PoolServiceTests/UndeployAllCloudPoolsAsyncTests` — including a
  regression test that pins `ICommunicationRepository.GetPoolsAsync` is
  never called from this path.

### Helm Workload Deploy (Phase 2)

The Communication Operator no longer deploys Adapter pods directly — it
runs `helm upgrade --install` for every Adapter and Application managed
by a Cloud pool. CK model (3.15.0) provides the type hierarchy:
`DeployableEntity → DeployableWorkload → Adapter | Application`.

**`IWorkloadEncryptionService`** (`Services/WorkloadEncryptionService.cs`)
provides AES-256-GCM encryption with a single instance-wide
`InstanceSecretKey` configured via
`OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY` (Base64-encoded
32-byte key). Ciphertext is `enc:v1:<base64(nonce||tag||ciphertext)>` —
`Decrypt` is a no-op when no sentinel is present, so a single CK
attribute can carry either a plaintext or an encrypted value. This same
service is also used for `HelmRepositoryConfiguration.Password` and any
future at-rest-encrypted attribute.

**`PoolService.DeployPoolAsync`** (Cloud pools) now:
1. Sets `DeploymentState = Deployed` on the pool.
2. Notifies the operator via `IOperatorConnectionManager.NotifyPoolDeployedAsync`.
3. Enumerates the pool's managed workloads through
   `ICommunicationRepository.GetWorkloadsForPoolAsync` (polymorphic load of
   `RtDeployableWorkload` via the `Manages` inbound association).
4. For each workload: resolves its `HelmRepositoryConfiguration` via
   `GetHelmRepositoryForWorkloadAsync`, decrypts secret-flagged
   `ValueOverride.Value` entries and the repository password, then fires
   `NotifyWorkloadDeployedAsync` with the assembled `WorkloadDeployedDto`.

**`PoolService.UndeployPoolAsync`** mirrors deploy but in reverse order:
workloads first (so the operator can `helm uninstall` while the pool
namespace still exists), then the pool itself.

**Tenant-delete cascade** (`UndeployAllCloudPoolsAsync`) reads tracked
workloads from `OperatorConnectionManager.GetDeployedWorkloadsForTenant`
and fires `NotifyWorkloadUndeployedAsync` for each, then notifies the
pool undeploys — same in-memory-tracking pattern as for pools, same
rationale (avoids racing with `PreUpdatePreDeleteTenantConsumer`'s
cache unload).

**`OperatorConnectionManager`** carries the workload tracking table:
`tenantId → Map<{poolName}|{workloadName}, WorkloadUndeployedDto>`.
`NotifyWorkloadDeployedAsync` adds, `NotifyWorkloadUndeployedAsync`
removes. Same restart-survival caveat as the pool tracking: if the
controller pod restarts between deploy and cascade, the in-memory state
is gone. Reverse-sync from a freshly (re)connecting operator's
`RegisterOperatorAsync` would close this gap (TODO).

Phase-3 plan in `octo-communication-operator/docs/DEPLOYMENT-MANAGEMENT-CONCEPT.md`
covers the operator-side helm CLI integration.

Tests:
- `Services/WorkloadEncryptionServiceTests` — round-trip, sentinel-skip,
  tamper detection, key-mismatch, missing/invalid key handling.
- `Hubs/OperatorConnectionManagerTests` — workload tracking add/remove,
  tenant isolation, bucket cleanup when empty.
- `Services/PoolServiceTests/DeployPoolAsyncTests` — Cloud/Edge branches,
  workload fan-out, undeploy ordering, scoping workloads to the right pool.
- `Services/PoolServiceTests/UndeployAllCloudPoolsAsyncTests` — extended
  with workload-cascade tests.

## Project Structure Notes

- Main service: `src/CommunicationControllerServices/`
- Construction Kit model: `src/SystemCommunicationCkModel/`
- Resource strings: `src/CommunicationControllerServices.Resources/`
- Unit tests (TUnit): `tests/CommunicationControllerService.Tests/`
- Integration tests (xUnit): `tests/CommunicationControllerServices.IntegrationTests/`
- CI/CD: `devops-build/azure-pipelines.yml`
- Output directory: `bin/$(Configuration)/`

## Configuration Notes

- `InternalsVisibleTo` configured for tests and DynamicProxy (for NSubstitute proxy generation)
- Target framework: .NET 10.0
- NLog configuration in `src/CommunicationControllerServices/nlog.config`
- SignalR configured with 100MB max message size
- Three build configurations: `Debug`, `Release`, `DebugL` (local development with version 999.0.0)

## Exception Handling Pattern

Services use a factory method pattern for exceptions with private constructors and static factory methods:

```csharp
// Wrong - don't throw generic exceptions
throw new AdapterServiceException("Tenant not enabled");

// Correct - use factory methods
throw AdapterServiceException.TenantNotEnabled(tenantId);
```

Each service has a dedicated exception class: `AdapterServiceException`, `PoolServiceException`, `PipelineDebugServiceException`, `TriggerManagementServiceException`, `PipelineExecutionServiceException`, `CommunicationRepositoryException`.

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
- **System API** (`SystemApi/v1/Controllers/`): System-level operations (enable/disable tenants via query parameter, kept for backward compatibility)
- **Tenant API** (`TenantApi/v1/Controllers/`): Tenant-scoped operations for adapters, pools, pipelines, and tenant enable/disable

Routes follow pattern: `{tenantId:tenantId}/v{version:apiVersion}/[controller]`

The `CommunicationController` exists in both System API and Tenant API:
- **System API**: `system/v1/communication/enable?tenantId=X` (legacy, backward compatible)
- **Tenant API**: `{tenantId}/v1/communication/enable` (preferred, tenant from route)

## Pipeline Execution Metrics

The `PipelineExecutionService` tracks pipeline execution metrics reported by adapters via SignalR. This enables real-time monitoring and historical analysis of pipeline performance.

### CK Model Entities

| Type | Description |
|------|-------------|
| `RtPipelineExecution` | Records individual pipeline execution with status, timing, and optional input data |
| `RtPipelineStatistics` | Aggregated statistics per pipeline (1h, 12h, 24h, 30d periods) |
| `RtPipelineExecutionStatusEnum` | Status: Running, Completed, Failed, Interrupted, Cancelled |
| `RtPipelineTriggerTypeEnum` | Trigger: Manual, Scheduled, Event, Startup |

### Hub Methods (AdapterHub)

Adapters report execution lifecycle via SignalR:
- `ReportExecutionStartAsync(PipelineExecutionStartDto)` - Reports execution start
- `ReportExecutionEndAsync(PipelineExecutionEndDto)` - Reports execution completion
- `ReportInterruptedExecutionResultAsync(PipelineExecutionEndDto)` - Reports final status after reconnect
- `GetInterruptedExecutionIdsAsync()` - Gets IDs of executions interrupted by disconnect

### Interruption Handling

When an adapter disconnects unexpectedly:
1. All running executions for that adapter are marked as `Interrupted`
2. On reconnect, adapter can query interrupted execution IDs
3. Adapter reports final status via `ReportInterruptedExecutionResultAsync`

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `PipelineExecutionReportProcessor` | Continuous | Drains execution reports from Channel in batches, bulk-inserts starts and bulk-updates completions |
| `PipelineStatisticsBackgroundService` | 60 minutes | Aggregates execution statistics for all pipelines |
| `ExecutionCleanupBackgroundService` | Daily | Times out stale running executions and removes records older than retention period |

### Service Methods

```csharp
// IPipelineExecutionService
Task StartExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionStartDto startDto);
Task CompleteExecutionAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto endDto);
Task MarkExecutionsAsInterruptedAsync(string tenantId, RtEntityId adapterRtEntityId);
Task ReportInterruptedExecutionResultAsync(string tenantId, RtEntityId adapterRtEntityId, PipelineExecutionEndDto endDto);
Task<IReadOnlyList<string>> GetInterruptedExecutionIdsAsync(string tenantId, RtEntityId adapterRtEntityId);
Task UpdateStatisticsAsync(string tenantId, RtEntityId pipelineRtEntityId);
Task UpdateAllStatisticsAsync(string tenantId);
Task<int> CleanupOldExecutionsAsync(string tenantId, int retentionDays);
Task<int> TimeoutStaleExecutionsAsync(string tenantId, int timeoutHours);
```
