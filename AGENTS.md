# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the **Octo Communication Controller Services** - an ASP.NET Core web service that manages communication
adapters and pools for data ingress and egress in an Octo Mesh instance. The service acts as a central hub for
coordinating communication between external adapters (data pipeline executors) and pools (device groups) in a
multi-tenant environment.

### Core Components

1. **Adapters**: External clients that execute data pipelines. They connect via SignalR and receive pipeline
   configurations from the service.
2. **Pools**: Groups of managed devices/entities. Pool operators register via SignalR to handle device communication.
3. **SignalR Hubs**: Real-time bidirectional communication channels:
   - `AdapterHub` at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
   - `AdapterPoolHub` at `/adapterPoolHub` - **tenant-free** management channel of adapter pool
     members; grants and releases leases. Not a variant of `AdapterHub`: a pool member
     belongs to no tenant, so it cannot use a tenant-addressed route.
   - `PoolHub` at `/{tenantId}/poolHub` - manages pool operator connections
Caches, the repository layer, the Construction Kit model, the project layout and the output directory:
`docs/service-conventions.md`. Source `src/CommunicationControllerServices/`, tests in
`tests/CommunicationControllerService.Tests/` (TUnit) and `tests/CommunicationControllerServices.IntegrationTests/`
(xUnit).

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
| `**/Services/ServiceAccount*Service.cs`, `**/BackgroundServices/PipelineServiceAccountProvisioningRetry*.cs` | `docs/pipeline-service-account-reconcile.md` |
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

```bash
dotnet restore Octo.CommunicationController.sln
dotnet build Octo.CommunicationController.sln --configuration Release
dotnet test --configuration Release   # unit (TUnit) + integration (xUnit), Microsoft.Testing.Platform runner
dotnet run --project src/CommunicationControllerServices/CommunicationControllerServices.csproj
```

TUnit filters, the `-p:BaseOutputPath` "no tests ran" trap, running the built assembly directly, the integration test
setup and the Docker image build: `docs/testing-and-integration-tests.md`. Building the CK model:
`docs/ck-model-build.md`.

## Before you commit

**MANDATORY for all code changes:**

1. **Unit Tests Required**: Every new feature, bug fix, or code change must include corresponding unit tests in
   `tests/CommunicationControllerService.Tests/`. Use TUnit framework with NSubstitute for mocking.

2. **Integration Tests Required**: Changes affecting repository operations, database interactions, or multi-component
   workflows must include integration tests in `tests/CommunicationControllerServices.IntegrationTests/`. Use xUnit
   framework with FluentAssertions.

3. **Documentation Updates Required**: This developer documentation (AGENTS.md) must be updated when:
   - Adding new architectural patterns or components
   - Introducing new services, repositories, or significant classes
   - Changing test infrastructure or fixtures
   - Adding new configuration options
   - Modifying API structures or authentication/authorization

4. **All Tests Must Pass**: Before completing any task, run both unit tests (`dotnet test`) and integration tests to
   ensure all tests pass.

## Rules

These rules bind new and changed code. Known exceptions in existing code are named in the linked docs.

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in docs and
  in new or changed code comments and test names (work item ids stay in `CHANGELOG.md` entries); remove an existing one
  when you touch its line.
- **A SignalR hub gets its own staged `IHubFilter` gate** with its own options section and `Mode` (`LogOnly` default,
  `Enforce`); REST policies never cover a hub. `/operatorHub` checks `SystemCommunicationApiPolicy`,
  `/{tenantId}/adapterHub` the read-write policy plus the route tenant, `/adapterPoolHub` binds the token's tenant; all
  three ship in `LogOnly`. `docs/hub-authorization-gates.md`; test `AdapterHubAuthorizationFilterTests`.
- **One configurator owns the JWT scheme:** `Program.cs` calls `AddJwtBearer()` without a delegate. Read subject, name,
  e-mail and roles only through `PrincipalClaimExtensions`, and build test principals in the mapped claim shape.
  `docs/tenant-authorization-gate.md`; tests `TenantAuthorizationWiringTests`, `PrincipalClaimExtensionsTests`.
- **A tenant route keeps the `{tenantId:tenantId}` constraint and gets no `IAllowParentTenantAdministration`;**
  user-token enforcement is staged in `Program.cs` (`LogOnly`), not flipped in code.
  `docs/tenant-authorization-gate.md`; test `TenantGateProgramPinTests`.
- **A CK method handler opens `CommunicationMethodCaller.Begin(context)` first**, a service reading `HttpContext.User`
  falls back to `CommunicationMethodCaller.Current`, a method declares `roles: [CommunicationManagement]`, and its
  cancellation is `ApplicationStopping`. Exception: REST callers without the role stay on the guarded direct path.
  `docs/ck-method-service-handlers.md`; test `DeployPipelineElevationGateTests`.
- **A reader of a caller-supplied pipeline definition calls `PipelineDefinitionLimits.CheckDefinition` before it
  parses.** `docs/ck-method-service-handlers.md`; test `PipelineDefinitionServiceLimitsTests`.
- **No client secret or token in a log line, result or `ToString`** (`LeaseDto` overrides it for that reason).
  `docs/adapter-pool-hub-gate-and-leases.md`; tests `LeaseSecretLogTargetTests`,
  `RotateServiceAccountSecretMethodRedactionTests`.
- **A lease takes the borrower's credential from the lease alone, read after both lending checks:** the pool's
  `SharingMode` and allow-list (`ITenantLendingScopeResolver`) and the adapter's own `LentFromTenantId`. A pool member
  registers only into a pool of its token's tenant. `docs/adapter-pool-hub-gate-and-leases.md`; test
  `LeaseServiceTests`.
- **A pipeline deploy path calls `EnsurePipelineHasServiceAccountAsync` before its first state write**, and ships with
  its provisioning. Exception: `DeploymentSiteService.DeployWorkloadAsync` is unguarded on purpose.
  `docs/pipeline-service-account-identity.md`; test `DeployPipelineServiceAccountGateTests`.
- **A workload deploy or undeploy notification for an operator-less pool is queued, never dropped**
  (`OperatorConnectionManager`), and a progress report writes `Pending` only, never `Deployed` or `Error`.
  `docs/helm-workload-deploy.md`; test `OperatorConnectionManagerTests`.
- **Endpoints that create operator-managed resources check `IConfigurationService.IsEnabledAsync`** and answer 409 on a
  disabled tenant; undeploy stays open and a failed state read propagates. Exception: the `UseOctoTenants()` gate is not
  registered here. `docs/tenant-enable-disable.md`; test `PoolControllerDeployGateTests`.
- **Notification scheduling decides when, never what:** a scheduling failure is stored as an error event and never stops
  tenant start. `docs/notification-subscriptions.md`; test `NotificationScheduleServiceTests`.
