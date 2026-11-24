# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the **Octo Communication Controller Services** - an ASP.NET Core web service that manages communication adapters and pools for data ingress and egress in an Octo Mesh instance. The service acts as a central hub for coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a multi-tenant environment.

### Core Components

1. **Adapters**: External clients that execute data pipelines. They connect via SignalR and receive pipeline configurations from the service.
2. **Pools**: Groups of managed devices/entities. Pool operators register via SignalR to handle device communication.
3. **SignalR Hubs**: Real-time bidirectional communication channels:
   - `AdapterHub` at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
   - `AdapterPoolHub` at `/adapterPoolHub` - **tenant-free** management channel of adapter pool
     members; grants and releases leases (AB#4924). Not a variant of `AdapterHub`: a pool member
     belongs to no tenant, so it cannot use a tenant-addressed route.
   - `PoolHub` at `/{tenantId}/poolHub` - manages pool operator connections
4. **Caches**: In-memory synchronized state for Adapters and Pools across service nodes
5. **Repository Layer**: `CommunicationRepository` - abstracts MongoDB persistence via Octo Runtime Engine
6. **Construction Kit Model**: YAML-based model definitions in `SystemCommunicationCkModel` that generate C# types

### Service Architecture

The service follows a layered architecture:
- **Hubs Layer** (`src/CommunicationControllerServices/Hubs/`) - SignalR hubs for real-time communication
- **Service Layer** (`src/CommunicationControllerServices/Services/`) - Core business logic (`AdapterService`, `DeploymentSiteService`, `PipelineDebugService`, `TriggerManagementService`, `PipelineExecutionService`, `CommunicationEventService`)
- **Repository Layer** (`src/CommunicationControllerServices/Repository/`) - Data access via MongoDB Runtime Engine
- **Cache Layer** (`src/CommunicationControllerServices/Caches/`) - In-memory state synchronized across nodes via hub callbacks
- **Consumers** (`src/CommunicationControllerServices/Consumers/`) - Message bus event consumers for tenant lifecycle management
- **Background Services** (`src/CommunicationControllerServices/BackgroundServices/`) - Periodic maintenance tasks

### Project Structure Notes

- Main service: `src/CommunicationControllerServices/`
- Construction Kit model: `src/SystemCommunicationCkModel/`
- Resource strings: `src/CommunicationControllerServices.Resources/`
- Unit tests (TUnit): `tests/CommunicationControllerService.Tests/`
- Integration tests (xUnit): `tests/CommunicationControllerServices.IntegrationTests/`
- CI/CD: `azure-pipelines.yml` (root; uses shared `octo-pipeline-templates`)
- Output directory: `bin/$(Configuration)/`

### Configuration Notes

- `InternalsVisibleTo` configured for tests and DynamicProxy (for NSubstitute proxy generation)
- Target framework: .NET 10.0
- NLog configuration in `src/CommunicationControllerServices/nlog.config`
- SignalR configured with 100MB max message size
- Three build configurations: `Debug`, `Release`, `DebugL` (local development with version 999.0.0)

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `**/Hubs/AdapterHub*.cs`, `**/Caches/Adapters/**`, `**/Services/AdapterConnectionTracker.cs` | `docs/adapter-connection-lifecycle.md` |
| `**/Hubs/AdapterPool*.cs`, `**/Services/Lease*.cs` | `docs/adapter-pool-hub-gate-and-leases.md` |
| `**/Consumers/BlueprintAppliedConsumer.cs`, `**/Consumers/ComControllerAdapterUpdateConsumer.cs` | `docs/blueprint-update-and-seeded-pipelines.md` |
| `**/Methods/**` | `docs/ck-method-pipeline-handler.md` |
| `**/Methods/Service/**`, `**/TenantApi/v1/Controllers/**` | `docs/ck-method-service-handlers.md` |
| `src/SystemCommunicationCkModel/ConstructionKit/**` | `docs/ck-model-build.md` |
| `**/Services/Workload*.cs`, `**/Hubs/OperatorHub*.cs`, `**/Controllers/WorkloadController.cs` | `docs/helm-workload-deploy.md` |
| `**/Hubs/**`, `**/Options/*HubAuthorizationOptions.cs` | `docs/hub-authorization-gates.md` |
| `**/Services/AdapterPool*.cs`, `**/Controllers/AdapterPoolController.cs` | `docs/leasing-adapter-pool.md` |
| `src/SystemCommunicationCkModel/ConstructionKit/migrations/**`, `**/Services/Lease*.cs` | `docs/leasing-ck-model-4-migration.md` |
| `**/Services/AdapterLeasingMetrics.cs` | `docs/leasing-metrics-and-operability.md` |
| `**/Services/LeaseScheduler*.cs`, `**/BackgroundServices/LeaseScheduler*.cs`, `**/Repository/ExecutionLeaseClaimStore.cs` | `docs/leasing-pool-queue-and-scheduler.md` |
| `**/Services/LeaseService.cs`, `**/Repository/Queued*.cs`, `**/Consumers/LeaseTriggerConsumer.cs` | `docs/leasing-queued-executions.md` |
| `**/Methods/PipelineMethodInvoker.cs`, `**/Services/PipelineExecutionService.cs` | `docs/leasing-queued-invoker.md` |
| `**/Services/TenantLendingScopeResolver.cs`, `**/Services/PipelineExecutionClassService.cs`, `**/Services/AdapterNodeCapabilityService.cs` | `docs/leasing-scope-and-capabilities.md` |
| `**/Services/*PoolMirror*.cs`, `**/Services/Lent*.cs` | `docs/lent-adapter-pool-mirrors.md` |
| `**/Notification*.cs` | `docs/notification-subscriptions.md` |
| `**/Services/Workload*Metrics.cs`, `**/Services/WorkloadObservabilityOptIn.cs`, `**/BackgroundServices/WorkloadStateMetrics*.cs` | `docs/observability-switch-and-workload-metrics.md` |
| `**/BackgroundServices/*OfflineReconciliation*.cs` | `docs/offline-reconciliation.md` |
| `**/Services/*Lifecycle*.cs`, `**/BackgroundServices/WorkloadLifecycleWatchdog*.cs`, `**/Middleware/**`, `**/Consumers/LifecycleWakeConsumer.cs` | `docs/on-demand-adapter-lifecycle.md` |
| `**/Controllers/PipelineController.cs`, `**/Controllers/PipelineDebugController.cs`, `**/Services/PipelineDebugService.cs` | `docs/pipeline-endpoints.md` |
| `**/Services/PipelineExecution*.cs`, `**/BackgroundServices/ExecutionCleanup*.cs`, `**/Services/PipelineStatisticsFolder.cs` | `docs/pipeline-execution-metrics.md` |
| `**/Services/ServiceAccountImpersonationSwitchService.cs`, `**/Services/UsesLinkedServiceAccountLookup.cs`, `**/Controllers/ServiceAccountController.cs` | `docs/pipeline-service-account-grants.md` |
| `**/Services/PipelineServiceAccount*.cs`, `**/Options/ServiceAccount*.cs` | `docs/pipeline-service-account-identity.md` |
| `**/Services/ServiceAccountHealthService.cs`, `**/Services/ServiceAccountRightsAnalysisService.cs`, `**/BackgroundServices/PipelineServiceAccountProvisioningRetry*.cs` | `docs/pipeline-service-account-reconcile.md` |
| `**/Methods/Service/**` | `docs/rest-method-facades.md` |
| `**/Services/SecretDefault*.cs`, `**/Controllers/SecretDefaultsController.cs` | `docs/secret-defaults.md` |
| `**/Caches/**`, `**/Program.cs`, `**/Services/*ServiceException.cs` | `docs/service-conventions.md` |
| `src/SystemCommunicationCkModel/Blueprints/**`, `**/Consumers/BlueprintAppliedConsumer.cs` | `docs/service-managed-blueprints.md` |
| `**/SignalChannel*.cs`, `**/SignalBridge*.cs` | `docs/signal-channel-self-service.md` |
| `**/Consumers/**`, `**/Services/CommunicationEventService.cs` | `docs/system-events-and-dependencies.md` |
| `**/Configuration/ConfigureJwtBearerOptions.cs`, `**/Controllers/PrincipalClaimExtensions.cs` | `docs/tenant-authorization-gate.md` |
| `**/Consumers/TenantManagementConsumer.cs`, `**/Services/AdapterService.cs` | `docs/tenant-enable-disable.md` |
| `tests/CommunicationControllerServices.IntegrationTests/Fixtures/**`, `azure-pipelines.yml` | `docs/testing-and-integration-tests.md` |
| `**/Services/WorkloadTemplateResolver.cs`, `**/Services/WorkloadResource*.cs` | `docs/workload-template-resolution.md` |
<!-- <<< end generated: routing -->

## Build & test

### Build & Test

```bash
# Restore packages
dotnet restore Octo.CommunicationController.sln

# Build solution
dotnet build Octo.CommunicationController.sln --configuration Release

# Run all tests (unit + integration). global.json selects the Microsoft.Testing.Platform
# runner, so VSTest-only switches (--logger, --collect) are not available.
dotnet test --configuration Release

# Run a specific unit test class. TUnit only understands the MTP tree-node filter
# (/assembly/namespace/class/method, '*' wildcards); a VSTest --filter selects zero tests.
dotnet test --project tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj --treenode-filter "/*/*/ReportAdapterMetricsAsyncTests/*"

# Run a specific unit test method
dotnet test --project tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj --treenode-filter "/*/*/ReportAdapterMetricsAsyncTests/ReportAdapterMetricsAsync_ServiceThrows_Swallowed"
```

The xUnit integration tests additionally accept `--filter` (VSTest syntax) and
`--filter-class` / `--filter-method`; see "Running Integration Tests" below.

🔴 **`dotnet test` and `-p:BaseOutputPath` do not mix here — it reports "no tests ran", exit code 5,
not an error.** Both test projects land in the same `<base>/DebugL/net10.0` folder, and the MTP
runner then discovers nothing at all: no filter, still zero tests, green-looking log. It is a false
negative in the direction that hides work. Build with the scratch output path and **run the built
assembly directly** instead:

```bash
dotnet build tests/CommunicationControllerService.Tests/CommunicationControllerService.Tests.csproj \
  -c DebugL -p:BaseOutputPath=$SCRATCH/bo-unit/ -nodeReuse:false
cd $SCRATCH/bo-unit/DebugL/net10.0
dotnet Meshmakers.Octo.Backend.CommunicationControllerService.Tests.dll --treenode-filter "/*/*/FairnessTests/*"

# the xUnit integration assembly takes -class / -method, NOT --filter
dotnet CommControllerServices.IntegrationTests.dll -class "*AdapterPoolQueueTests"
```

Give each test project its **own** `BaseOutputPath` (`bo-unit`, `bo-int`) — sharing one flattens two
different runners' assemblies into one directory.

### Run the Service

```bash
# Run locally (uses appsettings.Development.json)
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj

# Run with specific configuration
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj --configuration Debug
```

Integration test setup: `docs/testing-and-integration-tests.md`. Building the CK model: `docs/ck-model-build.md`.

### Docker

```bash
# Build Docker image
docker build -f src/CommunicationControllerServices/Dockerfile -t octo-communication-controller .
```

## Before you commit

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

## Rules

The rules of this repository are the sections of the routed docs; read the matching file before you change code it covers.
