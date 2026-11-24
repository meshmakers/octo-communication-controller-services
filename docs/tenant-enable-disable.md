---
description: Disable refusal while pools or workloads are deployed, enable and disable authorization, retry of failed tenant setup
applies_to: **/Consumers/TenantManagementConsumer.cs, **/Services/AdapterService.cs
---

## Disable Refuses While Pools or Workloads Are Deployed (AB#4255) — or AI Services Is Enabled (AB#4884)

`POST {tenantId}/v1/communication/disable` answers **409** with an `OperationFailedErrorDto` while any
Pool, Adapter or Application of the tenant has a `DeploymentState` other than `Undeployed` /
`Disabled` (`ActiveDeployment.IsActive`: Deployed, Pending and Error all own operator resources —
see the recompute comment in `DeploymentSiteService.RecomputeAllDeploymentStatesAsync`). The body names every
resource as `Kind 'Name' (State)` plus the undeploy verbs (`UndeployWorkload`, `UndeployPool`,
Studio). Every other `ConfigurationException` stays a 400. The tenant delete/detach guard in the
asset repository (AB#4255 step 1) only reads the enabled flag, so this is the check that keeps a
deleted tenant from leaving `DeploymentSite` CRs and helm releases behind.

**The AI Services flag blocks too (AB#4884).** `EnableAi` refuses while Communication is disabled
(the AI service depends on System.Communication), so the reverse holds as well: the blocker hook
additionally reads the tenant's `TenantCapabilityConfigurationKeys.AiServices` flag (from the
tenant's own configuration store, exactly as the delete/detach guard does — missing key or `false`
= disabled) and refuses with a `BuildAiDisableBlockedMessage` naming `DisableAi` and the Studio
Tenant Features panel. Without it, disabling Communication under a still-enabled AI Services left
the tenant in a state `EnableAi` could never have produced, which only the delete/detach guard
surfaced later. Both blockers can be present; their messages are joined. A flag read failure
propagates — an unreadable state must never look torn down.

Mechanics:

- `DefaultConfigurationCreatorService.GetDisableBlockerAsync` overrides the octo-common-services hook
  (consulted after the already-disabled check, before the flag is removed; a refusal keeps the flag and
  skips `StopTenantAsync`) and builds the message with `BuildDisableBlockedMessage` (pinned by
  `GetDisableBlockerAsyncTests.BuildDisableBlockedMessage_IsTheOperatorContract`).
- `IDeploymentSiteService.GetActiveDeploymentsAsync` reads the **repository** (`GetDeploymentSitesAsync` +
  `GetWorkloadsAsync`, the latter a polymorphic `GetRtEntitiesByTypeAsync<RtDeployableWorkload>`),
  NOT the `OperatorConnectionManager` tracking maps: this is a user request on a live tenant (no race
  with the PreDeleteTenant cache unload), and the persisted state is what the operator mirrors back
  through the reverse-sync, survives controller restarts and shows Cloud→Edge leftovers. A read
  failure propagates — an unreadable tenant must never look torn down.
- **Deliberately not in the guard:** pipelines (no cluster resource — their registration lives in the
  adapter's memory; disconnect/helm-uninstall leaves them `Pending`, and `UndeployDataFlowAsync` throws
  `AdapterNotLoaded` once the adapter is gone, so a pipeline-aware guard could not be remediated) and
  triggers (`StopTenantAsync` removes their schedule itself; `Pending` is their normal post-disable
  state, see `TriggerManagementService.RemoveScheduleAsync`).
- This is a **verified precondition, not a teardown**: `UndeployAllCloudPoolsAsync` stays wired to
  `PreDeleteTenant` only. Reasons: it reads the process-local tracking map, is Cloud-only, cannot stop
  self-hosted Edge adapters, and a flag flip that helm-uninstalls a production tenant's workloads would
  be a dangerous side effect of a harmless-looking verb.

**Deploy gate.** This service does not register the platform's `UseOctoTenants()` enabled-gate
middleware (it would 403 the `adapterHub` negotiate and every Studio Communication page of a disabled
tenant — the Studio derives navigation from CK-model presence, which Disable does not remove). After
a Disable the tenant API therefore stays callable. `DeploymentSiteController.DeployPoolAsync` and
`DeployWorkloadAsync` — the two endpoints that create operator-managed cluster resources — check
`IConfigurationService.IsEnabledAsync` themselves and answer 409 on a disabled tenant; undeploy stays
open so remediation always works. `DeployDataFlow` / `DeployTrigger` already fail on a disabled tenant
because the adapter cache is flushed (`AdapterServiceException.TenantNotEnabled`). Adding the
middleware gate is a follow-up decision, not part of AB#4255.

**Operator release must not resurrect a resting pool.** `UndeployPoolAsync` writes the resting state
(`Undeployed` / Edge `Disabled`) *before* it notifies the operator; the operator then removes the CR and
calls `UnregisterPoolAsync`, and `DeploymentSiteService.UnregisterPoolOperatorAsync` used to overwrite the resting
state with `Pending` ("no operator until one re-registers"). Every gracefully undeployed Cloud pool
therefore sat at `Pending` forever — invisible before, fatal for the guard (found in the local E2E with
the kind operator connected). The release now leaves a pool that already rests alone; only a
still-deployed pool that loses its operator flips to `Pending`
(`UnregisterPoolOperatorAsyncTests.UnregisterPoolOperatorAsync_RestingPool_KeepsItsDeploymentState`).

Tests: `Services/PoolServiceTests/GetActiveDeploymentsAsyncTests`,
`Services/DefaultConfigurationCreatorServiceTests/GetDisableBlockerAsyncTests`,
`Controllers/CommunicationControllerDisableTests`, `Controllers/PoolControllerDeployGateTests`,
`Services/PoolServiceTests/UnregisterPoolOperatorAsyncTests`, integration `Repository/GetWorkloadsAsyncTests`.


## Failed tenant setup is retried without MongoDB (AB#7249)

Staging-1, 2026-10-21: during a MongoDB primary election the setup of tenants `sbeg` and `eegroider` failed, the log
said "this tenant will be retried in the background", but this service wired no retry store, so nothing was ever
retried and the tenants' adapters stayed offline ("Tenant not enabled") until a manual pod restart. The shared
`TenantSetupRetryTracker` (octo-common-services, in-memory, process-wide) now queues every failed `SetupAsync` and
the 30 s `FailedTenantRetryBackgroundService` re-runs it with exponential backoff (30 s doubling to 5 min, never
giving up). `DefaultConfigurationCreatorService` takes the DI instance (`tenantSetupRetryTracker`) and passes it as
`setupRetryTracker:` to `DefaultConfigurationCreatorServiceStandardized`. Health check `TenantSetup` on `/health` turns
`Degraded` when a tenant fails longer than `TenantSetupRetry:DegradedAfter` (default 10 min); metrics
`octo.tenant_setup.failed` / `octo.tenant_setup.pending_seconds` (tag `octo.tenant.id`) feed the Dash0 alert. Details,
options and tests: octo-common-services `CLAUDE.md`, section "Tenant Setup - Failure Handling". Test here:
`tests/CommunicationControllerService.Tests/Services/DefaultConfigurationCreatorServiceTests/TenantSetupRetryWiringTests.cs`. Tests use one isolated `TenantSetupRetryTracker` per test and share it across the creator instances of that test when exercising a retry.

## Enable and disable need TenantManagement (AB#7319)

`POST {tenantId}/v1/communication/enable|disable` carry `Constants.TenantCommunicationCapabilityTogglePolicy`
(`CommunicationCapabilityToggleAuthorization`): scope `octo_api` plus the tenant role `TenantManagement`, defined by the
central `AddTenantRolePolicy` of octo-common-services (`AddOctoTenantRoleAuthorization()` in `Program.cs` registers the
handler). A token without the role gets 403 with a `{"message"}` body naming the role (AB#5227 shape). All other endpoints keep
their scope-only policies. Callers that need the role: octo-cli `EnableCommunication` / `DisableCommunication`, the MCP tools,
Studio's Tenant Features panel (tenant administrators). Test: `CommunicationControllerToggleAuthorizationTests`.
