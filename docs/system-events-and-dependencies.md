---
description: Multi-tenancy, external dependencies and the system events the controller publishes and consumes
applies_to: **/Consumers/**, **/Services/CommunicationEventService.cs
---

### Multi-Tenancy

All operations are tenant-scoped. Routes use a custom `tenantId` constraint. The MongoDB-backed repository supports per-tenant data isolation via `ISystemContext.FindTenantRepositoryAsync(tenantId)`.


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
| AdapterService | Deprecated node used | Warning | Deployed pipeline uses a node type flagged `IsDeprecated` in the adapter's node descriptors (one event per deprecated node type, associated with the pipeline entity) |
| AdapterService | Tenant pre/post-update | Information | Tenant lifecycle events |
| DeploymentSiteService | Pool operator registered/unregistered | Information | Pool operator connection state |
| DeploymentSiteService | Adapter deployed/undeployed to pool | Information | Adapter assignment to pools |
| DeploymentSiteService | Tenant pre/post-update | Information | Tenant lifecycle events |
| TriggerManagementService | Pipeline execution started | Information | Manual pipeline trigger |
| TriggerManagementService | Pipeline execution failed | Error | Pipeline execution errors |
| TriggerManagementService | Trigger schedule updated | Information | Scheduled triggers updated |
| PipelineExecutionService | Pipeline execution failed | Error | Adapter reports failed execution |
| PipelineExecutionService | Pipeline execution cancelled | Information | Adapter reports cancelled execution |
| PipelineExecutionService | Old executions cleaned up | Information | Retention cleanup completed |
| TenantManagementConsumer | Tenant update failed | Error | Errors during tenant lifecycle |
| TenantManagementConsumer | CK model change notification failed | Error | Adapter CK-cache flush broadcast failed (AB#4456) |
| Hubs | Operation failed | Error | Hub operation errors |
| SignalChannelService | Registration started / verified / adopted / deleted | Information | Signal channel self-service transitions (AB#5143), incl. adopting an account the bridge already holds |
| SignalChannelService | Registration / verification / deletion failed | Error | Bridge rejected a register or verify call, or the repository delete failed |
| NotificationScheduleService | Notification subscription deployed / undeployed | Information | `Notification subscription '<name>' deployed.` / `... undeployed.`, linked to the subscription; the rtId stands in for a missing name. Deploy records it right after `Enabled=true` is written, before the schedule and engine steps, so a deploy that fails half-way still shows (AB#2585) |

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

**Communication-state write retry (AB#6435).** `SetDeploymentSiteCommunicationStateAsync` and
`SetAdapterCommunicationStateAsync` run through `StateWriteRetry.ExecuteAsync`: when MongoDB aborts the
transaction with a `TransientTransactionError` label or a `WriteConflict` (code 112) — found anywhere in the
exception chain — the whole write (new session and transaction) is repeated, at most 2 times, after a
jittered 50-200 ms backoff, with a warning per retry. The logical write timestamp (entity + `AttributeNewerThanGuard`)
is taken once before the first attempt and reused by every retry, so a retried stale write cannot overwrite a newer
state. The last exception surfaces unchanged; every other
error is not retried. Seen on test-2 (tenant wwc26, 2026-10-10) where a lost Online write healed only when
the operator re-claimed the site 10 s later.
Since AB#9121 `SetPipelineDeploymentStateAsync` and `SetPipelineTriggerDeploymentStateAsync` use the same retry: on
prod-2 (tenant sbeg, 2026-11-15) tenant setup set a trigger to `Pending` while the adapter re-registered and redeployed
22 pipelines, and the unretried WriteConflict failed the whole tenant setup. These writes carry no timestamp guard, so
a retry is last-writer-wins exactly like the first attempt would have been. Other writes (adapter, application and
pool deployment state, configuration state) are not covered.
