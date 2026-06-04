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

### Service-Managed Blueprints (System.Communication.*)

`SystemCommunicationCkModel` ships two embedded blueprints that seed a tenant with a
default Cloud `Pool` + managed `Adapter`. The variants are picked per cluster via
`requires.octo.environment`, sharing the same rtIds so a tenant moving channels
(staging→prod hand-over) keeps its entities. The pattern mirrors the embedded
CK-model packaging — see `octo-construction-kit-engine/docs/blueprints.md` for the
engine side.

```
src/SystemCommunicationCkModel/
├── ConstructionKit/                          # CK model YAML
└── Blueprints/                               # System.* = service-managed
    ├── System.Communication.Release/         # requires.octo.environment: [staging, production]
    │   ├── blueprint.yaml                    #   blueprintId: System.Communication.Release-1.0.0
    │   └── seed-data/entities.yaml           #   ChartVersion: "${octo.version}"
    └── System.Communication.MainLatest/      # requires.octo.environment: [dev, test]
        ├── blueprint.yaml                    #   blueprintId: System.Communication.MainLatest-1.0.0
        └── seed-data/entities.yaml           #   ChartVersion: ""
```

The folder name carries only the blueprint **Name**; the version lives exclusively
in the manifest's `blueprintId`. Bumping the version is a manifest-only edit — no
folder rename required. The `BlueprintEmbed` MSBuild task validates that the folder
name equals `blueprintId.Name`.

| Variant                                | ChartVersion seed                | Use case |
|----------------------------------------|----------------------------------|----------|
| `System.Communication.Release`         | `"${octo.version}"`              | staging / production — matches the release-channel chart 1:1 (both derived from the same r-tag), so the first Deploy on a freshly attached tenant lands a working chart pull. |
| `System.Communication.MainLatest`      | `""` (empty)                     | dev / test — the operator's `HelmRunner` omits `--version` when blank and helm picks the newest chart from the dev channel. CD pipeline `deploy-adapter-chart-octo-mesh-adapter-*` then writes a concrete `0.1.<yyMMDDxxx>` version on the next main-CI rollout. |

Flow:

1. `<BlueprintFolder>` in `SystemCommunicationCkModel.csproj` triggers the `BlueprintEmbed` MSBuild task. The task validates each `blueprint.yaml`, embeds the files as manifest resources, and emits `obj/.../blueprints-cache.json`. The `BlueprintSourceGenerator` generates one DI extension per blueprint (`AddBlueprintSystemCommunicationReleaseV1`, `AddBlueprintSystemCommunicationMainLatestV1`) plus their `IBlueprintEmbeddedSource` implementations.
2. `Program.cs` registers every embedded source via the generated `AddBlueprint…V1()` extensions. The engine's `EmbeddedResourceBlueprintCatalog` (auto-registered by `AddConstructionKit()`) discovers them.
3. `DefaultConfigurationCreatorService.ImportCkModelAsync` (Enable path) and `StartTenantAsync` (every tenant load) both call `ApplyServiceManagedBlueprintsAsync`, which iterates every embedded blueprint whose name starts with `ServiceManagedBlueprintPrefix = "System.Communication."` (trailing dot anchors the match), picks the newest version per name, and invokes `IBlueprintService.ApplyBlueprintAsync`. Each blueprint's `requires:` decides whether it actually runs on the given tenant — non-matching ones return `BlueprintApplicationResult.Skipped` (success no-op, no install row, no seed). The blueprint runner is idempotent at the same version, so the cost of the per-startup call is near-zero unless the embedded version bumped.
4. Bumping any variant's `blueprintId` in `blueprint.yaml` (e.g. `System.Communication.Release-1.0.0` → `…-1.0.1`, no folder rename required) and shipping a new NuGet rolls every applicable tenant forward on the next service restart — no per-tenant migration script required.

The `System.` name prefix tells Refinery Studio (`BlueprintIdExtensions.IsServiceManaged`) to hide Install / Re-apply controls — the service owns these blueprints and admins shouldn't fight them.

> **Migration note**: tenants previously seeded with `System.Communication-1.0.2` keep that installation row as an orphan after the split — the new blueprints overwrite the Pool/Adapter entities by shared rtId (functional behaviour preserved), but the old install row still shows up in Studio. A one-time `mutation { blueprints { uninstall(blueprintName: "System.Communication", cascade: false) { success entitiesDeleted } } }` removes it per tenant.

The optional `HelloCommunication-1.0.0` demo blueprint lives in `samples/Blueprints/` (not embedded). It's admin-installable: drop the folder into a `LocalFileSystemBlueprintCatalog` root or publish it via a GitHub catalog. Its `blueprintDependencies` reference `System.Communication-[1.0,2.0)` so it can be applied on top of the service-managed baseline.

#### Runtime-State Attributes (do NOT reset on blueprint re-apply)

`System.Communication-3.22.0` (paired with blueprints
`System.Communication.Release-1.5.0` and `System.Communication.MainLatest-1.4.0`)
marks the volatile state attributes on `Adapter` / `Pool` as
`isRuntimeState: true` in `attributes.yaml`. The blueprint engine reads
that marker (see `octo-construction-kit-engine/CLAUDE.md` → "Runtime-State
Preservation on Re-Apply") and preserves the existing runtime value when
the blueprint is re-applied for a version bump — instead of overwriting
it with whatever the seed YAML happens to declare as the
fresh-tenant default.

| Attribute | Why it's runtime state |
|---|---|
| `DeploymentState` | Driven by `PoolService.Deploy/UndeployPoolAsync` + the reverse-sync from `ReportDeployedStateAsync`. Reset to 0 by a seed import flips a Deployed entity back to Undeployed in Studio. |
| `CommunicationState` + `CommunicationStateTimestamp` | Written by the `OperatorHub` / `AdapterHub` connect & disconnect handlers and the rolling-upgrade shutdown guard. Seed-managed reset would mark an online operator as Unregistered. |
| `ConfigurationState` | Toggled by `AdapterService` on configuration deploy / failure. |
| `StatusMessage` | Live status text overwritten on every state transition — pure runtime breadcrumb. |
| `LastDeploymentError` + `LastDeploymentErrorTimestamp` | Persistent error history written when a deploy fails; cleared on the next successful deploy. The blueprint must never silently clear these. |
| `LastConfigurationError` + `LastConfigurationErrorTimestamp` | Same contract as the deployment-error pair, scoped to the configuration state machine. |
| `LastSyncedSequenceNumber` | Adapter offline-sync cursor — wiping it would replay every queued event. |

Before this marker landed (CK model 3.21.0 and earlier), bumping a
service-managed blueprint silently reset all of these on the next
controller restart. The first observed regression was the
1.3.0 → 1.4.0 bump on `System.Communication.Release` (commit
`80cd9422`, adding the `meshmakers-apps` HelmRepository entity): every
tenant whose `MeshAdapter` had been `Deployed` flipped back to
`Undeployed` even though the helm release was still healthy and the
operator was still connected. Recovery for that incident is documented
in `docs/runbooks/recover-mesh-adapter-state.md`. Any future
service-managed-blueprint bump on 3.22.0+ should not need a runbook
companion for this class of failure.

When adding a new attribute on `Adapter` / `Pool` / similar entities,
decide at creation time: is the value driven by the blueprint author
(configuration → leave `isRuntimeState` unset), or by services /
operators / users at runtime (status / counters / error history →
mark `isRuntimeState: true`). Once shipped, downgrading from `true` to
`false` is a behaviour-breaking change.

### Empty ChartVersion ("use latest")

`WorkloadController.UpdateChartVersion` accepts a blank value as the explicit
opt-in for "deploy the newest chart in the configured Helm repository"; non-empty
values must still parse as SemVer. The audit-event message renders `(latest)` for
the empty sentinel so CI/CD inspection stays readable.

`PoolService.EnsureWorkloadIsHelmDeployableAsync` /
`BuildWorkloadDeployedDtoAsync` / `IsWorkloadHelmDeployableAsync` only require
`ChartName` and a linked `HelmRepositoryConfiguration` with a non-empty
`RepositoryUrl` — `ChartVersion` is optional. The `WorkloadDeployedDto` sent to
the operator coalesces a null `ChartVersion` to `""`; the operator's `HelmRunner`
then omits the `--version` argument when blank and helm picks the newest tag in
the repo.

This is the rollout contract for `System.Communication.MainLatest` — dev/test
tenants ship with empty `ChartVersion` and the CD pipeline overwrites it on every
main-CI run. The same path works for any workload whose operator wants to track a
channel instead of pinning a specific version.

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
| Operator's SignalR connection drops without an `UnregisterPoolOperatorAsync` (crash, network) | `OperatorHub.OnDisconnectedAsync` → `PoolService.SetCommunicationStateOfflineAsync(tenantId, poolName, disconnectingConnectionId)` | `Offline` | The hub must pass `poolName`, never `Context.ConnectionId`, as the lookup key. The third arg is the **disconnecting** connection id — the service compares it with the cache's current `Pool.ConnectionId` and only writes Offline if they still match. A newer connection that has replaced the disconnecting one (e.g. the operator auto-reconnected after a controller restart and the previous connection's handler is firing late) is treated as a stale disconnect and the call no-ops. Mirrors `AdapterService.SetAdapterCommunicationStateOfflineAsync`'s stale-disconnect guard. |

Tests for this state machine live in
`tests/CommunicationControllerService.Tests/Services/PoolServiceTests/`:
- `UnregisterPoolOperatorAsyncTests` pins the write-before-remove ordering.
- `SetCommunicationStateOfflineAsyncTests` pins (a) the happy path (matching
  connection ids → Offline written), (b) the stale-disconnect guard (cached
  connection id != disconnecting id → no-op, prevents the regression where
  the previous connection's late `OnDisconnectedAsync` overwrote Online state
  written by the new connection), and (c) that the lookup key is a real
  pool name, not a SignalR connection id.

### Rolling-Upgrade Race Guard (`IShutdownState`)

The intra-process stale-disconnect guard above only works **within one
controller pod**. During a Kubernetes rolling upgrade two pods overlap for
a few seconds, and the old pod's `OperatorHub.OnDisconnectedAsync` /
`AdapterHub.OnDisconnectedAsync` handlers fire as the operator and adapters
reconnect to the new pod. Observed sequence on `test-2`:

1. New controller pod starts, `RegisterPoolAsync` from the reconnected
   operator writes `Online` at T+0.
2. Old controller pod (still mid-shutdown) finally processes its dropped
   SignalR connection; its `OnDisconnectedAsync` calls
   `SetCommunicationStateOfflineAsync` with the OLD connection id.
3. The OLD pod's cache still has that connection id, so the stale-disconnect
   guard does **not** reject — both pods think they're the authority.
4. Old pod writes `Offline` at T+1s. `CommunicationRepository.SetPoolCommunicationStateAsync`
   uses `AttributeNewerThanGuard` to reject writes with timestamps OLDER
   than the current value; T+1s is newer than T+0, so the write succeeds.
5. UI shows the pool Offline until something else triggers an Online write
   (e.g. a second operator claiming the same pool, or an operator restart).

The fix is `IShutdownState` (`Services/IShutdownState.cs`,
`Services/HostApplicationShutdownState.cs`), a tiny singleton wrapper around
`IHostApplicationLifetime.ApplicationStopping.IsCancellationRequested`.
Both `OperatorHub.OnDisconnectedAsync` and `AdapterHub.OnDisconnectedAsync`
check it at the top of the handler and bail out before any
`SetCommunicationStateOfflineAsync` call — the surviving pod is the
authoritative state holder once shutdown starts. Local cache cleanup
(`OperatorConnectionManager.RemoveOperator`) still runs so any straggler
hub invocations on this pod don't see a stale connection.

The graceful `UnregisterPoolOperatorAsync` path is **not** gated by
`IShutdownState`: it represents an explicit user/operator decision to take
the pool offline (e.g. tenant undeploy) and must persist that state
regardless of pod lifecycle.

Tests:
- `Hubs/OperatorHubTests/OnDisconnectedAsyncTests` — `NormalDisconnect_WritesOfflineForEveryOrphanedPool`
  pins the existing happy path; `ShuttingDown_SkipsOfflineWritesButStillRemovesOperator`
  pins the shutdown-guard regression.
- `Hubs/AdapterHubTests/OnDisconnectedAsyncTests/ShuttingDown_SkipsOfflineWriteAndInterruptedMark`
  pins the equivalent guard on the adapter hub. The guard runs **before**
  `GetTenantId()` / `GetAdapterRtEntityId()` so the shutdown path stays valid
  even if the `HttpContext` has already been torn down (and so the test
  doesn't need to mock one).

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
the cascade has nothing to undeploy. Restart-survival is closed by the
operator-side reverse-sync (see "Operator Reverse-Sync" below) — a Cloud
operator reconnecting after either pod's restart calls
`ReportDeployedStateAsync` with the set of pools / workloads it currently
manages, and the controller rebuilds the tracking through
`TrackDeployedPool` / `TrackDeployedWorkload`.

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
   The workload's `ReceivesClusterSecrets` attribute (inherited from
   `RtDeployableWorkload`, so both Adapters and Applications carry it)
   is copied onto the DTO so the operator can decide whether to inject
   cluster credentials (MongoDB, CrateDB passwords) as secret-flagged
   value overrides at deploy time. The attribute defaults to false; the
   operator's secret-injection path runs only when set explicitly. The
   RabbitMQ broker password is NOT gated by this flag — it is injected
   unconditionally because every workload needs the controller-to-workload
   command bus.

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
removes. Restart-survival is closed by the operator-side reverse-sync
(see "Operator Reverse-Sync" below) — a Cloud operator reconnecting
after either pod's restart calls `ReportDeployedStateAsync`, which
restores both `DeploymentState=Deployed` and the per-tenant tracking
maps so the `PreDeleteTenant` cascade still has the entries it needs.

**Public ingress per workload.** `RtDeployableWorkload` carries two
typed attributes on the base type so Adapter and Application share them:
`IngressEnabled` (bool, default false) and `Hostname` (optional string).
`BuildWorkloadDeployedDtoAsync` copies them onto `WorkloadDeployedDto`
(normalising blank Hostname to null). `EnsureWorkloadIsHelmDeployableAsync`
rejects `IngressEnabled=true` + empty Hostname at Deploy time
(`PoolServiceException.WorkloadIngressEnabledButHostnameEmpty`) — the chart
templates build host rules from `publicUri`, and an empty host would fail
k8s admission mid-helm-rollout. The operator's
`WorkloadContextValuesBuilder` then projects `ingress.enabled=true` plus
top-level `publicUri=https://<Hostname>` only when both conditions hold,
layering on top of the cluster-wide `ingress.*` defaults (className,
cert-manager cluster-issuer, TLS) from operator config; those defaults
are not overridable per workload by design. The chart's
`templates/ingress.yaml` builds the tenant-scoped path (`/{tenantId}`) for
adapter charts itself, so the operator only needs to supply hostname +
enabled — no chart change required.

**Workload event routing.** `NotifyWorkloadDeployedAsync` and
`NotifyWorkloadUndeployedAsync` route only to the SignalR connection(s)
that have registered the target `(tenantId, poolName)` via
`RegisterPoolForConnection` — looked up through the
`GetConnectionsForPool` helper. Pool-level events (`PoolDeployedAsync`,
`PreUpdateTenantAsync`) still broadcast to every connected operator,
because the central operator with `AutoManagePools=true` decides
whether to create CRs based on the broadcast; edge operators just
ignore it. Workload events used to broadcast too — that meant a
central operator and an edge operator connected to the same controller
both received every workload-deploy event. The central operator would
happily helm-install the chart into its own namespace and report
success, overwriting the edge operator's `success=false` on the runtime
entity, leaving a stray release on the wrong cluster while the Studio
showed `DeploymentState=Deployed`. The routing fix scopes workload
events to the one operator that actually owns the target pool.

**Operator-mode enforcement at registration.** `IOperatorHub.RegisterOperatorAsync(bool? autoManagePools)`
carries the calling operator's mode (true = central / Cloud-only,
false = edge / Edge-only, null = legacy build pre-dating the
parameter). `OperatorConnectionManager` stores it per connection via
`SetOperatorMode` / `GetOperatorMode`. `OperatorHub.RegisterPoolAsync`
then looks up `RtPool.Environment` and rejects with a typed
`HubException` + writes an Error event via `ICommunicationEventService`
when an edge operator tries to claim a Cloud pool (or vice versa). A
legacy operator without a declared mode is allowed through but logged
+ audit-recorded as an Information event so the trail still shows the
registration. This is the controller-side complement to the
operator's `AutoManagePools` gate (which prevents the edge operator
from materializing CRs in the first place); together they keep
workload-deploy events from routing to the wrong cluster even if a
stale CR materialises on the wrong side. Tests in
`Hubs/OperatorHubTests/RegisterPoolAsyncTests` cover the four matrix
cells (central+Cloud, edge+Edge, central+Edge, edge+Cloud), legacy
fall-through, and the pool-not-found path.

**Edge vs. Cloud at the workload layer.** `DeployPoolAsync` rejects Edge
pools (the central operator does not create the CR / broker secret on the
edge cluster), but `DeployWorkloadAsync` and `UndeployWorkloadAsync` are
Environment-agnostic. The edge operator runs `OperatorHubService.WorkloadDeployedAsync`
through the same `WorkloadReconciler.DeployAsync` helm path as central —
only the pool-level CR/secret side effect is central-only (`AutoManagePools`
on the operator). The Recompute / Undeploy resting-state rules therefore
consider only missing Helm fields when deciding Disabled vs. Undeployed
for a workload, never the parent pool's Environment.

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

### Operator Reverse-Sync

Closes the restart-survival gap on the controller-side in-memory tracking
maps and on `DeploymentState` drift. The flow:

1. Operator pod restarts (or controller pod restarts).
2. Operator reconnects and calls `RegisterOperatorAsync(autoManagePools=true)`
   — controller returns the currently-tracked Cloud pools (forward sync).
3. Operator follows up with `ReportDeployedStateAsync(pools)` — for every
   pool / workload it currently has a healthy helm release for, it sends an
   `OperatorDeployedPoolReportDto` carrying `{TenantId, PoolRtId, PoolName,
   WorkloadRtIds[]}`.
4. Controller's `OperatorHub.ReportDeployedStateAsync` rejects the call with
   a typed `HubException` when the operator's declared mode is anything
   other than Cloud (edge or legacy/unknown) and writes an error audit event
   before throwing — defense in depth, prevents an edge cluster from
   reviving central-cluster state.
5. `PoolService.RestoreDeployedStateAsync` runs the per-pool work:
   - Loads `RtPool` by rtId; skips when missing.
   - Per-pool `Environment != Cloud` guard skips Edge pools silently —
     a second line of defense against a Cloud operator reporting cross-mode
     entities.
   - Writes `DeploymentState=Deployed` **only when the current state would
     change** (avoids no-op audit events).
   - Calls `OperatorConnectionManager.TrackDeployedPool` + `RegisterPoolForConnection`
     so undeploy fan-out + `OnDisconnected` offline-write keep working on the
     new connection.
   - Same restore-only-when-changed rule for each `WorkloadRtId` in the
     report; uses `SetWorkloadDeploymentStateAsync` (polymorphic over Adapter /
     Application) + `TrackDeployedWorkload`.
6. Empty report is a valid no-op (operator owns nothing yet).

**Why not extend `RegisterOperatorAsync` to take the list?** Keeping the
two contracts separate lets the operator stage the calls — fast register +
synchronous deploy-pool list first, then the larger workload-aware reverse-sync
catches up. It also keeps the SDK contract additive: existing operator builds
that don't know about `ReportDeployedStateAsync` keep working (they just won't
self-heal the tracking, which is the prior behaviour).

Tests:
- `Hubs/OperatorHubTests/ReportDeployedStateAsyncTests` — Cloud delegates,
  edge / legacy reject with HubException + audit, empty list still delegates.
- `Services/PoolServiceTests/RestoreDeployedStateAsyncTests` — Pending →
  Deployed restore, already-Deployed write-skip but track, Edge silently
  skipped, unknown pool silently skipped, workload restore + tracking,
  malformed rtId silently skipped.

### Workload Template Resolution

Workloads' `Hostname`, non-secret `ValueOverride.Value` and `ValuesYaml`
may carry template placeholders that are resolved at **deploy time**, not
at blueprint-apply time. Late binding keeps workload runtime entities
portable: moving a tenant between clusters picks up the destination
cluster's domain / service config without re-seeding entities.

Three placeholder families are supported:

| Placeholder | Source | Example |
|---|---|---|
| `{{domain.NAME}}` | `CommunicationControllerOptions.Domains` (cluster config) | `{{domain.default}}` → `staging.octo-mesh.com` |
| `{{service.NAME}}` | `CommunicationControllerOptions.ServiceUrls` (cluster config) | `{{service.authority}}` → `https://identity.staging.octo-mesh.com` |
| `{{context.tenantId}}` | `WorkloadTemplateContext` (per deploy) | `{{context.tenantId}}` → `acme` |

The semantic key `authority` maps to the Identity Service public URI; other
`service.*` keys follow the helm-section name (`assetRepository`, `bot`,
`communication`, `adminPanel`, `studio`). The `context.*` namespace is kept
so future per-deploy values (workload rtId, pool name) can land without
touching templates already in the field.

- **Options:**
  - `CommunicationControllerOptions.Domains` — bound from
    `OCTO_COMMUNICATIONCONTROLLER__DOMAINS__{NAME}={baseDomain}` env vars.
  - `CommunicationControllerOptions.ServiceUrls` — bound from
    `OCTO_COMMUNICATIONCONTROLLER__SERVICEURLS__{NAME}={url}` env vars.
  - Helm chart emits one env var per `services.communication.domains` map
    key, plus a fixed block of `SERVICEURLS__AUTHORITY`,
    `__ASSETREPOSITORY`, `__BOT`, `__COMMUNICATION`, `__ADMINPANEL` and
    (if-gated) `__STUDIO` from the corresponding `services.<name>.publicUri`
    helm values (`octo-helm-core/src/octo-mesh/templates/_env.tpl`,
    communication branch).
- **Resolver:** `IWorkloadTemplateResolver` / `WorkloadTemplateResolver`
  (`Services/WorkloadTemplateResolver.cs`). Singleton, regex-based, lookup
  is case-insensitive on NAME. `TryResolve(template, context, out resolved,
  out unknownPlaceholder)` returns `false` on the first unresolvable
  placeholder so callers get a stable, actionable error; the offender is
  reported in its full `family.NAME` form (e.g. `service.nope`,
  `context.tenantId`). Unknown namespaces (`{{foo.bar}}`) deliberately do
  NOT trigger an error — they pass through verbatim so a ValuesYaml block
  can carry literal Go-template-looking strings without the resolver
  tripping.
- **Hook points in `PoolService`:**
  - `EnsureWorkloadIsHelmDeployableAsync` validates the template up-front
    on **all three input surfaces** (Hostname, every non-secret
    `ValueOverride.Value`, and the whole `ValuesYaml` string) and throws
    `PoolServiceException.WorkloadTemplateUnknownPlaceholder` so
    misconfigured workloads fail at Deploy with an actionable message
    instead of producing an Ingress with a literal `{{...}}` host (which
    k8s admission would reject mid-rollout) or a helm values file with
    unresolved placeholders.
  - `BuildWorkloadDeployedDtoAsync` applies the resolver to the same three
    surfaces before assigning to `WorkloadDeployedDto`. By this point the
    template has already been validated, so the resolver call cannot fail
    in practice.
- **Secret-flagged `ValueOverride` entries are not part of the input
  surface.** The encryption / sentinel layer owns those values; running
  templating over decrypted secret material would mix two contracts and
  could leak placeholder text into the chart secret. Decrypt runs alone
  on secret entries.
- **API:**
  - `GET {tenantId}/v1/communication/domains` (legacy) returns just the
    configured `DomainConfigurationDto[]`.
  - `GET {tenantId}/v1/communication/workload-variables` returns every
    available placeholder as `WorkloadVariableDto[]` across all three
    families so the Studio's workload editor can offer a single suggestion
    list. `context.tenantId` is always present with `SampleValue=null`;
    domain / service entries carry the configured value as `SampleValue`.
    Both endpoints use `TenantCommunicationApiReadOnlyPolicy`; results are
    identical per tenant.
- **Per-cluster wiring** for domains lives in
  `octo-mesh-deployment/clusters/*/values-octo-mesh.yaml` under
  `services.communication.domains`. Service URLs are derived from the
  existing `services.identity.publicUri`, `services.assetRepository.publicUri`
  etc. helm values — those are already mandatory in production clusters,
  so no values-file change is needed to pick up the new `{{service.*}}`
  family.
- **Why `{{ }}` and not `${ }`:** Blueprint variables already use `${name}`
  (`BlueprintVariableInterpolator` in `octo-construction-kit-engine`) and
  are resolved at blueprint-apply time. Reusing that syntax for deploy-time
  substitution would either pollute the blueprint warning log (the engine's
  default provider warns on unknown vars) or require a cross-layer
  skip-prefix coupling. Double-brace `{{family.NAME}}` keeps the two
  resolution layers visually distinct in YAML and decoupled in code.

Tests:
- `Services/WorkloadTemplateResolverTests` — literal pass-through, single +
  mixed-family substitution (`domain.* + service.* + context.tenantId`),
  case-insensitive NAME match, unknown domain / service / empty-context
  paths each report the fully-qualified offender, first-offender wins,
  single-brace `${...}` is ignored, whitespace inside `{{ }}` tolerated,
  unknown namespace (`{{foo.bar}}`) stays literal.
- `Services/PoolServiceTests/DeployPoolAsyncTests` — Hostname,
  ValueOverride and ValuesYaml resolution + unknown-placeholder paths;
  `DeployWorkloadAsync_SecretValueOverride_NotSubstituted` pins the
  encryption/template boundary.
- `Controllers/CommunicationControllerWorkloadVariablesTests` — pins the
  ordering and shape of `GET /workload-variables` so the Studio's
  suggestion list stays stable.

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

## CI/CD Workload Rollout E2E

Manual end-to-end validation of the Epic 3054 rollout flow on the
`test-2` cluster lives at `docs/E2E-CICD-WORKLOAD-ROLLOUT.md`. Run after
non-trivial changes in `WorkloadController`, `ICommunicationRepository`'s
workload methods, `octo-cli` workload commands, or the deploy-workload
pipeline template. Companion to the operator-side smoke test
(`octo-communication-operator/docs/E2E-SMOKE-TEST.md`); the two together
cover the whole pool→workload deploy path.

## Workload Chart Management (Phase 2 — Epic 3054 / #4052)

`TenantApi/v1/Controllers/WorkloadController.cs` exposes two endpoints used
by the CI/CD rollout flow described in
`docs/concepts/cicd-workload-deployment.md`:

| Method | Path | Purpose |
|---|---|---|
| `GET`   | `{tenantId}/v1/workload?chartName=…` | Returns `WorkloadSummaryDto[]` — every `RtDeployableWorkload` (Adapter or Application) whose `ChartName` matches. Empty array means "this tenant has no exposure to the chart", which CI scripts use as the silent-skip signal. |
| `PATCH` | `{tenantId}/v1/workload/{workloadRtId}/chart-version` body `{ "chartVersion": "1.2.3" }` | Validates the version is a non-empty SemVer (`MAJOR.MINOR.PATCH[-prerelease][+build]`), updates the entity through `ICommunicationRepository.UpdateWorkloadChartVersionAsync`, and writes an `Information` event via `ICommunicationEventService` with the source tag `(source: CI/CD)`. |

Auth: read-only / read-write tenant communication policies (same as
`PoolController`).

**Deploy is intentionally a separate call.** `WorkloadController` writes the
chart version into MongoDB but never triggers a Helm rollout. The CI pipeline
calls `POST /pool/workloads/deploy?workloadRtId=…` (existing) after the PATCH
when it wants the operator to pick up the change. Splitting the two lets
operators stage version writes across many tenants before flipping the deploy
switch.

**Repository surface** (`ICommunicationRepository`):
- `GetWorkloadsByChartNameAsync(tenantId, chartName)` — polymorphic
  `RtDeployableWorkload` lookup by `ChartName` field filter.
- `UpdateWorkloadChartVersionAsync(tenantId, workloadRtId, newChartVersion)`
  — load-mutate-replace; returns the previous version so the audit event
  can include the before/after.

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

## Pipeline Reassignment (`PATCH /pipeline/move-to-adapter`)

When a new adapter (e.g. one provisioned from a fresh Blueprint) replaces
an older adapter, the existing `RtPipeline` entities can be re-pointed at
the new adapter without recreating them. `PipelineController.MovePipelinesToAdapter`
takes a bulk list of pipeline ids plus a target adapter id and walks each
one through `ICommunicationRepository.MovePipelineToAdapterAsync`.

Repository semantics (`MovePipelineToAdapterAsync`):
- Atomic per pipeline: in a single transaction, the outbound `Pipeline.Executes`
  edge to the source adapter is deleted and the equivalent edge to the
  target adapter is inserted via `AssociationUpdateInfo.CreateDelete` /
  `CreateInsert` + `ApplyChangesAsync`.
- **Source and target adapter must have the exact same `CkTypeId`.** A
  pipeline only knows how to run nodes its adapter implementation
  understands, so moving a pipeline onto an adapter of a different concrete
  subtype is rejected (`AdapterTypeMismatchForMove`). If you want a wider
  policy (allow any `Adapter`-derived subtype), loosen the equality check
  here — but mind that the operator's helm path will not magically
  reinterpret incompatible node configurations.
- Refuses to move a pipeline that currently has no `Executes` edge
  (`PipelineHasNoAdapter`) — moving an unassigned pipeline would silently
  assign it, which is too easy to misuse.
- No-op when the pipeline already points at the target — returns success
  with old==new so the caller does not need to special-case it.

Controller wraps the repository for bulk + best-effort redeploy:
- One repository call per pipeline; per-pipeline failures collected and
  reported as `MovePipelineResultDto { Success=false, ErrorMessage=... }`
  without aborting the rest of the batch.
- When `Redeploy=true` and the pipeline actually moved (i.e. not a
  no-op), the controller fires `IAdapterService.DeployPipelineAsync` on
  the new adapter immediately afterwards. A redeploy failure does **not**
  roll the move back — the pipeline already points at the new adapter
  and the operator can hit "Deploy" manually once the adapter is back
  online. The redeploy error surfaces in `ErrorMessage` as a warning
  while `Success` stays `true`.
- Writes one audit event per pipeline via `ICommunicationEventService`
  with the source tag `(source: User)`.

Tests:
- `Controllers/PipelineControllerMoveToAdapterTests` (TUnit) — empty
  list, invalid target id, single happy path, redeploy success,
  redeploy failure (warning path), no-op when already on target,
  repository exception, mixed bulk batch.

## Pipeline Debug Toggle (`PATCH`/`GET /pipeline/{id}/debug`)

`PipelineController` exposes explicit control of per-pipeline debug capture
(the `RtPipeline.IsDebuggingEnabled` flag), used by `octo-cli`
(`SetPipelineDebug` / `GetPipelineDebug`).

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `PATCH` | `{tenantId}/v1/pipeline/{pipelineRtId}/debug` body `{ "enabled": true|false }` | read-write | Persists `IsDebuggingEnabled` and, when the owning adapter is online, re-pushes config so it takes effect immediately. Returns `SetPipelineDebugResultDto { Enabled, AppliedToRunningAdapter }`. |
| `GET`   | `{tenantId}/v1/pipeline/{pipelineRtId}/debug` | read-only | Returns the persisted `PipelineDebugStateDto { Enabled }`. |

**Implementation:** `IAdapterService.SetPipelineDebuggingAsync` persists via
`ICommunicationRepository.SetPipelineDebuggingEnabledAsync`, then re-pushes the
owning data flow with `DeployDataFlowAsync` (the **non-force-enable** path —
`DeployPipelineAsync` force-enables debug, so it is deliberately NOT used here).
If the adapter is offline, `DeployDataFlowAsync` throws `AdapterServiceException`
(`AdapterNotLoaded`); the service swallows it, leaving the flag persisted
(`AppliedToRunningAdapter = false`, applies on next deploy). An `Information`
audit event tagged `(source: User)` is written per toggle.

**Why deploy still force-enables debug:** Refinery Studio's editor "Deploy" and
"Enable Debug" buttons rely on `POST /pipeline/deploy` force-enabling debug, and
its "Disable Debug" uses a GraphQL `isDebuggingEnabled=false` write + a
`POST /dataflow/deploy` (no force-enable). This feature mirrors the disable path
and does not change deploy behavior, so the UI is unaffected.

Tests: `Services/AdapterServiceTests/SetPipelineDebuggingAsyncTests` (online
enable/disable, offline persist-only, not-found paths) and
`Controllers/PipelineControllerDebugTests`.

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
