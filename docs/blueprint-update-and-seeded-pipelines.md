---
description: Blueprint update against live adapter routes, Communication enablement and pushing blueprint-seeded pipelines
applies_to: **/Consumers/BlueprintAppliedConsumer.cs, **/Consumers/ComControllerAdapterUpdateConsumer.cs
---

### Blueprint update vs. the routes a live adapter serves

A blueprint update (`UpdateBlueprint`) or a force re-apply replaces `Pipeline` entities **in place**; `DeploymentState`
stays `Deployed`. The adapter builds the route of a `FromHttpRequest@2` trigger — path, `allowAnonymous`,
`requiredRoles` — when the controller pushes the pipeline, and keeps it until the next push. Nothing pushed after an
update, so the live route kept the authorization of the OLD definition: `EnergyCommunity.Base` 2.11.4 -> 2.12.0 left
`/manualMeteringEntry` open to any valid token of the tenant although the pipeline said
`requiredRoles: [EnergyCommunityManagement]`. An undeploy and deploy of the data flow fixed it by hand.

`BlueprintAppliedConsumer` (broadcast consumer, registered for `BlueprintApplied` **and** `BlueprintUpdated`) therefore
calls `IAdapterService.RefreshDeployedPipelinesAsync(tenantId)` after the service-account sweep (own `try`, a failed
sweep does not hold it back). For every adapter with a live connection it rebuilds the configuration of the pipelines
the adapter **already runs** from the repository and, if any differs from what was last pushed, pushes the result
through `UpdateAdapterConfigurationAsync` (same path as `DeployDataFlow`; the adapter's selective update re-registers
only the pipelines whose definition changed). Deliberate limits:

- a changed definition replaces the running one; what the repository holds as Deployed/Pending but the
  adapter does not run yet is added (see below);
- the adapter's own configuration string is kept: an update resets it to the seed (EDA credentials) and the operator
  restores it afterwards, a push in between would hand the live adapter the reset values;
- a pipeline the adapter runs that the repository no longer lists for it (deleted, disabled, no definition, moved to
  another adapter) is dropped from the pushed configuration, so its route stops answering; a pipeline that is only
  no longer Deployed/Pending (Undeployed, Error) is left to the deploy/undeploy paths;
- adapters without a connection (offline, leased) are skipped; they get the persisted state on registration or lease.

Tests: `Services/AdapterServiceTests/RefreshDeployedPipelinesAsyncTests`, `Consumers/BlueprintAppliedConsumerTests`.

### A blueprint that needs Communication enables it

`BlueprintAppliedConsumer` first asks `IBlueprintCapabilityReconciler` (octo-common-services,
`octo-common-services/docs/blueprint-capabilities.md`, registered in `Program.cs` with `AddBlueprintCapabilityReconciler`) for
`BlueprintCapability.Communication`, then runs the sweep and the refresh above. The reconciler enables Communication
(`IConfigurationService.EnableAsync`, the same path as `POST communication/enable`) only when the event reports CK model
dependencies that contain `System.Communication` or `System.Ai`, the tenant resolves (never the system tenant) and the
named blueprint is installed there in exactly that version; an event of an older asset repository (no dependencies)
enables nothing. It retries the tenant lookup because a tenant created with a blueprint publishes the event before the
registry commits, never disables, never throws, and logs every automatic enable at Information (only a failure to
enable is a warning or error). The step runs also for an event that
changed no entity, so a re-apply of a blueprint that still needs Communication enables it again after a manual disable
(decided). Enabling first means a tenant created with a starter blueprint gets its Mesh Adapter registered after the
seed, so registration hands it the seeded Deployed pipelines. The AI service enables AI from the same event after
Communication is on. Tests: `Consumers/BlueprintAppliedConsumerTests` (`Applied_AsksTheReconciler…`,
`EventThatChangedNoEntity_…`, `ReconcilerFailure_…`); the guard: `BlueprintCapabilityReconcilerTests` in octo-common-services.

### Blueprint-seeded pipelines are pushed

A blueprint seeds `Pipeline` entities with `DeploymentState: Deployed` (the install path writes entities, it never
deploys). `RegisterAdapterAsync` hands an adapter the pipelines persisted as Deployed/Pending *at registration*; the
Mesh Adapter registers when the tenant's communication is enabled, i.e. BEFORE the blueprint installs. The seeded
pipelines were therefore Deployed on paper and unknown to the adapter: their `FromHttpRequest` routes answered 404
until `DeployDataFlow` was run by hand (a customer blueprint: `register-metering-point`, `meter-readings`,
`community-balance`, ...). The same refresh as for a changed definition closes it: after `BlueprintApplied`/`BlueprintUpdated`
`RefreshDeployedPipelinesAsync` makes each connected adapter converge to the persisted Deployed/Pending set (add,
replace, drop). An adapter that (re)connects later still gets that set from registration. Pipelines in other states
(Undeployed, Error) are never added.
