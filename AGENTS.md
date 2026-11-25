# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the **Octo Communication Controller Services** - an ASP.NET Core web service that manages communication
adapters and pools for data ingress and egress in a multi-tenant Octo Mesh instance.

### Core Components

1. **Adapters**: External clients that execute data pipelines. They connect via SignalR and receive pipeline
   configurations from the service.
2. **Deployment sites**: Places where workloads run. The operator of a site connects via SignalR and receives workload
   deploy and undeploy notifications.
3. **SignalR Hubs**: Real-time bidirectional communication channels:
   - `AdapterHub` at `/{tenantId}/adapterHub` - manages adapter lifecycle and pipeline debugging
   - `AdapterPoolHub` at `/adapterPoolHub` - **tenant-free** management channel of adapter pool
     members; grants and releases leases. Not a variant of `AdapterHub`: a pool member
     belongs to no tenant, so it cannot use a tenant-addressed route.
   - `OperatorHub` at `/operatorHub` - operator connections and workload deploy

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
| `**/Methods/Service/**`, `**/TenantApi/v1/Controllers/**`, `**/Services/PipelineDefinition*.cs` | `docs/ck-method-service-handlers.md` |
| `src/SystemCommunicationCkModel/ConstructionKit/**` | `docs/ck-model-build.md` |
| `**/Services/Workload*.cs`, `**/Hubs/OperatorHub*.cs`, `**/Controllers/WorkloadController.cs`, `**/DeploymentSite*.cs` | `docs/helm-workload-deploy.md` |
| `**/Hubs/**`, `**/Options/*HubAuthorizationOptions.cs` | `docs/hub-authorization-gates.md` |
| `**/Services/AdapterPool*.cs`, `**/Controllers/AdapterPoolController.cs` | `docs/leasing-adapter-pool.md` |
| `src/SystemCommunicationCkModel/ConstructionKit/migrations/**`, `**/Services/Lease*.cs` | `docs/leasing-ck-model-4-migration.md` |
| `**/Services/AdapterLeasingMetrics.cs` | `docs/leasing-metrics-and-operability.md` |
| `**/Services/LeaseScheduler*.cs`, `**/BackgroundServices/LeaseScheduler*.cs` | `docs/leasing-pool-queue-and-scheduler.md` |
| `**/Services/LeaseService.cs`, `**/Repository/Queued*.cs`, `**/Repository/ExecutionLeaseClaimStore.cs`, `**/Consumers/LeaseTriggerConsumer.cs` | `docs/leasing-queued-executions.md` |
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
| `**/Caches/**`, `**/Program.cs`, `**/Services/*ServiceException.cs`, `**/Repository/CommunicationRepository.cs` | `docs/service-conventions.md` |
| `src/SystemCommunicationCkModel/Blueprints/**`, `**/Consumers/BlueprintAppliedConsumer.cs` | `docs/service-managed-blueprints.md` |
| `**/SignalChannel*.cs`, `**/SignalBridge*.cs` | `docs/signal-channel-self-service.md` |
| `**/Consumers/**`, `**/Services/CommunicationEventService.cs` | `docs/system-events-and-dependencies.md` |
| `**/Configuration/ConfigureJwtBearerOptions.cs`, `**/Controllers/PrincipalClaimExtensions.cs` | `docs/tenant-authorization-gate.md` |
| `**/Consumers/TenantManagementConsumer.cs`, `**/Services/AdapterService.cs`, `**/Methods/Service/DeploymentMethodHandlers.cs` | `docs/tenant-enable-disable.md` |
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

1. **Unit tests** (TUnit, NSubstitute) for every change, in `tests/CommunicationControllerService.Tests/`.
2. **Integration tests** (xUnit, FluentAssertions) for repository, database or multi-component changes, in
   `tests/CommunicationControllerServices.IntegrationTests/`.
3. **Documentation**: update AGENTS.md and the matching `docs/` file when you add a pattern, service, repository or
   significant class, or change test infrastructure, configuration, API structure or authentication.
4. **All tests pass**: run unit and integration tests (`dotnet test`) before you finish.

## Rules

These rules bind new and changed code. Known exceptions in existing code are named in the linked docs.

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in docs,
  new or changed code comments and test names (ids stay in `CHANGELOG.md`); remove an existing one when you touch it.
- **A SignalR hub gets its own staged `IHubFilter` gate** with its own options section and `Mode` defaulting to
  `LogOnly`; `Enforce` is switched by configuration, never in code, and REST policies never cover a hub.
  `docs/hub-authorization-gates.md`; tests `AdapterHubAuthorizationFilterTests`,
  `AdapterHubAuthorizationWiringTests`, `AdapterPoolHubWiringTests` (likewise `OperatorHub*`).
- **One configurator owns the JWT scheme** (`AddJwtBearer()` without a delegate). A new caller read uses
  `PrincipalClaimExtensions` (roles: `PrincipalRoleExtensions.HasRole`; hubs: `HubConnectionPrincipal`). A tenant route
  keeps `{tenantId:tenantId}` and gets no `IAllowParentTenantAdministration`. User-token enforcement stays `LogOnly` in
  `Program.cs` and is flipped by configuration. Known raw reads: `SignalChannelController`, `CommunicationMethodFacade`.
  `docs/tenant-authorization-gate.md`; tests `TenantAuthorizationWiringTests`, `PrincipalClaimExtensionsTests`,
  `TenantGateProgramPinTests`.
- **A CK method handler opens `CommunicationMethodCaller.Begin(context)` first**, a service reading `HttpContext.User`
  falls back to `CommunicationMethodCaller.Current`, a method declares `roles: [CommunicationManagement]`, and its
  cancellation is `ApplicationStopping`. Exception: REST callers without the role stay on the guarded direct path.
  `docs/ck-method-service-handlers.md`; tests `CommunicationMethodHostTests` (roles), `DeployPipelineElevationGateTests`
  (caller fallback).
- **A reader of a caller-supplied pipeline definition calls `PipelineDefinitionLimits.CheckDefinition` before it
  parses.** `docs/ck-method-service-handlers.md`; test `PipelineDefinitionServiceLimitsTests`.
- **No client secret or token in a log line, a REST or method result, an event or a `ToString`** (`LeaseDto` overrides
  it). Exception: the credential reaches the workload that uses it - through the lease to the member, the Helm secret
  value and the adapter's pipeline configuration. `docs/adapter-pool-hub-gate-and-leases.md`; tests
  `LeaseSecretLogTargetTests`, `RotateServiceAccountSecretMethodRedactionTests`.
- **A lease takes the borrower's credential from the lease alone, read after both lending checks:** the pool's
  `SharingMode` and allow-list (`ITenantLendingScopeResolver`) and the adapter's own `LentFrom` edge to its
  `LentAdapterPool` mirror (`LentFromReference.FromMirror`).
  `docs/adapter-pool-hub-gate-and-leases.md`; tests in `Services/LeaseServiceTests/` (`GrantLeaseAsyncTests`).
- **A pipeline deploy path calls `EnsurePipelineHasServiceAccountAsync` before its first state write.** Exception:
  `DeploymentSiteService.DeployWorkloadAsync` provisions the account instead of gating (the pod must exist first).
  `docs/pipeline-service-account-identity.md`; test `DeployPipelineServiceAccountGateTests`.
- **A workload deploy or undeploy notification for a deployment site without an operator is queued in memory and
  replayed on registration, never dropped while the controller runs; a progress report writes `Pending` only.**
  `docs/helm-workload-deploy.md`; tests `OperatorConnectionManagerTests`, `ReportWorkloadDeploymentProgressAsyncTests`.
- **An endpoint or method that creates operator-managed resources checks `CommunicationEnabledGate.RefusalAsync` and
  answers 409 on a disabled tenant; undeploy stays open.** Exception: `UseOctoTenants()` is not registered here.
  `docs/tenant-enable-disable.md`; test `PoolControllerDeployGateTests`.
- **Notification scheduling decides when, never what:** a scheduling failure is stored as an error event and never stops
  tenant start. `docs/notification-subscriptions.md`; test `NotificationScheduleServiceTests`.
- **A change to the System.Communication CK model bumps its `ckModel.yaml` version per the engine's semver rules, and
  every attribute declares `ownership`** (a Secret attribute `Secret` only). `docs/ck-model-build.md`.
- **Every hub send takes its method name from `nameof(I...HubCallbacks.X)`, and a new callback interface is added to
  `CallbackInterfaces`.** `docs/adapter-pool-hub-gate-and-leases.md`; test `HubCallbackContractTests`.
