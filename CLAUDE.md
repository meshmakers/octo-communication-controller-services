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

#### The SignalR hubs are not covered by any of that (AB#5059, AB#5063)

`app.MapHub<AdapterHub>` / `app.MapHub<OperatorHub>` carry **no** `RequireAuthorization()`, neither hub
class has an `[Authorize]` attribute, and the service registers no `FallbackPolicy`. The policies above
apply to the controller routes only. Both gaps are now closed by **staged** `IHubFilter` gates —
`/operatorHub` (the tenant-crossing control plane where an operator claims pools and acknowledges
workload deploy / scale outcomes) under AB#5059, `/{tenantId}/adapterHub` (the adapter data plane)
under AB#5063. Same shape, one mode switch each, both defaulting to `LogOnly`.

`Hubs/OperatorHubAuthorizationFilter` is an `IHubFilter` registered per hub via
`AddSignalR().AddHubOptions<OperatorHub>(o => o.AddFilter<OperatorHubAuthorizationFilter>())`. On every
connection it evaluates **`Constants.SystemCommunicationApiPolicy`** — deliberately the policy the
service's own `system/v{version}` routes already use, not a new one: the hub is not tenant-scoped and
everything it does is a system-level write.

| `OperatorHubAuthorization:Mode` | Behaviour |
|---|---|
| `LogOnly` (**default**, and the enum's zero value) | Connection outcomes identical to no gate at all, but every connection an enforcing run would refuse is logged as a warning naming connection id, `client_id`, `sub` and scopes. That log **is** the consumer inventory. |
| `Enforce` | A connection that does not satisfy the policy is refused with a `HubException`. |

🔴 **It shipped on `LogOnly` because the operator had no credential — a precondition outside this
repo, not a knob to flip.** When AB#5059 was written, the connection was built by
`SignalRClient.CreateHubConnection` in **octo-sdk** (`src/Sdk.ServiceClient/SignalRClient.cs`), which
contained a literal `options.Headers["Authorization"] = "Bearer your-access-token"` under a
`// TODO: Handle authentication`, and **octo-communication-operator**'s `OperatorHubClientFactory`
handed the client a freshly constructed, never-populated `ServiceClientAccessToken`. Arming `Enforce`
then would have 401'd **every** operator in the estate, central and edge alike.

**That has since changed (AB#5062, octo-sdk `1197b9a`).** The placeholder header is gone; the SDK now
sets `options.AccessTokenProvider = () => ClientAccessToken.AccessToken` (returning `null` for a blank
token, i.e. sending *no* credential rather than a malformed one), and the operator got an
`OperatorAccessTokenService` that performs a client-credentials login and fills the holder. Whether
`/operatorHub` can now be armed is AB#5062's question — read an environment's `LogOnly` inventory
before answering it. Bind it per environment with `OCTO_OPERATORHUBAUTHORIZATION__MODE=Enforce`; no
release needed. The staging shape is the same one `TenantAuthorizationOptions` uses (AB#5032 /
AB#5054) on purpose.

`ConfigureJwtBearerOptions` also gained `OnMessageReceived`, which accepts the token from
`?access_token=` **on the hub paths only** (`/operatorHub`, `/{tenantId}/adapterHub`, and their
`/negotiate` sub-paths). SignalR cannot send an `Authorization` header on the WebSocket / SSE
transports, so this is the documented server-side counterpart; without it the gate would read every
standards-compliant client as anonymous and produce a clean-looking but worthless inventory. It is
narrowed to the hub paths so a token can never be smuggled into a REST route as a query parameter,
where it would land in access and proxy logs.

Tests: `tests/CommunicationControllerService.Tests/Hubs/OperatorHubAuthorizationFilterTests.cs`
(scoped operator connects in both modes; unauthenticated and read-only-scoped connections pass in
`LogOnly` and are refused in `Enforce`; the default is `LogOnly`) and
`Configuration/OperatorHubAuthorizationWiringTests.cs` (query token accepted on hub paths, ignored on
REST paths, filter registration pinned at the `Program.cs` source, mode operator-settable).

#### The adapter hub, and why its gate has a second half (AB#5063)

`Hubs/AdapterHubAuthorizationFilter`, registered via
`AddHubOptions<AdapterHub>(o => o.AddFilter<AdapterHubAuthorizationFilter>())`, checks **two** things
on every connection, both governed by the single `AdapterHubAuthorization:Mode` switch
(`OCTO_ADAPTERHUBAUTHORIZATION__MODE`, `LogOnly` default / `Enforce`, same table as above):

1. **Authentication** — `Constants.TenantCommunicationApiReadWritePolicy`, the policy this service's
   own tenant-scoped *write* routes use. The hub is a write surface (an adapter registers itself,
   receives the tenant's pipeline configuration — credentials included — and writes execution results,
   debug points and metrics), so the read-only policy would be the wrong bar.
2. **Tenant binding** — the connected adapter must belong to the tenant whose hub path it uses.

🔴 **The second half exists because nothing else can do it.** `/{tenantId}/adapterHub` is
tenant-addressed, but `TenantAuthorizationMiddleware` (`UseOctoTenantAuthorization()`) never sees a hub
connection: it returns early on any request without an `Authorization: Bearer` header, and a SignalR
client on the WebSocket / SSE transports sends its token as `?access_token=` instead. So the tenant
check happens in the filter and nowhere else. Its **rules are the middleware's**, not new ones — exact
`tenant_id` match against the route tenant (case-insensitive), fail closed when the claim is absent,
and a cross-tenant exemption read from the *same* `TenantAuthorizationOptions.CrossTenantServiceClientIds`
list the HTTP gate uses, so one allow-list governs the whole service. The **parent-tenant
administration rule (AB#5060) deliberately does not apply**: it is scoped to *user* tokens on endpoints
marked `IAllowParentTenantAdministration`, a hub is not such an endpoint, and a service token's
`tenant_id` proves much less (mirrored clients share the parent's secret; a token minted without
`acr_values` falls back to the system tenant, the root of the hierarchy).

🔴 **`Enforce` is blocked, and the blocker is not the transport any more — it is that the adapter
never logs in.** AB#5062 replaced the SDK's placeholder header with an `AccessTokenProvider` reading
the injected `IServiceClientAccessToken`, so the plumbing is in place and is re-read on every
reconnect. But `AdapterBuilder` / `WebAdapterBuilder` (octo-communication-sdk) register a bare mutable
`ServiceClientAccessToken` holder, `AdapterOptions` carries no client id / secret / authority to log in
with, and the **only** writer is the mesh adapter's `ServiceAccountTokenService` (AB#5027), whose only
callers are two *pipeline nodes at execution time* (`DeployPipeline@1`, `AnthropicAiQuery@1`). At
connect time the holder is empty and the provider returns `null` — no header, no query parameter. An
adapter therefore identifies itself with the unauthenticated `adapter-rtId` / `adapter-ckTypeId`
headers and nothing else. **The precondition work item is an adapter-side counterpart of the
operator's `OperatorAccessTokenService`**: a startup client-credentials login with
`acr_values=tenant:{tenantId}` writing into the singleton holder *before* `AdapterExecutionService`
starts the hub client, plus proactive refresh. That is octo-sdk / octo-mesh-adapter work, not this
repo's.

**Consumer inventory** (what to expect in the `LogOnly` log):

| Consumer | How it connects today | Verdict of an enforcing run |
|---|---|---|
| Mesh-adapter fleet (`AdapterHubClient`, octo-sdk, used by octo-communication-sdk's `AdapterExecutionService`) | anonymous — see above | refused (unauthenticated) |
| …the same adapter *after* one of its pipelines ran `DeployPipeline@1` / `AnthropicAiQuery@1` | the holder now carries a real, tenant-bound `octo_api` service-account token, re-read on the next reconnect | **passes both checks** |
| Studio's pipeline debugger | **not a hub client at all.** The adapter *pushes* debug points up the hub (`AdapterHub.SendDebugDataAsync`); Studio *pulls* them over REST from `PipelineDebugController` with the ordinary user bearer token. Its only SignalR connection is to the AI adapter's `/{tenantId}/aiHub`, with a proper `accessTokenFactory`. | n/a |
| octo-cli | connects to no SignalR hub | n/a |

Two consequences of the second row worth remembering: an adapter's credential state is
**non-deterministic** (it depends on which pipelines have run in that process), and **absence from the
inventory is not evidence that an adapter authenticates** — it may simply not have reconnected.

`ConfigureJwtBearerOptions`' `OnMessageReceived` already covers `/{tenantId}/adapterHub` and its
`/negotiate` sub-path (AB#5059 wrote it for both hubs), which is what makes the inventory meaningful
once a client does send a token — verified by the parameterised cases in
`OperatorHubAuthorizationWiringTests`.

Shared with the operator gate: `Hubs/HubConnectionPrincipal` (principal resolution including the
explicit bearer-scheme fallback, the caller description, and the service-vs-user-token test). One
implementation on purpose — a drifted copy would not fail loudly, it would quietly log "anonymous".

Tests: `Hubs/AdapterHubAuthorizationFilterTests.cs` (route-tenant adapter connects in both modes;
case-insensitive tenant match; anonymous and read-only-scoped connections pass in `LogOnly` and are
refused in `Enforce`; **fully-scoped credential of a foreign tenant refused** — the new half; service
token without `tenant_id` refused; allow-listed cross-tenant client connects; parent-tenant credential
refused on a child's hub path for both token kinds; user token of the route tenant connects; a
connection without a route tenant refused; default is `LogOnly`) and
`Configuration/AdapterHubAuthorizationWiringTests.cs` (filter registration pinned at the `Program.cs`
source, mode operator-settable, default when the section is absent, and that the two hub gates keep
distinct configuration sections).

#### The adapter **pool** hub — a third gate, and why it is not one of the other two (AB#4924)

`/adapterPoolHub` (`Hubs/AdapterPoolHub`) is the management channel of **adapter pool members**:
processes that belong to **no tenant** and are handed one per lease (shared adapter leasing, see
`docs/concepts/shared-adapter-leasing.md` §4). Mounted next to `/operatorHub`, i.e. with no tenant in
the route — which is the whole reason it exists as a separate hub.

🔴 **Neither existing gate fits, and the reasons are worth knowing before anyone "simplifies" this.**
`/operatorHub`'s gate applies `SystemCommunicationApiPolicy` — system authority over every tenant.
A pool member runs pipelines; it does not administer the estate, and giving the whole pool fleet
system authority to save a file is the wrong trade (concept §4 rejects it explicitly).
`/{tenantId}/adapterHub`'s gate has the right *policy* but binds the connection to a **route** tenant,
and this route deliberately has none. So: the adapter gate's policy
(`TenantCommunicationApiReadWritePolicy`), bound to the tenant **in the token**.

The binding is completed in **two places**, on purpose:

1. `AdapterPoolHubAuthorizationFilter` resolves the principal at connect time and stores its
   `tenant_id` on the connection (`Context.Items`, key `AdapterPoolHubAuthorizationFilter.ConnectionTenantIdItemKey`).
   It fails closed when there is no tenant to store. The filter cannot do more: it runs before any hub
   method, so no pool has been declared yet.
2. `AdapterPoolHub.RegisterPoolMemberAsync` refuses a registration whose declared `PoolTenantId` is a
   different tenant. **No parent/ancestor allowance**, same stance as AB#5063 — a member registered
   into another tenant's pool would receive leases carrying a *third* tenant's service-account secret.

Staged exactly like the other two: `OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE`, `LogOnly` default,
`Enforce` per environment without a release. The three sections are deliberately distinct so they can
be armed independently — pinned by `AdapterPoolHubWiringTests.TheThreeHubGates_HaveDistinctSections`.

🔴 **Authorization for a *borrower* never comes from this connection.** It arrives on the lease as the
borrower's own `PipelineServiceAccount` credential (AB#5027, concept §8 Q6) and dies with the lease.
That is what keeps a pool member from being a standing cross-tenant credential.

`Services/LeaseService` grants and releases. **Lending is consensual in both directions**: the pool's
`SharingMode` + allow-list say who *may* borrow (via `ITenantLendingScopeResolver`), and the borrowing
adapter's own `LentFromTenantId` / `LentFromPoolRtId` say from whom it *does*. Either half alone is not
enough — without the borrower half a lender could push executions into any descendant that never asked
for them, under an identity that descendant never intended to hand out. The borrower's credential is
read **after** both checks, so a request naming an adapter it has no business naming never reaches the
code that decrypts a client secret.

🔴 **There is no queue in this increment, deliberately.** A lease request that finds no idle member is
*refused with a reason*, not parked — scheduling (round-robin across tenants, interactive before batch
within a tenant's turn, the per-tenant cap) is increment 7, and half of it built here would be a second
scheduling policy nobody intended to write.

`POST {tenantId}/v1/adapterPool/{adapterPoolRtId}/lease` and `GET …/members`
(`TenantApi/v1/Controllers/AdapterPoolController`) are a **test and diagnostic surface**, not the
production path. They exist so the whole mechanism is exercisable end to end on a real two-tenant pair
one increment before the scheduler. The member list is **per controller instance** — a member's SignalR
connection lives on one pod — which is the same property `IOperatorConnectionManager` has.

🔴 **A client secret now travels over the hub as well as over the deploy path.** The AB#5027 guard
`DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget` is mirrored for the lease path in
`Services/LeaseServiceTests/LeaseSecretLogTargetTests` — same shape (NLog `MemoryTarget`, real path,
assertions on the **rendered** output, neither the value nor an eight-character prefix), plus a second
test that rendering a `LeaseDto` **as an object** does not reveal it. `LeaseDto` overrides `ToString`
for exactly that reason: a record's generated one prints every property, so the first
`logger.LogDebug("… {Lease}", lease)` would leak the secret without anybody noticing.

Tests: `Hubs/AdapterPoolHubTests/` (registration + the tenant-binding matrix in both modes, the
`IShutdownState` refusal, lease routing to a member, a second lease never landing on a claimed member,
release, stale release, disconnect mid-lease, draining, heartbeat),
`Hubs/AdapterPoolHubAuthorizationFilterTests.cs` (the AB#5063 matrix for this gate, plus that the
connection tenant is recorded in **both** modes), `Services/LeaseServiceTests/` (grant, the consent
matrix, the credential, TTL, the no-idle-member refusal, a failed push undoing the claim, release and
disconnect) and `Configuration/AdapterPoolHubWiringTests.cs` (mount, filter, section binding, the route
carrying no tenant, and that the **adapter** hub keeps its own gate and its route tenant).

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
| TenantManagementConsumer | CK model change notification failed | Error | Adapter CK-cache flush broadcast failed (AB#4456) |
| Hubs | Operation failed | Error | Hub operation errors |
| SignalChannelService | Registration started / verified / adopted / deleted | Information | Signal channel self-service transitions (AB#5143), incl. adopting an account the bridge already holds |
| SignalChannelService | Registration / verification / deletion failed | Error | Bridge rejected a register or verify call, or the repository delete failed |

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

**Operator-managed workload deploy attributes are runtime-state too
(3.28.0, AB#4706).** `Hostname`, `IngressEnabled`, `ChartVersion`,
`ValuesYaml` and `Values` on the deployable-workload attribute set
(`attributes/helmDeployment.yaml`, shared by `Adapter` and `Application`)
carry `isRuntimeState: true`. These fields are written at run time — the
productive hostname (e.g. `<tenant>.meshmakers.cloud`) and per-tenant
`ValueOverride`s (publicUri, clientId, …) by operators, `ChartVersion` by
the CD pipeline's `UpdateWorkloadChartVersion` — and an app blueprint that
seeds defaults (e.g. `EnergyCommunity.Base`'s Application entity) reset
all of them on every `InstallBlueprint -f`, silently breaking the deployed
ingress/DNS and OIDC configuration (observed live on prod-2/energydemo,
2026-08-04). Seeds now only apply on fresh installs. Consequence for
blueprint authors: a blueprint update can no longer ship CHANGED default
values for these five attributes to existing tenants — by design; document
new defaults in release notes instead. Note the association side (e.g. an
Application's `HelmRepository` edge) has no preserve mechanism — blueprints
must own their referenced `HelmRepositoryConfiguration` entity instead of
pinning foreign per-environment rtIds (the EC Base blueprint does this
since 2.2.5).

**`Pipeline.IsDebuggingEnabled` is runtime-state too (3.23.0, AB#4273).**
Debugging is an operational toggle (`SetPipelineDebug` / the Studio debug
button), not part of a pipeline's portable definition — so
`RtEntityToTcDtoConverter` skips it on export and it never round-trips
through `ExportRt`/`ImportRt` or a blueprint. The `isRuntimeState: true`
marker was added to `IsDebuggingEnabled` in `attributes.yaml` back in commit
`e54c749` but **in place on 3.22.0 without a version bump** (the 3.21.0→3.22.0
bump `0b31271` had already been cut ~46 min earlier), so a same-version
re-import is a no-op and the marker never reached tenants already on 3.22.0 —
which is why an imported pipeline (e.g. the voest simulator shipping
`IsDebuggingEnabled: true`) could still silently enable debugging. `3.23.0`
is the bump that actually propagates the marker on upgrade. No migration
script (`migration-meta.yaml` stays at 3.1.1 — schema-ahead-of-history; the
change is additive metadata, existing attribute values are untouched).
Note the flag only stops debug shipping via *export*; a hand-authored rt-model
that explicitly sets a runtime-state attribute still writes it on import, so
authored YAMLs must simply not declare `IsDebuggingEnabled` (fixed for the
simulator under AB#4274).

**`Configuration.ClientSecret` is runtime-state too (3.31.0, AB#5027).** The shared
`ClientSecret` attribute definition (used by `ServiceAccountConfiguration`,
`FinApiConfiguration` and `MicrosoftGraphConfiguration`) carries `isRuntimeState: true`
so a blueprint re-apply can no longer overwrite a live secret with the seeded
placeholder. Details, blast radius and the first-install semantics: see
"Pipeline Service Account — mandatory execution identity" below.

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

### Signal Channel Self-Service (AB#5143)

Tenant self-service activation of a Signal phone number against the cluster-shared
`signal-cli-rest-api` bridge (bbernhard/signal-cli-rest-api, multi-account). The bridge API is
**unauthenticated and cluster-internal — this controller is the ONLY component allowed to call
it**; the browser talks exclusively to the tenant-scoped REST endpoints below.

| Route | Policy | Behaviour |
|---|---|---|
| `GET {tenantId}/v1/signal/channel` | ReadOnly | The definition + live bridge cross-check (`bridgeRegistered` from `GET /v1/accounts`; `null` + `warning` when the bridge is unreachable) + `history` (registration audit trail, newest first, newest 50). 404 without a definition. |
| `POST {tenantId}/v1/signal/channel/register` | ReadWrite | Body `{number, apiUrl?, captchaToken?}`. Creates the singleton, claims the number, then checks `GET /v1/accounts`: a number the bridge already holds is **adopted** (state `Registered` immediately, no verify — clients detect `registrationState=2` in the answer); otherwise bridge `POST /v1/register/{number}` → state `CodePending`. 409 while Registered (number immutable) and when ANY other tenant claims the number. **422 when Signal demands a captcha** (machine-readable for the Studio wizard: attempt without a captcha first, show the captcha step only on demand, retry with `captchaToken`). 429 passthrough (with Retry-After) on Signal rate limits. |
| `POST {tenantId}/v1/signal/channel/verify` | ReadWrite | Body `{code}`. Bridge `POST /v1/register/{number}/verify/{code}` → `Registered` + `RegisteredAt=utcnow`; a wrong code keeps `CodePending` with `LastError` set. |
| `PUT {tenantId}/v1/signal/channel/displayName` | ReadWrite | Body `{displayName}` (camelCase segment — pinned by the Studio frontend). Changes the profile display name WITHOUT re-registering. Non-empty: attribute stored (persisted before the push, so a failed push is retried by calling again), pushed to the bridge (`PUT /v1/profiles/{number}`) only while `Registered` — push failures surface as 400/422/429 there. Empty/whitespace: clears the stored attribute WITHOUT a bridge call (Signal profiles need a non-empty name; the previously pushed profile name remains on the account). `ProfileUpdated` history entry in every case. |
| `DELETE {tenantId}/v1/signal/channel` | ReadWrite | Works from EVERY state. The bridge account is only unregistered (`POST /v1/unregister/{number}`, `delete_local_data: true`, best-effort — "not registered" / unreachable never blocks) when the definition **owns** the registration, i.e. its state is `Registered`; a CodePending/Failed/Unregistered definition may reference a working bridge account it does not own (the adopt scenario), which must not be destroyed. Then Erase-deletes the definition **including its history** — the number becomes claimable again, and a fresh definition starts with a fresh history. |

Pieces:

- **CK type `System.Communication/SignalChannel`** (model 3.34.0, singleton per tenant, derived
  from `${System}/Configuration`): `Number` (E.164), `ApiUrl`, `DisplayName` (optional profile
  display name — attribute id `SignalDisplayName` to keep the shared attribute pool
  collision-free; without it Signal shows "Unknown"), the runtime-state trio
  `RegistrationState` (enum `SignalRegistrationState`: 0 Unregistered, 1 CodePending,
  2 Registered, 3 Failed), `RegisteredAt`, `LastError`, and the runtime-state
  `RegistrationHistory` (RecordArray of `SignalRegistrationEvent`, see below). No migration script
  (additive — schema-ahead-of-history, `migration-meta.yaml` stays at 3.1.1) and no blueprint bump
  (no seed; a plain CK model bump rolls out via `IServiceManagedCkModelDescriptor`).
- **Registration audit trail (WI rev 4)** — `RegistrationHistory` holds `SignalRegistrationEvent`
  records `{At (UTC), User, Action, Detail?}` with enum `SignalRegistrationAction`
  (0 RegisterRequested, 1 RegisterFailed, 2 CodeVerified, 3 VerifyFailed, 4 Deleted,
  5 DeleteFailed, 6 Adopted, 7 ProfileUpdated). The service appends server-side for EVERY register/verify/delete
  attempt — bridge failures answered as 4xx/429 included (the `RegisterRequested` entry rides
  along with the pre-bridge claim save; the failure entry is appended exactly once and saved
  best-effort). Newest first, capped at the newest 50 on append (rebuild + reassign, never
  in-place mutation — `AttributeRecordValueList` materializes copies per read). The acting user
  is a `SignalChannelActor` built by the controller from the request principal (`sub` falling
  back to `client_id`, `name` falling back to `preferred_username` — the `ExecutePipelineCaller`
  claim mapping) and rendered as `"Name (subject)"`. Clients never write history; the DTO
  projects it as `history: [{at, user, action, detail}]`. A successful DELETE erases the
  definition INCLUDING the trail (fresh definition = fresh history), so a persisted `Deleted`
  entry never survives — the system event trail carries the durable deletion record; a FAILED
  repository delete persists a `DeleteFailed` entry on the surviving definition.
- **Adopt-existing-bridge-account** — before `POST /v1/register`, the register flow reads
  `GET /v1/accounts`; a number the bridge already holds (pre-existing dev account, backup/restore
  migration of bridge state — a plain register would 400 "Account is already registered") is
  adopted: state `Registered` + `RegisteredAt=utcnow` immediately, `Adopted` audit entry, no
  captcha/verify. All guards run BEFORE adoption (singleton, cross-tenant claim, immutability
  while Registered). Known limitation: the claim scan covers this instance's tenants only — a
  number claimed by another OctoMesh instance sharing the cluster bridge is not detectable (the
  bridge's NetworkPolicy trust boundary covers that). An unreadable account list falls back to
  the plain register attempt.
- **`SignalChannelService`** owns every invariant the model cannot express: singleton per tenant,
  cross-tenant number claim (scan over `IAdapterCache.GetEnabledTenantIds()`; an unreadable
  foreign tenant is skipped with a warning so one broken tenant cannot block all registrations),
  number immutability while Registered, E.164 validation (`+` then 7–15 digits), and the
  register/verify/delete state machine. The claim is persisted BEFORE the bridge call so two
  tenants racing for a number collide on the stored definition. Rate-limited / unreachable
  register attempts keep `Unregistered` (retry-able); only a bridge rejection is `Failed`.
- **`SignalBridgeClient`** (named `HttpClient`, 60s timeout — a bridge register can take 10-30s)
  maps bridge answers into `SignalBridgeException` kinds (Rejected / RateLimited with Retry-After
  / Unreachable); the controller maps those onto 400 / 429 / 400 and conflicts onto 409. A
  Rejected answer additionally carries the typed `IsCaptchaRequired` flag — set in
  `SignalBridgeException.Rejected`, the ONLY place matching signal-cli's stable
  "Captcha required" message text — which the service surfaces as
  `SignalChannelErrorKind.BridgeCaptchaRequired` → **422** (same ErrorResponse envelope), so the
  Studio wizard can attempt without a captcha and show the captcha step only on demand. The
  Failed state, LastError and the RegisterFailed history entry are identical to a plain 400
  rejection.
- **Profile display name** — when a channel with a `DisplayName` reaches `Registered` (verify
  success OR adoption), the service pushes the profile to the bridge
  (`PUT /v1/profiles/{number}` with `{"name": ...}` → 204, `SignalBridgeClient.UpdateProfileAsync`).
  Best-effort on the registration paths: a push failure must NOT fail the registration — it is
  recorded as a `"; profile update failed: ..."` detail suffix on the transition's
  `CodeVerified`/`Adopted` history entry; a successful push appends a `ProfileUpdated` entry. The
  dedicated `displayName` endpoint (row above) is where push failures DO surface; clearing the
  name there never touches the bridge profile.
- **`CommunicationControllerOptions.SignalBridgeApiUrl`** is the instance default ApiUrl
  (`http://signal-cli-rest-api.signal-bridge.svc.cluster.local:8080`); a register request may
  override per tenant, and an existing definition's ApiUrl wins over the default.
- Deletes use `DeleteOptions.Erase` — the stored definitions are the instance-wide number-claim
  registry, and an archive tombstone must not keep a number blocked.

Tests: `tests/CommunicationControllerService.Tests/Services/SignalChannelServiceTests/`
(register / verify / delete / get suites pinning the singleton, claim, immutability,
state-machine and validation rules against a mocked bridge; `RegistrationHistoryTests`
pinning the audit trail — entries on success AND failure paths, exactly-once append on
bridge failure, newest-first ordering, the 50-cap trim, the GET projection and the actor
formatting; `RegisterAdoptionTests` pinning the adopt-existing-bridge-account flow and that
every guard runs before adoption).

#### SignalChannel projection into every pipeline configuration (AB#5145)

The tenant's SignalChannel singleton is **always** mixed into every pipeline's projected
configuration list — no `Uses` association required — by
`AdapterService.AddSignalChannelConfigurationAsync` (the authoritative cross-repo contract doc
lives on that method). The adapter keys its per-pipeline `GlobalConfiguration` by
`RtWellKnownName`, so the Signal nodes (`FromSignal@1` / `SignalSender@1` in octo-mesh-adapter,
shared `SignalChannelEndpointResolver`) look the entry up under `"signal-channel"` and use its
`Number`/`ApiUrl` only while `RegistrationState == Registered` — the entity is shipped in EVERY
state on purpose so the node can warn "present but not registered" instead of silently reading
nothing. Guards: no channel → nothing added; a same-rtId/same-well-known-name entry already in
the list (legacy `Uses` edge) is never duplicated; a repository failure (e.g. a tenant whose CK
model predates 3.34.0) is logged and skipped, never failing the deploy. Like every projected
configuration it is cached at pipeline registration — a channel registered later reaches the
nodes on the next redeploy. Tests:
`Services/AdapterServiceTests/SignalChannelProjectionTests.cs`.

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
public int PipelineExecutionRetentionDays { get; set; } = 3;     // Orphan hard-cap: unconditional delete (executions whose pipeline is gone)
public int PipelineExecutionRetentionHours { get; set; } = 1;    // AB#4370: hours before terminal executions are folded into buckets + erased
public int StatisticsUpdateIntervalMinutes { get; set; } = 60;   // Statistics aggregation interval
public bool StoreInputData { get; set; } = false;                // Whether to store pipeline input data
public int MaxInputDataLength { get; set; } = 10000;             // Max length of stored input data
public int PipelineExecutionTimeoutHours { get; set; } = 24;     // Legacy connection-unaware timeout (no longer used by the reaper)
public int PipelineExecutionStuckGraceMinutes { get; set; } = 15;         // AB#4280 grace before a stuck execution is failed
public int PipelineExecutionStuckCheckIntervalMinutes { get; set; } = 5;  // AB#4280 reaper cadence
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

### Adapter CK Cache Invalidation (AB#4456)

Adapters cache the tenant's CK model in-process (engine `CkCacheService`, populated
load-once per tenant). After a CK model import (`ImportCk`) or `ClearCache`, that cache
must be invalidated or pipeline nodes (`CreateUpdateInfo@1` / `ApplyChanges@2`) keep
validating against the old model until the adapter process restarts.

Flow:

1. Asset-repo publishes `PreUpdateTenant` / `PosUpdateTenant` (same `CorrelationId`)
   around the tenant update — both for CK imports (`TenantContext.ImportCkModelAsync`)
   and for `ClearCache` (`TenantsController.ClearCache`).
2. `TenantManagementConsumer` pairs the two messages; when the pair completes (i.e. the
   update is finished) it calls `IAdapterService.CkModelChangedAsync(tenantId)` **before**
   the enabled-gated restart relay (`ExecutePreTenantUpdate`/`ExecutePosTenantUpdate`).
   The call is deliberately NOT gated on `IConfigurationService.IsEnabledAsync` and its
   failure never blocks the restart relay (own try/catch + error event).
3. `AdapterService.CkModelChangedAsync` → `AdapterHubCallbacks.CkModelChangedAsync`,
   which **broadcasts** `IAdapterHubCallbacks.CkModelChangedAsync(tenantId)` to every
   adapter connection (`Clients.All`) instead of routing through the adapter cache. The
   cache is wiped during a tenant pre-update, and an adapter whose re-registration failed
   stays connected but uncached — exactly the stale-cache case; adapters filter by
   tenant themselves.
4. Adapter side (`octo-communication-sdk` `AdapterExecutionService`): the callback is a
   no-op for foreign tenants; for the own tenant it calls the new
   `IAdapterService.CkModelChangedAsync` (default interface method, no-op) — the mesh
   adapter overrides it and unloads the CK cache (`MeshAdapterService`), which is lazily
   reloaded on the next pipeline execution. No restart, pipelines stay registered.

Old adapter builds without the hub handler just log an unbound-method warning — the
`PreUpdateTenantAsync` restart relay remains their (gated) fallback.

**Update scope (AB#4895).** `PreUpdateTenant`/`PosUpdateTenant` carry an optional
`TenantUpdateScope` (default `Full`; older publishers deserialize to `Full`). When a completed
pair is `CacheOnly` — e.g. the nightly `AttributeValueAggregatorJob`, which only rewrites
`AutoCompleteValues` on CK attributes — the consumer broadcasts the CK cache flush but
**skips the adapter restart relay** entirely. The fleet-wide midnight restart this relay used
to cause was the trigger window for AB#4876. A mixed pair is treated as `Full` (defensive).
Tests: `CacheOnlyPair_NotifiesCkModelChangedButSkipsRestartRelay`,
`MixedScopePair_FullWins_RunsRestartRelay`.

**Pairing state is `static` on purpose.** `AddBroadcastEventConsumer` registers consumers
**scoped**, so MassTransit creates a new `TenantManagementConsumer` per message — an
instance-level pair dictionary can never match Pre with Pos, and the paired branch
(restart relay + CK flush) silently never runs. This was the second root cause of
AB#4456: the relay had been dead in production. Any consumer that keeps cross-message
state must hold it in a static field or a singleton service, never an instance field.

Tests: `Consumers/TenantManagementConsumerTests` (flush on pairing, flush despite
disabled tenant, flush failure doesn't block relay, no flush on unpaired Pre,
cross-instance pairing) and `Services/AdapterServiceTests/CkModelChangedAsyncTests`.

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

### Adapter Reconnect Remove Race (AB#4594, completed)

The first AB#4594 fix (`f43e2bc`) refreshed the cached `ConnectionId` in
`RegisterAdapterInternalAsync` and added stale-connection early-return guards to
`UnregisterAsync` / `SetAdapterCommunicationStateOfflineAsync`. Those guards only
cover the case where the adapter has **already** reconnected *before* the
stale handler runs. They do **not** cover the reconnect that lands *during* the
handler's own `await`s: an adapter's graceful stop calls `UnRegisterAdapterAsync`
on the OLD connection (`AdapterExecutionService.StopAsync`) and then reconnects.
On the controller, `UnregisterAsync` passes the entry guard (the old connection
is still current at that instant), does its DB downgrades (`await`s), and only
then removed the adapter — but by then the new connection may have re-registered
(`AddAdapter` / `UpdateConnectionId`). The old **unconditional** `RemoveAdapter`
then deleted the freshly-registered live adapter, so every later deploy threw
`AdapterServiceException.AdapterNotLoaded` ("no live SignalR connection") until a
pod restart. `SetAdapterCommunicationStateOfflineAsync` had the same TOCTOU
between its guard and the unconditional `RemoveConnectionId`.

Fix: `AdapterTenant` now serializes its connection-lifecycle mutations
(`AddAdapter` / `UpdateConnectionId` / `RemoveAdapter` / `RemoveConnectionId`)
under a single `_connectionLock`, and exposes **atomic compare-and-remove**
primitives `RemoveAdapterIfConnection(rtId, connectionId)` /
`RemoveConnectionIdIfConnection(rtId, connectionId)` that only remove when the
cached `ConnectionId` still equals the passed one. `UnregisterAsync` and
`SetAdapterCommunicationStateOfflineAsync` call these instead of the unconditional
variants, so a stale unregister/disconnect can never clobber a reconnected
connection. (`PublishConfiguration` is a no-op today, so holding the lock across
it is safe.) Note the DB `CommunicationState`/pipeline-`Pending` writes a stale
unregister makes before the conditional remove are a narrow, cosmetic residual —
the adapter stays live and deployable, and `OnConnectedAsync`'s Online write
reconciles the state.

Tests: `Caches/Adapters/AdapterTenantTests` (compare-and-remove happy path +
newer-connection no-op for both primitives) and
`Services/AdapterServiceTests/UnregisterAsyncTests.UnregisterAsync_ReconnectDuringUnregister_DoesNotRemoveFreshConnection`
(the reconnect-mid-await interleaving that the old code failed).

### Reconcile Config Push on Registration (AB#4594 recurrence #2)

The two fixes above only cover the *connection-cache* races (stale ConnectionId,
TOCTOU remove). They do **not** guarantee the freshly (re-)registered adapter
actually ends up running its deployed pipelines. Recurrence #2 (prod-1 /
salzburgdev, 2026-08-06): a coordinated rollout restarted the Communication
Controller (07:59) and then the mesh adapter (08:09) within minutes; the adapter
came up `Online` with **zero pipeline routes registered** — every
`FromHttpRequest` endpoint (`/exportHandover`, `/processDocuments`,
`/uploadDocuments`, …) returned **404** — while the controller still reported the
pipelines as `Deployed`. The accounting-app Handover export surfaced it as
"kein Paket". Only a manual `UndeployWorkload`→`DeployWorkload` recreate restored
the routes.

Root cause: config reaches an adapter **two** ways — (1) the return value of
`RegisterAdapterAsync` (the only delivery on the connect path), and (2) the active
push `AdapterHubCallbacks.AdapterConfigurationUpdatedAsync` (used by the *deploy*
paths). `RegisterAdapterInternalAsync` only ever *returned* the config DTO; it
never actively pushed and never re-drove the deployed pipelines onto the new
connection. So any register whose return value the adapter failed to fully apply
(or that resolved to a stale/empty set during rollout churn) left the adapter
routeless with **nothing to reconcile it** — the controller believed everything
was `Deployed`, so no deploy was ever retried.

Fix: `AdapterService.ReconcileAdapterConfigurationAsync` — after the connection is
(re-)cached in `RegisterAdapterInternalAsync`, the controller actively re-pushes
the adapter's deployed configuration onto the freshly registered connection
(when it has ≥1 pipeline). This is **best-effort** (a push failure is logged, never
fails the registration — the return value still carries the config) and uses the
**raw, non-waiting** `AdapterConfigurationUpdatedAsync` send, *not*
`SendConfigurationAndWaitForResultAsync`, so the register RPC is never blocked for
the 120s deploy-ack window. The adapter's ack
(`UpdateConfigurationStateAsync`) then transitions the pipelines to `Deployed`,
which also clears the long-standing "stuck `Pending` after restart" drift (a
registration delivered purely via the return value was never acked). The re-push
is idempotent — the SDK's `RegisterPipelineCoreAsync` replaces stale registrations
— so the double delivery (return value + push) is safe.

Tests: `Services/AdapterServiceTests/RegisterAdapterTests`
(`RegisterAdapter_RePushesDeployedConfiguration_OnRegistration` pins the re-push
with the deployed pipeline set; `RegisterAdapter_NoPipelines_DoesNotRePush` pins
the empty-config no-op). The `AdapterServiceTestsBase` mock already simulates the
adapter's deploy ack, so the existing register tests exercise the ack path too.

### Adapter Offline Reconciliation (AB#4699)

The rolling-upgrade race guard in `AdapterHub.OnDisconnectedAsync` skips the Offline
write during the controller pod's own shutdown, on the premise that the adapter
reconnects to the surviving pod (which writes Online). If it never reconnects
(adapter crash / lasting partition that coincides with the controller restart), the
DB entity is stuck at a stale `Online` with no live SignalR connection — config
pushes target a dead connection and Studio shows the adapter green. SignalR's own
client-timeout only covers disconnects while the controller is **healthy**; the
shutdown-window orphan has nothing to reconcile it.

`AdapterOfflineReconciliationBackgroundService` closes the gap: after a startup
grace it periodically asks `AdapterService.ReconcileOrphanedOnlineAdaptersAsync` to
mark every adapter persisted `Online` but without a live connection as `Offline`.

**Why not reuse the config `IAdapterCache` as the liveness signal?** Because
`PreUpdateTenantAsync` flushes that cache on every tenant update while the SignalR
connections stay alive (see `PreUpdateTenantAsync` / `PosUpdateTenantAsync`) — a
connected adapter is therefore legitimately absent from the config cache, so a
cache-miss cannot mean "disconnected". This is the exact trap that made the old
"Offline-if-not-in-cache" loop reset every adapter's state; do not reintroduce it.
There is also no periodic adapter heartbeat, so `CommunicationStateTimestamp`
staleness is not a liveness signal either (a healthy adapter connected for days keeps
its connect-time timestamp).

The fix introduces a dedicated `IAdapterConnectionTracker` — a per-pod registry keyed
by `(tenantId, adapterRtEntityId) → connectionId`, populated in
`SetAdapterCommunicationStateOnlineAsync` and cleared (compare-and-remove) in
`SetAdapterCommunicationStateOfflineAsync`, and **never** touched by a tenant
pre/post-update. So a tracker-miss reliably means "no live SignalR connection on this
pod". The controller runs single-replica in steady state (no SignalR backplane, the
cross-node cache publish is a no-op), so this pod's tracker is the authoritative
liveness view; the only overlap is the brief rolling-upgrade window, which the
startup grace covers (adapters reconnect to the new pod, repopulating its tracker,
before the first sweep runs). `ReconcileOrphanedOnlineAdaptersAsync` re-checks the
tracker immediately before each write, and the repository's `AttributeNewerThanGuard`
on the state timestamp rejects a stale Offline that raced past a concurrent Online.

Config: `CommunicationControllerOptions.AdapterOfflineReconciliationIntervalMinutes`
(default 5) is both the sweep cadence and the startup grace — it must comfortably
exceed the worst-case adapter reconnect time after a controller restart.

Tests: `Services/AdapterConnectionTrackerTests` (track / compare-and-remove /
stale-disconnect-keeps-live / reconnect-then-stale-disconnect) and
`Services/AdapterServiceTests/ReconcileOrphanedOnlineAdaptersAsyncTests` (orphaned
Online → Offline, live connection kept, non-Online skipped, missing CkTypeId skipped,
mixed fleet, and the real Online-write-populates-tracker path).

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

**Pending workload notifications (AB#4371).** When `GetConnectionsForPool`
returns no owner, the workload deploy/undeploy notification is **queued**,
not dropped. Dropping it caused the prod-1 incident where an undeploy fired
while the pool was transiently orphaned (the operator's `RegisterPoolAsync`
had been rejected during a parallel-startup CkCache race and the operator
never retried — fixed operator-side by its registration retry loop): the
helm release kept running forever while the entity said Undeployed.
`OperatorConnectionManager` keeps a per-`(tenant, poolRtId)` map, last-wins
per workload rtId — an undeploy queued after a deploy of the same workload
supersedes it (and vice versa), so a stale deploy can never resurrect a
release. `OperatorHub.RegisterPoolAsync` calls
`FlushPendingWorkloadNotificationsAsync` right after the pool's state write
succeeds; a replay whose send fails is re-queued for the pool's next
registration. The queue is in-memory like the rest of the tracking maps —
a controller restart clears it, and the operator reverse-sync plus the next
user-triggered deploy/undeploy re-establish state. Note the tracking maps
(`_deployedWorkloadsByTenant` etc.) are updated before routing, so the
tenant-delete cascade sees consistent state whether or not the notification
was queued. Tests: `Hubs/OperatorConnectionManagerTests` (pending-queue
section).

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

### Pending-Workload Reconcile on Pool Registration (AB#4894)

A workload deploy notification is fire-and-forget SignalR; one sent while the operator pod was
being replaced (e.g. an operator CD mid-rollout) lands on the dying connection and is lost —
the entity stays `Pending` forever, and neither the AB#4371 pending queue (the pool HAD a
registered owner at send time) nor the reverse-sync (restores state, never re-dispatches)
covers it. `OperatorHub.RegisterPoolAsync` therefore calls
`PoolService.ReconcilePendingWorkloadsAsync` after the AB#4371 flush: every workload of the
pool still in `DeploymentState=Pending` gets its deploy re-dispatched through the normal
`DeployWorkloadAsync` path. Best effort — lookup or per-workload failures are logged and never
fail the registration. Re-dispatching a genuinely in-flight deploy is safe: the operator queue
is serial and `helm upgrade --install` is idempotent. Tests:
`Services/PoolServiceTests/ReconcilePendingWorkloadsAsyncTests`.

**A reconcile is not a release decision (AB#4955).** The re-dispatch above fires on events that
have nothing to do with releasing software — an operator restart, a blueprint re-apply, a CK-model
update, `EnableCommunication` — because all of them re-register the tenant's pools via
`PreUpdateTenantAsync`. A workload with an empty `ChartVersion` means "newest in the repository",
resolved by the operator at `helm upgrade` time, so those events silently moved six prod-1
accounting workloads from chart 1.0.71 to 1.0.72 with nobody deploying them. Two things close it:

- `DeployWorkloadAsync(tenantId, workloadRtId, isReconciliation)` puts
  `WorkloadDeployedDto.IsReconciliation` on the wire; only `ReconcilePendingWorkloadsAsync` passes
  `true`. The operator then keeps the chart version it already has installed instead of resolving
  the newest one again (see the operator's CLAUDE.md → "Reconciliation Keeps the Installed Chart
  Version"). A user-triggered Deploy stays `false`, so an empty `ChartVersion` keeps meaning
  "newest" — the contract `System.Communication.MainLatest` depends on.
- The re-dispatch of an **unpinned** workload additionally writes a Warning event. The flag is
  additive, so an operator that pre-dates it still resolves anew; the warning is what makes that
  visible until the whole fleet is current. A pinned workload gets an Information event instead —
  it comes back on exactly the version it was running.

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

## Disable Refuses While Pools or Workloads Are Deployed (AB#4255) — or AI Services Is Enabled (AB#4884)

`POST {tenantId}/v1/communication/disable` answers **409** with an `OperationFailedErrorDto` while any
Pool, Adapter or Application of the tenant has a `DeploymentState` other than `Undeployed` /
`Disabled` (`ActiveDeployment.IsActive`: Deployed, Pending and Error all own operator resources —
see the recompute comment in `PoolService.RecomputeAllDeploymentStatesAsync`). The body names every
resource as `Kind 'Name' (State)` plus the undeploy verbs (`UndeployWorkload`, `UndeployPool`,
Studio). Every other `ConfigurationException` stays a 400. The tenant delete/detach guard in the
asset repository (AB#4255 step 1) only reads the enabled flag, so this is the check that keeps a
deleted tenant from leaving `CommunicationPool` CRs and helm releases behind.

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
- `IPoolService.GetActiveDeploymentsAsync` reads the **repository** (`GetPoolsAsync` +
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
a Disable the tenant API therefore stays callable. `PoolController.DeployPoolAsync` and
`DeployWorkloadAsync` — the two endpoints that create operator-managed cluster resources — check
`IConfigurationService.IsEnabledAsync` themselves and answer 409 on a disabled tenant; undeploy stays
open so remediation always works. `DeployDataFlow` / `DeployTrigger` already fail on a disabled tenant
because the adapter cache is flushed (`AdapterServiceException.TenantNotEnabled`). Adding the
middleware gate is a follow-up decision, not part of AB#4255.

**Operator release must not resurrect a resting pool.** `UndeployPoolAsync` writes the resting state
(`Undeployed` / Edge `Disabled`) *before* it notifies the operator; the operator then removes the CR and
calls `UnregisterPoolAsync`, and `PoolService.UnregisterPoolOperatorAsync` used to overwrite the resting
state with `Pending` ("no operator until one re-registers"). Every gracefully undeployed Cloud pool
therefore sat at `Pending` forever — invisible before, fatal for the guard (found in the local E2E with
the kind operator connected). The release now leaves a pool that already rests alone; only a
still-deployed pool that loses its operator flips to `Pending`
(`UnregisterPoolOperatorAsyncTests.UnregisterPoolOperatorAsync_RestingPool_KeepsItsDeploymentState`).

Tests: `Services/PoolServiceTests/GetActiveDeploymentsAsyncTests`,
`Services/DefaultConfigurationCreatorServiceTests/GetDisableBlockerAsyncTests`,
`Controllers/CommunicationControllerDisableTests`, `Controllers/PoolControllerDeployGateTests`,
`Services/PoolServiceTests/UnregisterPoolOperatorAsyncTests`, integration `Repository/GetWorkloadsAsyncTests`.

## On-Demand Adapter Lifecycle — Scale-to-Zero (Epic AB#4914; AB#4916/4917/4918)

Rarely used adapter workloads scale to 0 replicas when idle and are woken automatically on
demand. Design doc: `docs/concepts/on-demand-adapter-lifecycle.md`.

**CK model (3.29.0, AB#4916).** `DeployableWorkload` carries `LifecycleMode`
(AlwaysOn=0 default | OnDemand=1 | Auto=2 reserved, rejected until implemented) and
`IdleTimeoutMinutes` (default 30) as author configuration, plus `LifecycleState`
(Running/Draining/Hibernated/Waking) and `LastActivityAt` as `isRuntimeState` attributes.
The existing state fields are deliberately untouched: `CommunicationState=Offline` stays
factually correct while hibernated, `DeploymentState=Deployed` stays correct (the helm
release still exists) — consumers interpret them *through* `LifecycleState`.

**Activation is runtime configuration, not a deployment switch.** Two gates must both be on:
1. Per-tenant config record `communicationLifecycle` (`CommunicationLifecycleConfiguration
   { ScaleToZeroEnabled }`, default false) in the tenant KV store — same store as the
   enabled flag. Read through `ILifecycleConfigurationService` (30 s TTL cache; a write
   invalidates this pod immediately). REST: `GET/PUT {tenantId}/v1/communication/lifecycle`;
   octo-cli: `GetCommunicationLifecycle` / `SetCommunicationLifecycle -sze true|false`.
   Setting false is the per-tenant emergency stop.
2. Per-workload `LifecycleMode=OnDemand`.

**State machine (owned by `IWorkloadLifecycleService` / `WorkloadLifecycleService`).**
`Running → Draining` (idle watchdog) `→ Hibernated` (operator scale-0 ack via
`OperatorHub.ReportWorkloadScaleStatusAsync`) `→ Waking` (any demand signal) `→ Running`
(readiness = **`ConfigurationState=Configured`**, NOT Online — AB#4594; hooked in
`AdapterService.UpdateConfigurationStateAsync` → `NotifyWorkloadConfiguredAsync`, which also
releases the wake waiters). A failed scale-0 reverts Draining→Running; a failed scale-1
fail-fasts active waiters; a wake that never reaches Configured within
`LifecycleWakeBudgetSeconds` (default 60) reverts to Hibernated and throws
`WorkloadLifecycleServiceException` (deployment stays scaled up for diagnosis). The wake
wait registry is per-pod in-memory and deliberately separate from
`AdapterService._pendingDeployments` (single-TCS last-writer-wins slot).

**Scale mechanics (AB#4917).** `RequestScaleAsync` routes a `ScaleWorkloadDto` through
`IOperatorConnectionManager.NotifyWorkloadScaleAsync` (pool-scoped routing; when no operator
owns the pool the notification is queued under a scale-specific pending key
`{workloadRtId}::scale` so it never supersedes a queued deploy/undeploy — AB#4371 rules).
The operator patches `{"spec":{"replicas":N}}` on the Deployments matching
`app.kubernetes.io/instance={release}` (no helm) and acks via
`ReportWorkloadScaleStatusAsync`. Redeploys must not resurrect: `BuildWorkloadDeployedDtoAsync`
sets `WorkloadDeployedDto.Hibernated` for Hibernated/Draining workloads and the operator pins
`--set replicaCount=0`.

**Wake gates (AB#4918).** Demand signals all funnel into
`EnsureWorkloadRunning[ForPipeline]Async` (fast no-op when the tenant gate is off, the
workload is AlwaysOn, or the workload is Running — Running just stamps `LastActivityAt`):
- Execute pipeline: `TriggerManagementService.StartExecutePipelineAsync`, **before** the
  queue send — the execute queue is non-durable/auto-delete, a send to an absent queue is
  silently dropped.
- Config/deploy pushes: `AdapterService.DeployAdapterConfigurationAsync`,
  `DeployPipelineAsync`, `DeployDataFlowAsync` (wake-first, before the cache lookup that
  would throw `AdapterNotLoaded`). 🔴 **Except a `Leased` adapter** (AB#4924):
  `DeployPipelineAsync` reads the workload first and skips the gate for one, because a leased
  adapter has no workload of its own and the pool belongs to another tenant.
- Manual wake API: `POST {tenantId}/v1/adapter/{workloadRtId}/wake` (Studio "wake now",
  app pre-warm).
- Cron co-wake: `UpdateScheduleAsync` registers, for every cron trigger whose pipeline runs
  on an OnDemand adapter, a companion recurring send (same cron, same scheduleGroup) of a
  `LifecycleWakeMessage` to the **durable** queue `PipelineQueueNames.LifecycleWakeQueue`
  (`octo::com-controller::lifecycle-wake`), consumed by `LifecycleWakeConsumer`
  (registered via `AddRoutedEventConsumer` — NOT `AddCommandConsumer`, whose endpoints are
  temporary). The pipeline's own trigger message meanwhile buffers durably on its
  per-pipeline trigger queue. Co-wake schedules are registered independently of the tenant
  gate (the consumer-side gate no-ops when the feature is off) so flipping the flag needs no
  trigger redeploy.

**Idle watchdog (AB#4918).** `WorkloadLifecycleWatchdogBackgroundService`
(`LifecycleWatchdogIntervalMinutes`, default 5, also the startup grace; structure mirrors
`AdapterOfflineReconciliationBackgroundService`). Per scale-to-zero tenant and OnDemand
Adapter workload in Running+Deployed: idle metric = max(`LastActivityAt`, per-pipeline
`RtPipelineStatistics.LastExecutionAt` — statistics survive the AB#4370 execution fold, raw
`CompletedAt` does not); busy guards = running executions
(`GetRunningExecutionsForAdapterAsync`) and an in-flight wake (`HasActiveWake`). No observed
activity at all counts as idle-since-forever (that fleet is the feature's target). Idle >
`IdleTimeoutMinutes` → Draining + scale-0 request. The watchdog also reconciles stale
`Waking` states (controller restart lost the in-memory waiters): Configured → Running;
stuck > 2× wake budget → Hibernated + error event. Applications are skipped (no pipeline
activity signal yet).

**Hibernation stays out of the audit trail (AB#4919).** A scale-to-zero disconnects the
workload on purpose, so the offline paths ask
`IWorkloadLifecycleService.IsIntentionallyDownAsync` (true for `Draining`/`Hibernated`;
fast-path false when the tenant has scale-to-zero off, never throws) before they report
anything:

- `AdapterService.SetAdapterCommunicationStateOfflineAsync` skips the "is now offline"
  information event.
- `AdapterService.ReconcileOrphanedOnlineAdaptersAsync` still corrects a stale `Online` — a
  workload that looks healthy while hibernated is worse than the log line — but drops the
  "had no live connection" event and logs at Info instead of Warn.
- `PipelineExecutionService.MarkExecutionsAsInterruptedAsync` keeps marking executions, but
  when the adapter is hibernating it reports a **warning** instead of the routine information
  event: the watchdog only drains a workload with no running executions, so finding any here
  means the drain guarantee did not hold, and unlike an ordinary disconnect the platform is
  what cut the work short.

The **state writes are deliberately unchanged** — `CommunicationState=Offline` is factually
true while hibernated, and consumers are supposed to read it through `LifecycleState`. What
changes is only how loudly it is reported. Tests:
`Services/AdapterServiceTests/HibernationAuditSuppressionTests`, the two hibernation cases in
`Services/PipelineExecutionServiceTests/MarkInterruptedTests`.

### Lifecycle metrics (AB#4919)

`WorkloadLifecycleMetrics` (static, mirroring `MongoCommandObservability` in the MongoDB
engine) emits four instruments on the meter **`Meshmakers.Octo.Communication`**, which
octo-common-services' `ObservabilityBuilder` registers with the OpenTelemetry MeterProvider —
without that registration the measurements are recorded and dropped.

| Instrument | Kind | Purpose |
|---|---|---|
| `octo.workload.wake.count` | counter | Wakes, tagged `octo.wake.outcome` = `configured` / `timeout` |
| `octo.workload.wake.duration` | histogram (s) | Scale-up request → `ConfigurationState=Configured`; the latency a request pays for a wake |
| `octo.workload.hibernation.count` | counter | Completed hibernations (operator acked scale-0) |
| `octo.workload.hibernated` | observable gauge | 1 while hibernated or draining, 0 while running — averaged over time this is the hibernation ratio |

Tags on every instrument: `octo.tenant.id`, `octo.workload.rt_id`, `octo.workload.name`.
Cardinality is bounded by the number of workloads (dozens per cluster).

Three decisions worth keeping:

- **The wake is timed by the caller that started it**, in the `Hibernated`/`Draining` branch of
  `EnsureWorkloadRunningAsync` — not inside `WaitForConfiguredAsync`, which is also entered by
  callers joining an in-flight wake. Timing there would count one wake per waiter and inflate
  both the count and the percentiles.
- **A timed-out wake is counted but never recorded as a duration.** The budget is a cut-off,
  not an observation; mixing it in pulls every percentile towards the timeout.
- **The gauge map is in-memory and republished by the idle watchdog** from each swept
  workload's persisted state (`ObserveState`), so a controller restart heals within one sweep
  instead of reporting every workload as running forever. `UndeployWorkloadAsync` calls
  `Forget` — an undeployed workload left in the map would keep publishing its last value.

Tests: `Services/WorkloadLifecycleMetricsTests` (through a real `MeterListener`, so the
assertions are about the names and tags the exporter actually publishes; each test uses a
unique tenant because the instruments are process-wide and the suite runs concurrently).

### HTTP Activator — wake on request (AB#4923)

Routes and authorization live inside the adapter (`HttpRequestService` /
`FromHttpRequest@2`), so nothing in front of it can pre-authorize a call — and with the
workload scaled to zero the ingress can only answer 502/503. `WorkloadActivatorMiddleware`
holds such a request through the wake and then forwards it.

**Wiring is an ingress annotation, not a route.** Adapter Ingresses carry
`nginx.ingress.kubernetes.io/default-backend` naming this service; nginx uses that backend
exactly when the primary Service has no ready endpoint. That single condition is the whole
mechanism — there is no state to flip on hibernate and no window where the two disagree,
and steady-state traffic never passes through the controller. It also catches the
non-lifecycle cases (ImagePullBackOff, crash loop) and answers them with a named 503 rather
than nginx's default page. The companion annotation
`nginx.ingress.kubernetes.io/proxy-read-timeout` must exceed the wake budget; the cluster
default of 60 s would cut the hold short. Both are projected onto every workload by the
operator's cluster-wide `OPERATOR__INGRESS__ANNOTATIONS__n__*`.

Enabled per instance by `CommunicationControllerOptions.ActivatorEnabled` (chart:
`services.communication.activatorEnabled`, default off). The flag alone is inert — the
annotation is what routes traffic.

Request path:

1. `IWorkloadHostnameIndex` resolves the inbound `Host` header to a workload. The index is
   built in the background (`WorkloadHostnameIndexBackgroundService`) over every enabled
   tenant's ingress-enabled workloads, with hostname templates resolved exactly as the deploy
   path resolves them, because the Ingress carries the resolved value. A miss falls through
   untouched — one dictionary lookup, and it is what keeps the controller's own API
   unaffected. The middleware therefore runs **before** authentication and routing: these
   requests belong to the adapter's URL space, so neither this service's auth policies nor
   its route table apply.
2. `EnsureWorkloadRunningAsync` — the same wake gate the execute path uses, so concurrent
   callers share one wake.
3. Forward to `ActivatorWorkloadAddressTemplate` with `{release}` replaced by the workload's
   helm release name. `WorkloadHostnameIndex.ReleaseName` mirrors the operator's
   `K8sNaming.DnsName`; it is duplicated rather than shared (no common library) and pinned by
   `WorkloadHostnameIndexTests.ReleaseName_MatchesTheOperatorsRule`. The default template
   carries no namespace, so the pod's own search domain resolves it in the controller's
   namespace — which is where the operator deploys workloads.
4. A refused connection is retried for `ActivatorForwardRetrySeconds` (default 30). The wake
   completes on `ConfigurationState=Configured` — the adapter has registered over SignalR —
   while the Service endpoint only appears once the readiness probe passes and kube-proxy picks
   it up; until then the connection is refused. Measured on test-2, under four seconds was not
   enough and produced a 503 for a workload that was already running. Each attempt builds a
   **fresh** `HttpRequestMessage`; reusing one throws `InvalidOperationException` ("the request
   message was already sent"), which is exactly how the first live request failed.
   **Bodies within `MaxBufferedBodyBytes` (32 MB) are buffered once and replayed per attempt**
   (`ByteArrayContent` — a `StreamContent` over the request stream would be disposed with the
   first message). Without that, a body meant exactly one attempt, and the first request after
   hibernation is precisely the one that wakes the workload and lands in the endpoint gap:
   browser uploads failed deterministically while bodyless probes sailed through (AB#4968 field
   report). Oversized or chunked bodies keep the single attempt — silently forwarding a
   truncated body would be worse than a 503.
   A 503 response carrying the `X-Octo-Activator` marker is the **loop guard answering the
   forwarded request** — the response-shaped twin of the refused connection, occurring where
   the forward path re-enters through the ingress (a host-run controller cannot reach
   ClusterIPs) — and gets the same retry treatment instead of being copied to the caller.
5. Failure to wake, or a workload that stays unreachable, answers 503 with `Retry-After` set
   to the wake budget and the `X-Octo-Activator` marker. A forwarded request that comes back
   here is recognised by that header and answered 503 instead of forwarded again.
   **The 503 reflects the request's `Origin`** (plus `Vary: Origin`): the routes and their CORS
   policy live inside the adapter — the thing that is unavailable — so nothing can make the
   real policy call, and without a reflected origin the browser hides status and body entirely
   and surfaces a bare network error ("0 Unknown Error"), which misdirected every diagnosis of
   this path. An error-only response that names workload and tenant the caller addressed leaks
   nothing.

The middleware makes **no authorization decision** and rewrites nothing but the loop-guard
header: the adapter must see what the client sent.

Tests: `Services/WorkloadHostnameIndexTests`.

**Known limitations / follow-ups:** in-process `FromPolling`/`FromMicrosoftGraphEmail`
triggers never idle and silently stop at 0 replicas — such pipelines must move to cron
`PipelineTrigger`s before their workload goes OnDemand (AB#4922 precondition); the
OnDemandCapable trigger-classification validation is not yet implemented (rejecting
`LifecycleMode=OnDemand` on process-bound workloads, AB#4916 §5); the Studio badges of AB#4919
are still open, as is its Dash0 alerting review; the
`octo-eda-adapter` chart still needs the `terminationGracePeriodSeconds` fix that
`octo-mesh-adapter` got.

Repository surface: `SetWorkloadLifecycleStateAsync` / `SetWorkloadLastActivityAsync`
(polymorphic load-then-update over Adapter/Application — an `EntityUpdateInfo` needs the
concrete CK type).

Tests: `Services/WorkloadLifecycleServiceTests/*`, `Services/LifecycleConfigurationServiceTests`,
`Hubs/OperatorHubTests/ReportWorkloadScaleStatusAsyncTests`, the scale section in
`Hubs/OperatorConnectionManagerTests`, `BackgroundServices/WorkloadLifecycleWatchdogTests`,
plus gate assertions in the AdapterService / TriggerManagementService test folders.

## Shared Adapter Leasing — CK model 4.0.0 (Epic AB#4914; AB#4924)

A parent tenant runs an adapter pool its descendants borrow: a pool member is **leased** to a
tenant for the duration of one work item, then released. Design doc:
`docs/concepts/shared-adapter-leasing.md`; the increment plan, the measured bump cascade and
the places where the design does not survive contact with the code:
`docs/concepts/shared-adapter-leasing-implementation.md`.

**Only the CK model has landed so far (4.0.0).** Nothing reads these values yet — validation,
the pool workload, the lease protocol and the scheduler are increments 2–9 of the plan.

### 🔴 This is a MAJOR bump: 3.35.0 → 4.0.0, with a migration

3.36.0 (the first, additive cut) is **withdrawn**. The major is forced by one rename:

| Element | Where | Meaning |
|---|---|---|
| **`Pool` → `DeploymentSite`** | `types/deploymentSite.yaml` (was `pool.yaml`) | it is a deployment *location* (`Default Cloud`, `k8sfirmianstrasse`), never a pool of anything — and the name collided head-on with the new `AdapterPool`. Ships **with** leasing so the estate takes one migration, not two (concept §8, Q16 + Q18). |
| **`Manages`/`ManagedBy` → `Hosts`/`HostedBy`** | `associations/hosts.yaml` (was `manages.yaml`) | a deployment location does not *manage* anything — the Communication Operator does. Renamed in the **same** migration as the type, so the estate migrates once. `Deploys`/`DeployedTo` was rejected: the site does not deploy either, it is deployed *to*. |
| `AdapterPool` | `types/adapterPool.yaml` | a pool of interchangeable adapter processes: `MinReplicas`/`MaxReplicas`, per-member sizing, queue-driven scale-up, lending scope. **One workload with a replica range**, not N `Adapter` entities. |
| `LifecycleMode` `3 Leased` | `enums/lifecycleMode.yaml` | the **borrower's** mode and only ever that: no process of its own. `2 Auto` stays reserved (AB#4984). |
| `PipelineExecutionStatus` `5 Queued` | `enums/pipelineExecutionStatus.yaml` | appended, never inserted — the numeric keys are persisted on every entity. |
| `PipelineExecutionClass` (`Interactive`/`Batch`) | `enums/pipelineExecutionClass.yaml` | on `Pipeline.ExecutionClass`, resolved from the trigger node **on save** and persisted, like `OnDemandCapable`. Keys ordered so ascending == scheduling order; default `Batch`. |
| `AdapterSharingMode`, `LendingAllowedTenantIds`, `LendingMaxConcurrentLeasesPerTenant` | `attributes/adapterLeasing.yaml` → **`AdapterPool`** | lender side. Moved off `Adapter` in the rework: lending is a property of the thing that owns processes. The allow-list **intersects** the resolved subtree and can never widen it. |
| `LentFromTenantId`, `LentFromPoolRtId` | `attributes/adapterLeasing.yaml` → `Adapter` | borrower side, set as a pair. Names the **pool**, not a member — membership is elastic. |
| `QueuedAt`, `LeaseGrantedAt`, `LeaseReleasedAt`, `LeaseWaitMs` | `attributes/attributes.yaml` → `PipelineExecution` | four timestamps, all runtime state. `LeaseGrantedAt..LeaseReleasedAt` ≠ `StartedAt..CompletedAt`: the difference is the per-lease warm-up the pool exists to amortise, and it is also the **billing** span (concept §4b), so it must be stamped on the TTL-expiry and crash paths too. |
| `LeasedFromTenantId`, `LeasedFromPoolRtId`, `LeasedOnMemberId` | `attributes/adapterLeasing.yaml` → `PipelineExecution` | which pool and member served it. |
| **`StartedAt` relaxed to optional** | `types/pipelineExecution.yaml` | a `Queued` execution has no start time (concept §8, Q5). |
| New index on `QueuedAt` | `types/pipelineExecution.yaml` | the queue read is `(status = Queued) ordered by QueuedAt`; it cannot ride the `StartedAt` index. |
| `migrations/3.35.0-to-4.0.0.yaml` + two meta entries | `migrations/` | `ChangeCkType Pool → DeploymentSite`, post-validation at `severity: Error`. |

🔴 **The borrower half and the execution's member reference are attributes, not associations,
and they have to be.** A CK association resolves inside one tenant database; the pool lives in
the lending tenant's and both the borrowing `Adapter` and its `PipelineExecution` live in the
borrower's. Concept §5's "an association from the execution to the assigned member" describes
an edge the engine has no database to store. There is therefore no referential integrity
behind these values — an unresolvable pool is detected at **lease** time, not at write time.

🔴 **The role rename moves THREE identifiers with disjoint blast radii.** `id` drives the
persisted `roleId` in seed data *and* the generated constant
`SystemCommunicationCkIds.RtCkManagesRoleId` → `RtCkHostsRoleId`; `inboundName` drives the
GraphQL field `manages` and `…_ManagesUnion*`; `outboundName` drives `managedBy` and
`…_ManagedByUnion*`. Only the `id` half has persisted data behind it. Measured blast radius:
**20 source files, 38 occurrences** — C# is *one* file (`CommunicationRepository.cs`, 4 call
sites); the operator, `octo-cli`, the MCP server and every C# test are **zero**; 17 of the 38
are hand-written Studio TypeScript. Separately, **59 generated/published artifacts must not be
hand-edited**, including the published catalog JSONs, which keep `Pool`/`Manages` forever.

🔴 **The role rename required a NEW CK engine transform.** `RtAssociation.AssociationRoleId` is
persisted on every edge and none of the seven existing transforms touches it, so renaming the
role without one orphans every deployment-site↔workload edge **silently** — the navigation just
returns nothing. `RenameAssociationRole` was added to `octo-construction-kit-engine`
(+ `-mongodb`); see `docs/ck-model-migrations.md` there. **It must ship before the CK model**:
the CK compiler validates migration YAML against the schema in the *installed* engine package,
so until it is published the model fails with
`Schema validation failed at '/steps/1/transform/type' … "RenameAssociationRole"`.
(`ChangeCkType` already rewrites `originCkTypeId`/`targetCkTypeId` on edges, so the *type* half
of the migration needed nothing new — verified in `TenantRepository`, not assumed.)

🔴 **`rtWellKnownName: CommunicationPool` stays** on the seeded entity. It is identity, not
display text: the service-managed blueprints re-apply against it, and renaming it would create
a *second* deployment site on every provisioned tenant.

### Bump cascade — measured 2026-09-14, not estimated

Scope matters here. In **this `dev` checkout**, **65 files** carry a `System.Communication`
version pin (61 source + 4 `.octo` artifacts), of which **46 source files are blueprint files**.
Estate-wide (all checkouts, including repos not cloned here) it is **203 blocking pins + 7
open-ended**. 🔴 The ~70 pins in the **published catalog** (`meshmakers.github.io` and the local
mirrors under `.octo/local-catalog/.../System.Communication/3/`) are immutable history — they
must keep saying `Pool`/`Manages` and must never be edited. 56 of the 61 hard-block at `<4.0`; the other 5
are open-ended (`[3.6,)`, `[3.12,)`, `[3.13,)`, all in `demo-energy-iq/data/`, which is on
branch `main`) and **silently absorb 4.0.0** — the more dangerous half.

- 🔴 **Blueprint `ckModelDependencies` floors are NOT "satisfiability floors only" under a major bump.** `[3.22.0,4.0)` *excludes* 4.0.0: the blueprint becomes unsatisfiable. Both embedded blueprints are therefore raised to `[4.0,5.0)` and bumped `1.5.0` → **`2.0.0`**, and their seed now creates a `DeploymentSite`. The same applies to the 8 other `blueprint.yaml` files and 36 seed-data files in `octo-construction-kit`, `octo-office-integration` and `meshmakers-app`.
- 🔴 **Both dependent CK models need MAJOR bumps, not minor ones.** `ck-semver-rules.md` classifies "dependency switched to a new major" as Major. `System.Ai-3.7.0` → **4.0.0** (which in turn breaks `System.Ai.Default`'s `System.Ai-[3.1.2,4.0)` pin); `Loxone-4.3.1` → **5.0.0** with its range widened off `[3.31,3.32)`.
- 🔴 **Loxone must move WITH the bump.** The exact pin `[3.31,3.32)` (from `499e55d`, AB#5196) resolves to 3.31.0 and is unaffected by 3.x bumps — which is what makes it dangerous. `CkModelId` is name+version and `CkModelMigrationService` refuses to migrate across names, so a tenant holds exactly **one** version of `System.Communication`: a prod-1 tenant running Loxone cannot take 4.0.0 at all while the pin stands. Third repeat of AB#4696 / AB#4999 / AB#5196.

### 🔴 Two traps specific to this bump

**A major bump renames the generated C# namespace.** `…Generated.System.Communication.v3` →
`.v4` breaks **123** `.cs` files (122 here, 1 in `octo-ai-services`) — far more than the 26
that the `RtPool` → `RtDeploymentSite` rename itself touches. Also renamed:
`AddCkModelSystemCommunicationV3()` → `…V4()`, and — because the blueprint went to 2.0.0 —
`AddBlueprintSystemCommunication{Release,MainLatest}V1()` → `…V2()`.
`octo-sdk`'s `CommunicationCkTypeIds.Pool = "System.Communication/Pool"` is a `const string`,
so it compiles fine and is simply **wrong at runtime** — grep for it explicitly.

**A stale build artifact makes the rename look free.** The CK output directory holds one
compiled model per major, and the source generator emits `Rt*` classes for *every* file it
finds there. A leftover `ck-system.communication-3.yaml` next to the new `-4.yaml` makes it
emit `RtPool` **and** `RtDeploymentSite`: the repo compiles clean, all 917 tests pass, and
`RtPool` silently refers to a type the installed model no longer defines. Deleting
`bin/DebugL` + `obj/DebugL` turned "0 errors" into **506**. Never trust a green *incremental*
build of a major CK bump — validate after `git clean -xfd`.

**Migration entry points.** `CkModelMigrationService.FindBridgedMigrationPathAsync` picks
candidate entry points as "every `fromVersion` **greater than** the installed version", so the
entry hangs off `3.35.0` (not off the 3.1.1 chain end, which would drag modern tenants back
through four obsolete DataFlow scripts). A tenant on the **withdrawn 3.36.0** sits above every
entry point, falls through to the post-chain schema-only bridge and would **skip the rename
silently** — 3.36.0 is in `dev/.octo/local-catalog`, so `migration-meta.yaml` carries a second,
deletable `3.36.0 → 4.0.0` entry pointing at the same idempotent script.

### Out of scope of the rename, deliberately

The operator's `CommunicationPool` **CRD** (its Kind is a hardcoded literal; the link to the CK
entity is by RtId, not by name), the Helm value names `operator.{autoManagePools,poolNamespace,defaultPoolName}`,
the REST route `{tenantId}/v1/pool`, the `octo-cli` `GetPools`/`DeployPool`/`UndeployPool`
commands and the MCP `get_pools`/`undeploy_pool` tools. The GraphQL type
`SystemCommunicationPool`, however, is derived from the CkTypeId and **renames itself** on
publish — ~66 frontend files across the Studio, `octo-frontend-libraries` and `meshmakers-app`
break without anyone editing them, so codegen must be re-run in the same train.

## Leasing: lending scope and execution class (AB#4924 increments 2 + 4)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §4 and §6.

### Lending scope — `ITenantLendingScopeResolver`

Resolves which tenants an `AdapterPool` may lend to. `Services/TenantLendingScopeResolver.cs`,
singleton, 30 s cache (the walk opens an admin session per descendant and must not run per work
item).

🔴 **It does not call `GET {tenantId}/v1/tenants/descendants`.** This service has no
`Sdk.ServiceClient` reference and a background scheduler has no caller token to forward. It
re-implements the same breadth-first `ITenantContext` walk in process.

🔴 **Copying `GetDescendants` verbatim is necessary but NOT sufficient.** Its `visited` set stops the
walk from looping; it does **not** stop an ancestor from appearing in the result. With a corrupted
registry (parent `P` lists child `C`, and `C` also lists `P`), the walk from `C` adds `P` — never
visited, so nothing rejects it. For a listing endpoint that is tolerable; here the same set decides
whether one tenant may execute work **inside** another, so it would be an upward privilege
escalation. The resolver subtracts the lender's ancestor chain, walked via `ParentTenantId` — a path
independent of the child records a cycle corrupts. `ARegistryCycleCannotMakeAnAncestorBorrowable`
fails, **and nothing else does**, if that subtraction is removed.

Also: an unresolvable lender lends to **nobody** (never a wider scope), a root tenant has **no**
siblings, and `LendingAllowedTenantIds` intersects — it can never widen past the subtree.

### Deploy guards (`PoolService.EnsureLeasingConfigurationIsValidAsync`)

Same enforcement rationale as the AB#4984 `LifecycleMode` block: all of this is plain CK author
configuration with no service-layer hook, so the deploy is the net. `Leased` requires
`OnDemandCapable` (reusing `IWorkloadOnDemandCapabilityService`, never a duplicated trigger list);
`LentFromTenantId`/`LentFromPoolRtId` are validated as a pair and against the resolver;
`LentFrom*` set without `Leased` is an error, because a value that silently does nothing reads like
the workload borrows a process when it runs its own; an `AdapterPool` may never itself be `Leased`.

Read surface: `GET {tenantId}/v1/adapter/lending?adapterPoolRtId=…` → `AdapterLendingScopeDto`.

### Execution class — `IPipelineExecutionClassService`

The trigger node declares its class via `[NodeExecutionClass]` (SDK), the reflection descriptor scan
picks it up, and it travels on `NodeDescriptorDto.ExecutionClass` — appended and defaulted, the same
wire-compat trick `RequiresRunningProcess` uses.

🔴 **Persisted in the same update as the definition it was derived from**
(`SetPipelineDefinitionAsync`), so the class and the YAML can never disagree. This is *not* where
`OnDemandCapable` is written — that is per-workload, after deploy, best-effort, and is not a
pipeline-save hook at all. The redeploy path (unchanged YAML, possibly different adapter
descriptors) uses the single-field `SetPipelineExecutionClassAsync`; rewriting the definition there
would break the "deploy must not persist a definition it was not given" invariant, which a test pins.

🔴 **`TryGetTriggerNodes`, not `TryGetAllNodes`.** The latter flattens `triggers:` and
`transformations:` — correct for on-demand capability, wrong here: the class describes how the work
*arrived*, so only a trigger may declare it.

⚠️ **Known wart, adopted deliberately.** Node descriptors are never persisted and are null for any
adapter that has not registered in this process, so `KnownInteractiveTriggerNames` mirrors
`KnownProcessBoundTriggerNames`. Bounded: two entries, a descriptor always wins, and drift degrades
to `Batch`, never to a wrong `Interactive`.

⚠️ **Pre-existing AB#4984 defect found while doing this:** `octo-adapter-loxone`'s
`LoxonePollTrigger@1` is process-bound but carries no `[NodeRequiresRunningProcess]` and is **missing
from `KnownProcessBoundTriggerNames`** — a workload whose only trigger is that node is classified
on-demand capable and would be hibernated. `FromLoxoneStateChange` is listed; this one was missed.
Needs its own work item.

⚠️ A pipeline edited to a different trigger changes class **on save**, so every surface must read the
current value rather than cache it — the same caveat `OnDemandCapable` carries.

## Deploying a pipeline to a `Leased` adapter (AB#4924)

🔴 **`DeployPipelineAsync` has two named halves, and only the first is universal.**

| Half | Method | Runs for |
|---|---|---|
| validate + persist | `ValidateAndPersistPipelineAsync` | **every** adapter |
| push (project config, send over SignalR, wait for the ack) | inline in `DeployPipelineAsync` | dedicated adapters only |

`DeployLeasedPipelineAsync` is the leased entry point: shared half → `DeploymentState` →
capability refresh → audit event. Anything added to the deploy verb belongs in the shared half
unless it is genuinely about pushing bytes to a pod.

**Why this exists.** The method used to wrap its whole body in
`adapterTenant.AdapterById.TryGetValue(...)`, which is only populated for adapters holding an open
`/{tenantId}/adapterHub` connection. A `Leased` adapter never has one, so every leased deploy
answered 404 — and, far worse, **none of the four gates ever ran** on that path
(schema validation, AB#4984 process-bound triggers, AB#5027 mandatory service account, AB#5128
elevation authorization). The pipeline was then executed by a borrowed process that had checked
none of them.

**Three deliberate differences on the leased path:**

- **No AB#4918 wake gate.** It was already a no-op, but only because `Leased != OnDemand` — a
  coincidence, not a decision. Now an explicit branch: a leased adapter has no workload of its own,
  and the pool is a *different tenant's* workload. A borrower must not be able to scale the lender
  by pressing Deploy.
- **`EnsurePipelineIsOnDemandCompatible` fires on `Leased` too**, with its own message factory
  `AdapterServiceException.PipelineNotLeasable` (different remedy: an OnDemand workload can go back
  to AlwaysOn, a leased one has no process to switch on). `PoolService` enforces the same rule at
  *workload* deploy over the whole pipeline set; without this per-pipeline arm a new process-bound
  pipeline on an already deployed leased adapter would only be caught at the next workload deploy.
- **`SetPipelineDeploymentStateAsync` writes `Deployed` directly, never `Pending`.** `Pending` means
  "a push is in flight"; there is none, so the state would never leave it. Persisting IS the
  deployment here — the next lease runs exactly what was written. The status message names the
  difference.

### `IAdapterNodeCapabilityService` — whose descriptors answer

```csharp
AdapterNodeCapabilities Resolve(string tenantId, RtEntityId adapterRtEntityId, RtAdapter? adapter);
```

One seam for three deploy-time questions that each used to reach into `AdapterById` on their own:
schema validation, process-bound classification, and the execution class.

- dedicated → the adapter's cached `NodeDescriptors` / `PipelineSchemaJson`;
- `Leased` → `IAdapterPoolConnectionManager.TryGetPoolCapabilities(LentFromTenantId, LentFromPoolRtId)`.

🔴 **A leased adapter never falls back to its own adapter-cache entry.** An adapter switched from
`AlwaysOn` to `Leased` can still have a stale entry describing a process that no longer exists;
"nothing known" degrades to the name-based fallback (wrong-but-conservative), stale descriptors
would be wrong-and-confident.

**Which member answers:** draining members last (during a rollout they are the outgoing version),
then ordered by member id — deterministic, because a pipeline's persisted execution class must not
depend on dictionary enumeration order.

⚠️ **Per controller instance.** A SignalR connection lives on one pod, so with >1 replica the pod
handling the deploy may hold no member of the pool and answers "nothing known". Same limitation as
the member listing and the scale-up evaluation.

⚠️ **Still blind:** `WorkloadOnDemandCapabilityService.EvaluateAsync` reads only `AdapterById`, so
for a leased adapter it classifies with `descriptors = null` (name-based fallback) both in
`PoolService.EnsureLeasingConfigurationIsValidAsync` and in the persisted display value.
`DeployDataFlowAsync` has no leased branch at all — it still falls through the `AdapterById` lookup.

### Pool members report node descriptors

`PoolMemberRegistrationDto.NodeNames` (a `string` list that no member ever filled and no controller
ever read) was **replaced** by `NodeDescriptors` (`IReadOnlyList<NodeDescriptorDto>`) plus
`PipelineSchemaJson` — the same values a dedicated adapter sends on
`RegisterAdapterWithSchemaAsync`, built by the shared
`AdapterNodeDescriptorProjection` in `octo-communication-sdk` rather than a second projection.

🔴 **Source-breaking in `octo-sdk`, wire-compatible in both directions** (SignalR's JSON protocol
ignores unknown members and defaults absent ones, and nothing ever populated `NodeNames`). Skew rule
as for every hub contract: `octo-sdk` publishes before `octo-communication-sdk` and this repo.

## Leasing: `AdapterPool` deployment, scaling and the watchdog exclusion (AB#4924 increment 5)

An `AdapterPool` is **one workload with a replica range**, not N `Adapter` entities. Nothing new
was invented for it: it rides the existing `DeployableWorkload` path (one workload ↔ one helm
release ↔ one `RtDeployableWorkload`) and the AB#4917 `ScaleWorkloadDto` verb. KEDA stays rejected
for the same reasons as on the on-demand path.

| Piece | Where |
|---|---|
| `WorkloadTypeDto` mapping (one place, four call sites) | `Services/WorkloadWireMapping.cs` |
| Pool `DeploymentState` writer | `CommunicationRepository.SetAdapterPoolDeploymentStateAsync`, `PoolService.SetWorkloadDeploymentStateAsync` |
| `replicaCount` + per-member sizing as chart values | `PoolService.AppendAdapterPoolMemberOverrides` |
| Scale verb with the `MinReplicas` floor | `PoolService.ScaleAdapterPoolAsync` → `WorkloadLifecycleService.ClampToAdapterPoolRange` |
| `POST {tenantId}/v1/pool/workloads/adapter-pool/scale` | `TenantApi/v1/Controllers/PoolController.cs` |
| Idle-watchdog exclusion | `BackgroundServices/WorkloadLifecycleWatchdogBackgroundService.SweepTenantAsync` |
| Activator index scoping | `Services/WorkloadHostnameIndex.RefreshAsync` |

Deploy and undeploy need no new endpoints — `POST pool/workloads/deploy` / `…/undeploy` are typed on
`RtDeployableWorkload` and a pool is one. What they *did* need is a `DeploymentState` writer: the
`switch` in `SetWorkloadDeploymentStateAsync` had arms for `RtAdapter` and `RtApplication` only, so a
pool deployed, stayed at its default state, never showed as deployed in Studio, and then failed
Undeploy with "already not deployed". The same hole existed in
`CommunicationRepository.UpdateWorkloadPolymorphicAsync`, where every lifecycle write against a pool
threw `WorkloadNotFound`.

### 🔴 The idle watchdog must not see a pool

`WorkloadLifecycleWatchdogBackgroundService` decides idleness from the workload's **own** pipelines'
`LastExecutionAt`. A pool has none: every pipeline it runs belongs to a borrower in a different
tenant database, which that query cannot see and a CK association cannot reach. Left alone the
watchdog reads a perfectly busy pool as "no pipelines, no activity, idle since forever" and drains
it to zero, taking every borrower's work with it — and the failure looks exactly like a correctly
working idle timeout. `SweepTenantAsync` therefore skips `RtAdapterPool` explicitly, before the
`LifecycleMode` filter, at every mode.

Two things about that guard are worth knowing before anyone tidies it away:

- For a **Running** pool it is currently redundant with the `workload is not RtAdapter` check
  further down, which exists to skip Applications and catches pools as a side effect. A side effect
  is not a safeguard for a cross-tenant outage, and the day the watchdog is extended to every
  `DeployableWorkload` that side effect disappears.
- For a pool stuck in **`Waking`** it is *not* redundant. `ReconcileWakingAsync` runs **before** the
  `RtAdapter` check and writes `LifecycleState = Hibernated` plus an error event about a wake that
  never existed. That path was reachable. `AdapterPoolStuckInWaking_IsNotRevertedByTheWatchdog` is
  the test that holds it, and removing the guard alone fails that test and no other.

The pool owns its members' lifecycle instead (concept §4a): `MinReplicas` is the floor,
`IdleTimeoutMinutes` and queue pressure decide what happens above it (the queue arrives in
increment 7).

### `MinReplicas` is enforced twice, on purpose

The watchdog exclusion is a *filter* — it protects against one caller. `RequestScaleAsync` clamps
every pool scale request into `MinReplicas..MaxReplicas` regardless of who asked, because a guard
that only covers the caller you thought of is the guard that is missing during the incident.
`MinReplicas = 0` is a legitimate, deliberate choice (a scale-to-zero pool where every burst pays
one cold start), so the floor is the declared value and never a hardcoded 1.

### What a pool member is allowed to pick up

`ReceivesClusterSecrets` is forced **false** for a pool on the way to the wire, whatever the entity
says. Those are the cluster's *shared* Mongo / CrateDB credentials — one user, every tenant's data
behind it. A pool member executes work for tenants other than the one that owns it, and the lease is
the mechanism that grants it exactly one tenant at a time; a standing credential to all of them makes
that mechanism decorative. The operator refuses the same thing independently in
`WorkloadReconciler.AppendClusterSecrets`: two gates, one on each side of the wire, because either
alone is one edit away from silence.

What a member *does* get is the RabbitMQ command bus, the TLS trust anchor, and its own per-release
secret in the platform namespace — none of which carries tenant authority. Tenant-scoped access
arrives with a lease and leaves with it (increment 6).

### The activator index skips pools

`WorkloadHostnameIndex` publishes an address built from the release name alone
(`ActivatorWorkloadAddressTemplate` defaults to `http://{release}`), which resolves in the
**controller's** namespace. A pool runs in the platform namespace, so an entry for one would point
the activator at a Service that is not there — or at a same-named one that is. A pool is reached by
being leased, never by an inbound request, so it is skipped before the hostname map is built. This
is the only place in the controller that still assumes one namespace for everything.

## Leasing: the pool queue and its scheduler (AB#4924 increment 7)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §9.

| Piece | Where |
|---|---|
| One queue per pool, rotation, priority, cap, TTL reaper, scale-up, queue projection, cancel | `Services/LeaseSchedulerService.cs` |
| Scheduling round (default every **5 s**) | `BackgroundServices/LeaseSchedulerBackgroundService.cs` |
| Lease reaper (on the cleanup service's cadence, next to the AB#4280 stuck reaper) | `BackgroundServices/ExecutionCleanupBackgroundService.cs` |
| `Queued` is written here and nowhere else | `TriggerManagementService.StartExecutePipelineAsync` → `CommunicationRepository.EnqueueExecutionAsync` |
| `LeaseReleasedAt`, terminal-state fallback, interrupt + re-queue, drain | `Services/LeaseService.cs` |
| `GET/DELETE {tenantId}/v1/adapterPool/{id}/queue` — the shared contract all three surfaces consume | `TenantApi/v1/Controllers/AdapterPoolController.cs` |

🔴 **A `Leased` adapter never reaches the execute queue.** `StartExecutePipelineAsync` branches
*before* the AB#4918 wake gate: there is no workload to wake, and the per-pipeline execute queue has
no consumer, so a send would be a message nobody reads. A **dry run** deliberately does not queue —
it is a synchronous answer to a caller holding the request open, and parking it behind a rotation
would turn "validate this pipeline" into something that returns minutes later.

🔴 **Fairness is round-robin across tenants, never global FIFO — and the class never crosses a
tenant boundary.** Both halves are load-bearing and both have a test that a global FIFO would fail:
`FairnessTests` arranges the starving tenant's work to be **older** than everyone else's, so arrival
order and fair order disagree. A fairness test a global FIFO would also pass proves nothing, and a
silent regression to global FIFO is the most likely way this scheduler stops being fair.

🔴 **The admission gate.** `ILeaseService.GrantLeaseAsync` takes an optional callback that runs after
an idle member is reserved and before the lease is pushed. The scheduler claims the work item there.
That position is forced: the claim writes `LeasedOnMemberId`, which needs a reserved member, and it
has to land before the member is handed anything, so a second controller pod that claimed the same
item first can stop this one from dispatching it. A gate that declines **or throws** puts the member
back — otherwise every lost race costs the pool a member.

🔴 **The claim is latched at the MongoDB filter level**, not merely checked. `CreateConditionalUpdate`
with `AttributeNewerThanGuard("attributes.leaseGrantedAt", DateTime.MinValue)` means "apply only
while unclaimed". The pre-read is not enough: two pods can both read `Queued` before either writes.
A conditional update reports nothing about whether it applied, so the claim re-reads and confirms.

🔴 **A re-queue is a NEW execution entity.** Concept §6 wants the previous attempt `Interrupted` *and*
the work re-queued; one entity cannot be both. Each attempt is one entity, so the interrupted one
keeps its own `LeaseGrantedAt`/`LeaseReleasedAt` span (a billing input, §4b) and the retry gets its
own `QueuedAt` and therefore its own honest `LeaseWaitMs`.

🔴 **`LeaseReleasedAt` is stamped on every release path**, TTL and crash included. A span stamped
only on the happy path silently under-bills exactly the failures a borrower did pay for. The release
is also the controller's **last-resort** completion signal: an execution still `Running` when the
member hands the process back is completed/failed from the lease result, but one that already
reached a terminal state through the normal adapter path is left strictly alone.

### 🔴 A `Queued` execution and the five sweeps — measured, and it corrected the plan

Three sweeps filter on an **exact** status (`FailStuckExecutionsAsync`,
`TimeoutStaleExecutionsAsync`, `FailOrphanedExecutionsForAdapterAsync`), so `Queued` is excluded by
construction and no clause was needed — contrary to the plan, the "reaper half" was already done.

Two do not: `GetTerminalExecutionsOlderThanAsync` (`Status != Running`) and
`DeleteOldExecutionsAsync` (**no status filter at all**). Both key off `StartedAt < cutoff`, both
**erase** what they match, and a queued execution has no `StartedAt`. On paper that is silent work
loss. It is not, and the reason is not in this repo: **MongoDB's range operators are type-bracketed**
— `$lt` against a date never matches a null or missing value, even though null sorts below every
date in BSON *ordering*. Removing either explicit `Queued` exclusion leaves
`FailStuckAndOrphanedExecutionsTests.AQueuedExecutionIsInvisibleToEveryReaperAndSweep` green. They
are kept so the invisibility is a property of the query rather than of the storage engine.

### 🔴 The tick is a floor, not the cadence (AB#4924 §9.6)

`LeaseSchedulerIntervalSeconds` used to be both, and that made it the throughput ceiling. Measured on
the second local walk: grants came **5.11–5.14 s apart for runs of 0.43–1.36 s**, so the member was
idle **75–92 %** of the time and no member could exceed roughly **twelve executions a minute** however
small the work was. For `Interactive` it meant a Studio Execute paid up to a full interval before
anything started.

`ILeaseSchedulerWakeSignal` lets the two events that can make a grant possible pull the next round
forward. **Two, not one** — and the second is the one that was missing from the original write-up:

| Event | Where | What it changes |
|---|---|---|
| a lease is **released** | `LeaseService.ReleaseLeaseAsync` | a member became available for queued work |
| work is **enqueued** | `TriggerManagementService.StartExecutePipelineAsync` | work arrived while a member may be idle *already* |

The release side alone looks sufficient and is not: with two borrowers enqueuing continuously
somebody is always working, which is exactly why the round-robin walk never exposed the enqueue case.
A Studio Execute against an idle pool is the case that matters, and no release happens there.

Three properties, each load-bearing:

- **Coalescing.** The semaphore has capacity **one**, so at most one round is ever pending. Six
  executions enqueued in 320 ms — the shape the fairness walk produced — must cost one extra round,
  not six: a round reads the queue of *every* borrower of the pool.
- **A minimum gap** (`LeaseSchedulerWakeMinIntervalMilliseconds`, default 250, 0 disables) between
  requested rounds, measured from the **start** of the previous round so a long round is never
  followed by a wait it has already served. Without it a burst, or a pool whose members release
  continuously, runs rounds back to back and makes the scheduler the most expensive thing in the
  controller — the outcome the interval was chosen to avoid.
- **The periodic tick stays.** It covers what no signal reaches: TTL expiry, topology changes, and the
  multi-pod case where the event arrived at a *different* controller instance. Same reasoning as
  `SweepStalePools` — several controllers act independently and each sees a different subset, so
  nothing may depend on having observed an event.

🔴 **A `Drained` release wakes nobody.** A draining member takes no further work (concept §6 drains
and restarts a member whose post-lease cleanliness is unproven), so a round on its account would read
every borrower's queue and grant nothing.

🔴 **The request is a pending permit, not an event.** A release landing while a round is still running
has nobody waiting at that instant; dropping it would leave the member it just freed idle until the
tick — precisely the latency this removes.

The gap is measured with `Stopwatch`, not the wall clock and not an injected `TimeProvider`: it is a
duration, so it must be monotonic, and `TimeProvider` is registered nowhere in this service — taking
it as a dependency would be a startup failure the compiler cannot see.

Tests: `Services/LeaseSchedulerWakeSignalTests` (coalescing, the pending permit, re-arming, disposal),
the wake cases in `Services/LeaseServiceTests/ReleaseAndDisconnectTests` (clean, failed, drained,
unknown lease) and in `Services/TriggerManagementServiceTests/LeasedAdapterEnqueueTests` (leased
enqueues wake, a manual adapter does not).

### Scale-up: the window is derived, not invented

`LeaseScaleUpAveragingWindowSeconds` defaults to **0** = "derive per pool from that pool's own
`ScaleUpQueueWaitSeconds`". Q14 defers the window to measurement, so a hard-coded constant would be
the one thing the decision rules out; the derived value is the author's own declaration of how long
a work item may wait. The window applies to the **depth** signal only — the wait signal is already
time-integrated. Samples are retained for **twice** the window: pruning at exactly the window drops
the sample that proves the history is long enough, and the signal then never fires.

### ✅ The lease carries the work (§9.9 / D4)

This section used to say a lease carried `ExecutionId` and nothing else, so a member that received
one had nothing to run. Resolved: `LeaseDto` carries the pipeline rtId, the input and the projected
`PipelineConfigurationDto`, and `octo-mesh-adapter` implements the work item. `LeaseService` refuses
the lease — **before a member is reserved** — when the pipeline cannot be projected.

## Leasing: metrics, alerts and rollout operability (AB#4924 increment 9)

Plan: `docs/concepts/shared-adapter-leasing-implementation.md` §11. Alert rules live in
**`meshmakers-infrastructure`**, not here (§11.5).

`Services/AdapterLeasingMetrics` (static, mirroring `WorkloadLifecycleMetrics`) emits 18 instruments
on the meter **`Meshmakers.Octo.Communication`** — the same meter AB#4919 uses, so
octo-common-services' `ObservabilityBuilder` already registers it and nothing had to be wired up.

| Instrument | Kind | Purpose |
|---|---|---|
| `octo.lease.held.duration` | histogram (s) | `LeaseGrantedAt` → release; the span that prices the borrower |
| `octo.lease.work.duration` | histogram (s) | what the member reported it spent running the pipeline |
| `octo.lease.overhead.duration` | histogram (s) | held − work — **the amortisation number** |
| `octo.lease.granted.count` / `octo.lease.queue.wait` | counter / histogram (s) | served count and wait distribution per borrowing tenant — the two halves of fairness |
| `octo.lease.enqueued.count` | counter | the queue's in-rate against `granted.count` |
| `octo.lease.queue.depth` | gauge | waiting items **per pool and per borrowing tenant** |
| `octo.lease.queue.oldest_wait` | gauge (s) | what the wait signal thresholds |
| `octo.lease.pool.members` | gauge | members on *this* controller pod, by `available` / `leased` / `draining` |
| `octo.lease.scaleup.signal` / `.window` / `.count` | gauge / gauge (s) / counter | the signals, the window in force, and the decision they produced |
| `octo.lease.pool.undersized` | gauge | 1 while the pool should grow and is at `MaxReplicas` — the alertable condition |
| `octo.lease.refused.count` | counter | by `octo.lease.stage` and `octo.lease.refusal_reason` |
| `octo.lease.released.count` / `.interrupted.count` / `.requeued.count` / `.member_drained.count` | counters | outcomes, mid-lease failures, at-least-once retries, member churn |

Tags on every series: `octo.tenant.id` (the **borrowing** tenant), `octo.pool.tenant_id`,
`octo.pool.rt_id`, `octo.pool.name`.

Five decisions worth keeping:

- 🔴 **The work span is measured by the MEMBER and travels on the release**
  (`LeaseResultDto.WorkDurationMs`, set by `AdapterPoolClient`). The controller cannot measure it:
  it stamps `StartedAt` at claim time, so its own view of the two spans is identical by construction
  and the overhead would read as zero forever. Null when the work item never ran — no fabricated
  zero, which would say the pool spent 100 % of the lease on overhead.
- 🔴 **A refusal reason is an enum, not the message.** `LeaseGrantResult.Reason`
  (`LeaseRefusalReason`) is the label; `StatusMessage` names the tenant and the adapter and stays
  free text. Every refusal path goes through `LeaseService.Refuse(...)`, so a reason added later
  cannot be forgotten on the metric.
- 🔴 **The member id is never a label.** Bounded at any instant by `MaxReplicas`, unbounded over
  time — and draining is precisely the path that restarts members, so the label set would grow
  fastest while the drain counter fires. Drains and expiries are counted per **pool**.
- **`octo.lease.pool.undersized` publishes the answer, not the inputs** — same pattern, and the same
  reason, as `octo.workload.offline_unexpected`. ⚠️ It is computed from the members on *this* pod,
  inheriting increment 7's per-instance ceiling arithmetic; `max by (pool)` in the alert is a
  mitigation, not a fix.
- ⚠️ **Log volume.** A lease is frequent. Depth, wait, member states and signals are metrics and are
  logged nowhere; `ScheduleForPoolAsync`'s "no idle member" line fires once on the **transition**
  into exhaustion rather than on every five-second round.

**Pool observations expire, they are not evicted.** `SweepStalePools` drops a pool no round has
observed for three minutes. "Evict everything this round's topology did not contain" is wrong with
more than one controller pod: each sees a different subset.

**Traces: a lease cannot be traced end to end.** `ObservabilityBuilder` has no `AddSource(...)` at
all and nothing in the estate propagates trace context over SignalR. Correlation is by lease id and
execution id in the logs. See plan §11.3 before anyone builds half of it.

Tests: `Services/AdapterLeasingMetricsTests` (the instrument shapes),
`Services/LeaseServiceTests/LeasingMetricsTests`,
`Services/LeaseSchedulerServiceTests/SchedulerMetricsTests`, and the two increment 9 cases in
`Services/TriggerManagementServiceTests/LeasedAdapterEnqueueTests`. 🔴 All four carry
`[NotInParallel(nameof(MeterListener))]`, and so does `WorkloadLifecycleMetricsTests`: a listener
started or disposed on one thread mutates the subscription lists another thread's `Add` is walking,
and the symptom is a measurement that is never delivered — one short of sixteen, once in a few dozen
runs. Each harness also forces the metrics class constructor before starting its listener.

## Pipeline Service Account — mandatory execution identity (Epic AB#4979; AB#5027 phases 1 + 2)

Pipeline execution runs under a real identity instead of anonymously. Granularity:
**one service account per adapter as the default, optionally overridden per pipeline**;
a pipeline without a resolvable account is refused at deploy time.

**CK model (3.31.0, additive minor).**

| Change | File | Why |
|---|---|---|
| New association role `PipelineServiceAccount` (`PipelineServiceAccountOf` ← N / `PipelineServiceAccount` → ZeroOrOne) | `ConstructionKit/associations/pipelineServiceAccount.yaml` | Dedicated role, **not** the generic `Uses`. A second origin type on `Uses` flips the inverse `UsedBy` view on `Configuration` and the CK engine then drops `SystemConfigurationInterface` from the generated GraphQL schema for every `Configuration` subtype — the exact failure already documented on the `HelmRepository` link in `types/deployableWorkload.yaml`. |
| `Adapter.PipelineServiceAccount → ServiceAccountConfiguration` | `ConstructionKit/types/adapter.yaml` | Declared on `Adapter`, not on `DeployableWorkload`: only Adapters execute pipelines. |
| `ClientSecret` marked `isRuntimeState: true` | `ConstructionKit/attributes/finApiConfiguration.yaml` | See below. |

**Multiplicity is `ZeroOrOne`, i.e. optional — deliberately.** The obligation ("every mesh
adapter MUST have a service account") is enforced by the deploy guard, not by the model.
A mandatory `One` would (a) reject every existing `Adapter` entity on the next
blueprint re-apply / `ImportRt`, before any tenant has an account at all and before the
provisioning phase exists, and (b) reclassify the change as a **major** CK bump
(`octo-construction-kit-engine/docs/ck-semver-rules.md`: a new association referencing a
role with multiplicity `One` is Major). No migration script is needed — the engine's
no-migrations bridge synthesises a schema-only no-op step for purely additive bumps, so
`migration-meta.yaml` stays at 3.1.1.

**No blueprint bump.** `System.Communication.{Release,MainLatest}` keep `1.5.0` and their
`ckModelDependencies` floor `[3.22.0,4.0)`: their seed creates neither a
`ServiceAccountConfiguration` nor the new edge, and the floor is a satisfiability floor
only — the install target is the embedded `IServiceManagedCkModelDescriptor` version.
The CK bump alone is what carries the change to tenants.

**`ClientSecret` is runtime state now.** A client secret is issued by the identity provider
(or pasted in by an admin) *after* the entity exists — a blueprint can only ever seed a
placeholder, and before the marker every re-apply overwrote the live secret with it.
Blast radius: the attribute definition is **shared**, so `ServiceAccountConfiguration`,
`FinApiConfiguration` and `MicrosoftGraphConfiguration` all keep their secret across a
re-apply. Correct for all three; the consequence is that a blueprint can no longer *change*
a `ClientSecret` on an existing tenant — rotate through Studio / the provisioning path.
First install is unaffected: preservation only rewrites an incoming value when the target
entity already exists **and** already holds a value for the attribute
(`ImportRtModelCommand.PreserveAttributesForEntity`), so a fresh tenant still gets the
seeded value. The marker needs the version bump to land — a same-version re-import is a
no-op (`ImportCkModelAsync` short-circuits; see the `IsDebuggingEnabled` 3.22.0/3.23.0
cautionary tale above). Explicitly **not** marked: `IssuerUri` and `ClientId` are
per-environment author configuration a blueprint must be able to correct;
`TenantId` is `${System}/TenantId` and can only be marked in the System model.

**Resolution** — `IPipelineServiceAccountResolver` / `PipelineServiceAccountResolver`
(singleton, registered next to the capability service in `Program.cs`):

1. the pipeline's own `ServiceAccountConfiguration` on the generic `Uses` role — the
   per-pipeline override. Needs no model change; Pipeline→Configuration already exists.
   Several linked accounts are picked by ordinal `RtId` so every pod and every redeploy
   agrees; a warning is logged.
2. otherwise the executing adapter's `PipelineServiceAccount` edge
   (`ICommunicationRepository.GetServiceAccountForAdapterAsync`).
3. otherwise `PipelineServiceAccountResolution.Unresolved`.

`ResolveAsync` takes an already-known adapter (the deploy paths have it);
`ResolveForPipelineAsync` walks the `Executes` edge itself and treats "pipeline has no
adapter" as unresolved rather than an error — that condition belongs to the deploy paths.

**Projection into the pipeline configuration.** Configurations reach a pipeline
*exclusively* through that pipeline's own `Uses` edges: `GetConfigurationsByPipelineAsync`
traverses `Uses` outbound from the pipeline, `CreatePipelineConfigurationAsync` turns the
result into `ConfigurationDto`s keyed on `RtWellKnownName`, and the adapter materialises
that into a per-pipeline `GlobalConfiguration` dictionary. There is no adapter-level
configuration scope, so the adapter-wide default has to be **mixed in controller-side** —
done in `CreatePipelineConfigurationAsync` (which now takes the adapter's `RtId`), only
when the pipeline has no service account of its own, and skipped when the same `RtId`
**or** the same `RtWellKnownName` is already present (a duplicate key throws on the adapter).
No SDK/wire change and no new DTO shape, hence no adapter/controller version skew.
**The adapter caches `GlobalConfiguration` at pipeline registration — changing the linked
service account only takes effect after the pipeline / data flow is redeployed.**

**Deploy guard** (`AdapterService.EnsurePipelineHasServiceAccountAsync`, modelled on the
AB#4984 gate): called from `DeployPipelineAsync` and `DeployDataFlowAsync`, in both cases
**before the first state write**, so a rejected deploy leaves nothing half-applied. It
throws `AdapterServiceException.PipelineHasNoServiceAccount` — deliberately the same
exception family as the AB#4984 gate (→ HTTP 404 in `PipelineController`, not 400): both
gates must surface identically in the Studio, which renders `ErrorResponse.ErrorMessage`
regardless of status. The message names cause, work item, pipeline, adapter and **both**
ways out (link the account on the adapter, or set a per-pipeline override).

**Not guarded in `PoolService.DeployWorkloadAsync`** (documented in place next to the
AB#4984 lifecycle validation): that method also validates `Application`s, which execute no
pipelines; the helm deploy of the adapter pod is the step that must succeed *before* a
service account can be provisioned onto it, so gating it would be circular and would make
every existing tenant's adapter undeployable ahead of the provisioning phase; and an
adapter with no pipelines is harmless. Enforcement stays tight either way — no pipeline
reaches an adapter without a resolvable identity. Phase 2 does the *opposite* on that path:
`DeployWorkloadAsync` **provisions** the adapter's account (best effort, after the deploy
notification) instead of gating on it — see below.

Phase 1 tests: `Services/PipelineServiceAccountResolverTests` (override wins, adapter default,
nothing linked, no adapter query when an override exists, deterministic multi-link pick,
`ResolveForPipeline` via `Executes`, pipeline without adapter),
`Services/AdapterServiceTests/DeployPipelineServiceAccountGateTests` (rejection message
contents, negative proof that neither the adapter nor any state write is reached, data-flow
path, both happy paths), `Services/AdapterServiceTests/PipelineServiceAccountProjectionTests`
(default projected exactly once, no duplicate when already linked, untouched when the
pipeline has an override, coexistence with other configurations).
`AdapterServiceTestsBase` arranges a provisioned tenant by default
(`DefaultAdapterServiceAccount` + `DefaultAdapterServiceAccountDto`) so the pre-existing
suites keep testing what they were written for; gate tests re-stub the repository to null.

### Phase 2 — provisioning (must ship together with phase 1)

🔴 **Phase 1 alone bricks every tenant.** Before AB#5027 nothing on the platform created a client
*secret*: dynamic client registration deliberately produces public clients
(`RequireClientSecret=false`), the operator's `CreateSecret` paths are Kubernetes secrets, and client
mirroring only copies. So without phase 2 the deploy guard refuses **every** pipeline deploy in
**every** tenant. Never release one without the other.

**`IPipelineServiceAccountProvisioningService` / `PipelineServiceAccountProvisioningService`**
(singleton, registered next to the resolver in `Program.cs`). Per adapter, in this order:

1. Resolve the adapter's linked account; if there is none, look one up by the **deterministic
   well-known name** `pipeline-service-account-{adapterRtId}` — that is what makes a second run
   adopt its own earlier work instead of creating a second credential entity (and what repairs a
   lost edge). The rtId, not the name, is the key: names are editable.
2. Decide the secret. A **complete** existing configuration (issuer + client id + secret + tenant id
   all present) contributes its own plaintext; otherwise a fresh 384-bit URL-safe secret is generated
   with `RandomNumberGenerator`.
3. Send `CreateIdentityDataCommandRequest` with that one client — **every pass**, not only the first.
   Re-sending the *same* plaintext hashes to the same value identity-side, so nothing rotates, while
   grants / scope / roles converge for a client created before this code, and a client someone
   deleted underneath us is recreated with the credential the adapter still holds.
4. Write the `ServiceAccountConfiguration` entity and the `PipelineServiceAccount` edge in one
   transaction (`ICommunicationRepository.SavePipelineServiceAccountAsync`) — unless step 2 found a
   complete, linked, current configuration, in which case nothing is written at all.

**Why the bus and not the identity REST API.** `ClientsController` already accepts a plaintext
`ClientSecret` and hashes it — but calling it needs an `octo_api` bearer token, i.e. a
client-credentials identity, which is exactly what is being created. No bootstrap client with a
secret is seeded anywhere (`System.Identity.Bootstrap` has only authorization-code and device-code
clients), so the REST route is circular. It would also add a `Meshmakers.Octo.Sdk.ServiceClient`
package reference and a second identity transport to a service that already has exactly one:
`ICommandClient<CreateIdentityDataCommandRequest>`. Cost of the bus route: three optional properties
on the shared `DistClientDto` in octo-common-services (`ClientSecret`, `RequireClientSecret`,
`AssignedRoleNames`) — additive, defaulting to the pre-existing behaviour, so every other producer is
unchanged. **Deployment order: identity first, then the controller**; an identity that predates the
change ignores the new fields and would create a secretless client.

🔴 **The command client is scoped.** `ICommandClient<T>` wraps MassTransit's `IRequestClient<T>`,
which is **scoped**; this service is a singleton consumed by the singleton `PoolService`, so it
resolves the client per call from a fresh scope (the `CommunicationEventService` pattern).
Constructor-injecting it fails DI validation at startup.

**What the client looks like:** `AllowedGrantTypes = [client_credentials, on-behalf-of URN]`,
`AllowedScopes = [octo_api]`, `RequireClientSecret = true`, `AllowOfflineAccess = false`. The
delegation URN (`Constants.OnBehalfOfGrantType`) is seeded **now** even though AB#5031 is not live:
Duende gates its extension-grant validators on the client's own `AllowedGrantTypes`, so adding it
later would mean touching every already-provisioned tenant.

**Triggers.** Two, both idempotent:

| Trigger | Where | Covers |
|---|---|---|
| Tenant-wide sweep | `DefaultConfigurationCreatorService.StartTenantAsync` → `EnsurePipelineServiceAccountsAsync`, right after `ApplyServiceManagedBlueprintsAsync` | The **backfill** for existing tenants (service start, Enable, and `PosUpdateTenant` — i.e. the documented `clearCache` recovery lever), and the blueprint-seeded default Adapter on a fresh tenant. |
| Per adapter | `PoolService.DeployWorkloadAsync`, after the deploy notification, for `RtAdapter` only | An adapter an operator adds between two tenant loads. There is no adapter-*create* path in this service (adapters are RtEntities written through the asset repository), and nothing runs pipelines before its workload is deployed, so this is the earliest point that matters. |

**Fault tolerance is the point of the backfill.** `EnsureAdapterProvisionedAsync` never throws: it
logs an Error **and writes a persistent Error event into the tenant's event log**, so the refusal an
operator later sees on a pipeline deploy has a visible cause instead of only a pod log line. One
adapter's failure does not stop the others; a failing adapter lookup (CK cache being unloaded during
a tenant update) is reported, not thrown; and `EnsurePipelineServiceAccountsAsync` wraps the whole
thing again so tenant startup — adapters, pools, trigger schedules — completes regardless.

### 🔴 Roles: an under-privileged service account fails **silently**

The controller's own endpoints authorize on the **`octo_api` scope**, not on a role — every policy in
`Program.cs` is a `RequireClaim` on the scope claim, so `CommunicationManagement` is *not* required
for the deploy calls a pipeline makes back into this service. It is granted anyway, because of what
comes next.

For the delegation case (AB#5031) the issued token carries the **intersection** of the service
account's roles and the calling user's roles. A service account without the tenant's *business* roles
(e.g. the Accounting roles) therefore produces an **empty intersection** — and identity treats an
empty intersection as a **success**, not an error: the token is issued, it simply carries no `role`
claim, and every role-gated consumer fails closed. The symptom is a chat that goes quiet, an export
that returns nothing, a pipeline that "does nothing" — with **no error anywhere**.

So when you set up delegation for a tenant, grant the pipeline service account
(`octo-pipeline-sa-{adapterRtId}`, one per adapter) the fachliche roles its pipelines must act
under, on top of `CommunicationManagement`:

```
octo-cli AddClientToRole --clientId octo-pipeline-sa-<adapterRtId> --roleName <Role> --context <tenant>
```

(or Refinery Studio → Identity → Clients → Roles). Role names are matched case-insensitively against
the tenant's `RtRole.NormalizedName`; a role that does not exist yet is **skipped with a warning**
identity-side rather than failing the whole identity-data setup, and picked up by the next
provisioning pass.

Phase 2 tests: `Services/PipelineServiceAccountProvisioningServiceTests` (secret entropy / URL-safety /
randomness, both grant types + scope + role on the created client, entity and edge written with the
plaintext, second run rotates nothing and writes nothing, unlinked entity is re-linked without
rotation, entity without a secret gets a fresh one, issuer change converges, no secret in any NLog
target and `DistClientDto.ToString()` redaction, tenant sweep backfills / isolates one broken adapter
/ survives a failing adapter lookup, identity refusal is not written as a half-provisioned entity,
seed-pending still writes the entity),
`Services/AdapterServiceTests/DeployPipelineAfterProvisioningTests` (**the phase 1 ↔ phase 2 proof**:
same guard refuses before and passes after the backfill, and the projected configuration carries the
deterministic well-known name the mesh adapter's `ServiceAccountTokenService` looks accounts up by),
`Services/PoolServiceTests/DeployWorkloadServiceAccountProvisioningTests` (Adapter yes / Application
no / a throwing provisioning does not fail the deploy),
`Services/DefaultConfigurationCreatorServiceTests/EnsurePipelineServiceAccountsAsyncTests` (audit
event only when something changed, never throws), integration
`Repository/PipelineServiceAccountRepositoryTests` (entity + edge against real MongoDB, repeat run
keeps exactly one edge despite the ZeroOrOne multiplicity, a different account replaces the edge).


### Phase 3 — deliberate secret rotation (AB#5032)

Everything above is built to **never** rotate: a convergence pass re-sends the plaintext it already
holds, because a service restart that invalidated every adapter's credential would be a
self-inflicted outage. That leaves a leaked or aged secret unretireable. The rotation verb is the
explicit counterpart.

```
POST {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rotateSecret
  → 200 RotateServiceAccountSecretResultDto { ClientId, ConfigurationWellKnownName,
                                              WasCreated, RequiresPipelineRedeploy, Message }
```

`AdapterController.RotateServiceAccountSecret` (`TenantCommunicationApiReadWritePolicy`) →
`IPipelineServiceAccountProvisioningService.RotateAdapterSecretAsync`.

**Why the REST verb belongs here and not (only) in the CLI.** This service is the sole owner of
*both* halves of the credential — it is the only producer of the identity client (over the
distribution event hub; the identity REST API is unreachable without exactly the client-credentials
identity being rotated) and the writer of the `ServiceAccountConfiguration` entity. A CLI-side
rotation would have to reproduce that pairing across two services and could leave the halves apart.
Putting it here means CLI, MCP and Studio all reach it through the existing
`ICommunicationServicesClient`, and the audit event is written where the change happens. The
octo-cli/SDK command is a follow-up in those repos; nothing else is needed server-side.

**Ordering is the consistency argument** — identity is written **first**:

| Failure | Resulting state |
|---|---|
| Identity write fails | Nothing changed anywhere. Both sides still hold the old secret; the verb throws and answers 400 saying the previous secret remains in effect. |
| Configuration write fails | The client already carries the new hash — the one genuinely inconsistent window. The previous plaintext is pushed back to identity (compensation), so the adapters running on it keep working, and the original failure is surfaced. |
| Compensation also fails | Logged as Error naming both sides. The next convergence pass re-sends the plaintext the configuration still holds and heals it. |

A second call after any failure rotates again from a consistent state; two successful calls in a row
each leave both sides on the same fresh secret.

🔴 **`isRuntimeState` on `ClientSecret` does not block this.** That marker only makes a *blueprint
re-apply* preserve the existing value (`ImportRtModelCommand.PreserveAttributesForEntity`) and
excludes the attribute from `ExportRt`. The runtime write path (`EntityUpdateInfo` /
`SavePipelineServiceAccountAsync`) is unaffected — which is precisely why rotation has to go through
this path: a blueprint can no longer change a live secret at all.

🔴 **A rotation only takes effect after a redeploy.** The adapter bakes the pipeline's
`GlobalConfiguration` — service-account credentials included — into the immutable
`PipelineRegistration` at **pipeline registration** (`PipelineRegistryService`,
octo-communication-sdk) and never refreshes it. Until the adapter's pipelines / data flows are
redeployed, they keep presenting the secret the identity service has already replaced.
`RequiresPipelineRedeploy` and the `Message` say so; the audit event repeats it.

🔴 **No secret leaves the two sanctioned sinks.** The response DTO deliberately has no secret-shaped
property (returning one would add proxy logs, shell history and CI output as a third home), and
neither plaintext — the new one or the retired one — is written to any log target, not even
truncated, and not on the failure paths that log the most.

Phase 3 tests: `Services/PipelineServiceAccountRotationTests` (both sides get the same new secret,
entity updated not duplicated, identity-before-configuration ordering, twice in a row stays
consistent and really rotates, identity refusal leaves the configuration untouched, configuration
failure restores the previous secret, a failing compensation still reports the original failure,
adapter without an account degrades to a provisioning that needs no redeploy, no secret in any NLog
target on the happy and the double-failure path),
`Controllers/AdapterControllerRotateServiceAccountTests` (redeploy hint + audit event, no
secret-shaped property on the DTO, provisioning variant, 404 unknown adapter, 400 malformed rtId,
400 naming that the previous secret still works).

### Phase 4 — the credentials reach the adapter pod (AB#5072)

Everything above provisions and rotates a credential the adapter had no way of *receiving*. AB#5072
delivers it: `octo-communication-sdk` gained `AdapterOptions.IssuerUri` / `ClientId` / `ClientSecret`
plus an `AdapterAccessTokenService` that logs in **before** the hub client connects, and this service
supplies the two values through the `ValueOverride[]` that already goes to the operator at deploy
time.

**The coupling to the chart, side by side.** These four rows are the whole contract, and a typo in
any of them is invisible until a pod is running — the adapter simply connects anonymously:

| Controller (`PoolService`) | Operator | Chart (`octo-mesh-adapter/templates/_env.tpl`) | Adapter |
|---|---|---|---|
| `ServiceAccountClientIdValuePath` = `serviceAccountClientId`, `IsSecret=false` | literal value in `values-overrides.yaml` | `.Values.serviceAccountClientId` | `OCTO_ADAPTER__CLIENTID` |
| `ServiceAccountClientSecretValuePath` = `secrets.serviceAccountClientSecret`, **`IsSecret=true`** | materialised into `{release}-octo-secrets`, path rewritten to `valueFrom.secretKeyRef` | `.Values.secrets.serviceAccountClientSecret` through `octo-mesh.secretEnv` (same helper as `secrets.rabbitmq`) | `OCTO_ADAPTER__CLIENTSECRET` |
| *(nothing — deliberately)* | `authUri` from `OperatorOptions.AuthUri` | `.Values.authUri` | `OCTO_ADAPTER__ISSUERURI` **and** `OCTO_ADAPTER__AUTHORITYURL` |

The third row is Gerald's decision and worth keeping: `AuthorityUrl` (inbound — the issuer secured
`FromHttpRequest@2` routes accept) and `IssuerUri` (outbound — the identity service the adapter
authenticates *itself* against) are two configuration keys because `AdapterOptions` lives in the SDK
and must also serve adapters that have no `MeshAdapterConfiguration` (Loxone, Modbus, Zenon). They
always name the same identity service, so the chart feeds both from the one `authUri` value; a second
chart value could only ever drift. Nothing is projected for it here.

**Where the projection happens.** `PoolService.AppendPipelineServiceAccountOverridesAsync`, called
from `BuildWorkloadDeployedDtoAsync` after the entity's own overrides are built. It reads the account
through `IPipelineServiceAccountResolver.GetAdapterDefaultAsync` — the adapter-wide default, not a
per-pipeline override: this is the *process* identity of the pod, and a per-pipeline account is about
one pipeline's execution.

- **Adapters only.** `Application`s execute no pipelines, connect to no adapter hub, and their CK
  type does not carry the association; they are skipped before any repository read.
- **Not gated on `ReceivesClusterSecrets`, deliberately.** That flag decides whether a workload gets
  the *cluster's shared* data-store credentials (MongoDB, CrateDB) — a pure edge adapter must not.
  These two values are the opposite kind of thing: the adapter's own, per-adapter, tenant-scoped
  identity, and an edge adapter needs it *more* than an in-cluster one, because it is the only
  credential it presents when dialling into the controller across the network. Same precedent as the
  RabbitMQ broker password, which is injected unconditionally because every workload needs the
  command bus.
- **Everything degrades to "no credentials", never to a failed deploy.** No account linked, a
  half-written entity (attributes are read through `GetAttributeValueOrDefault`, never the generated
  mandatory-attribute getters), a resolver that throws while the CK cache is being unloaded — each
  logs and returns the overrides unchanged. The result is a pod that connects anonymously, which is
  what the entire fleet does today.
- **A path the workload entity already pins wins.** `WorkloadOverrideYamlBuilder` is last-wins, so
  appending unconditionally would silently overrule a deliberate manual override.
- 🔴 **The secret reaches no log path.** Not verbatim, not truncated, not in an exception message.
  Pinned by `DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget`, which reconfigures NLog
  to a `MemoryTarget` and asserts on the **rendered** output.

#### 🔴 The ordering: provisioning moved BEFORE the deploy notification

AB#5027 phase 2 provisions the adapter's account in `DeployWorkloadAsync` **after** the deploy
notification ("best effort, so the identity round trip cannot delay the helm rollout"), while
`BuildWorkloadDeployedDtoAsync` runs before it. That was free while the account was only ever read
back by a *later* pipeline deploy. It is not free any more: the deploy notification now carries the
credentials, and the DTO is built from what exists at that moment — so the **first** deploy of every
freshly created adapter would ship none, the pod would come up anonymous, and nothing would
re-deploy it. Once the adapter-hub gate (AB#5063) is armed, such a pod never gets online at all.

Provisioning therefore now runs **before** `BuildWorkloadDeployedDtoAsync`, immediately after
`EnsureWorkloadIsHelmDeployableAsync`. The two rejected alternatives: triggering a second deploy after
a successful provisioning costs a second helm rollout and pod restart on every deploy and needs its
own loop guard; and documenting "the first deploy is anonymous" pushes a manual second click onto
every adapter creation. What is paid instead is latency on the deploy call — bounded by the
provisioning service's own 30 s identity timeout, normally milliseconds — and it is paid *before* the
rollout starts rather than during it, so a slow identity service delays a deploy instead of
half-configuring one. The best-effort contract is unchanged: a provisioning failure is still
swallowed and the workload still deploys, just without credentials.

Note `DeployManagedWorkloadsAsync` (the pool fan-out) calls `BuildWorkloadDeployedDtoAsync` without
provisioning first — it has no live caller today, and the tenant-start sweep
(`EnsurePipelineServiceAccountsAsync`) is what covers that path.

Phase 4 tests: `Services/PoolServiceTests/DeployWorkloadServiceAccountCredentialsTests` (both paths
projected with the right secret flags, Application projects nothing and never asks the resolver, no
account / half-written account / throwing resolver each deploy without credentials, an entity pin is
kept, `ReceivesClusterSecrets=false` still gets them, an encrypted attribute reaches the wire
decrypted, no secret in any NLog target, and a literal assertion that the two value paths match the
chart) plus `DeployWorkloadAsync_ProvisionsBeforeItBuildsTheDeployNotification` in
`DeployWorkloadServiceAccountProvisioningTests`.

### Phase 5 — declarative reconcile (AB#5111) and identity health + hardened guard (AB#5112)

**AB#5111** made the account declarative: `ServiceAccountConfiguration` (CK 3.32.0, additive)
carries `AssignedRoleNames` (null = legacy, roles unmanaged) and `AllowDelegation`, the backfill
became an idempotent reconcile (`ReconcileAdapterAsync` / `ReconcileConfigurationAsync` — never
rotates an existing secret), `IssuerUri` defaults to the portable `{{service.authority}}` token
resolved at projection time by the workload-template machinery, and REST endpoints (`POST
{tenantId}/v1/adapter/{adapterRtId}/serviceAccount/reconcile`, `POST
{tenantId}/v1/serviceAccount/reconcile?configurationRtId=…`, `POST
{tenantId}/v1/serviceAccount/{configurationRtId}/rotateSecret`) expose it; a user-initiated
reconcile only materialises roles when the caller holds `UserManagement`. See the XML docs on
`IPipelineServiceAccountProvisioningService` for the full contract.

**AB#5112** adds the read side and hardens the deploy guard.

**Identity-health aggregate** — `IServiceAccountHealthService` / `ServiceAccountHealthService`:

```
GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/health        (adapter-scoped)
GET {tenantId}/v1/serviceAccount/{configurationRtId}/health          (configuration-bound)
  → 200 ServiceAccountHealthDto { OverallStatus, ConfigurationRtId,
                                  ConfigurationWellKnownName, ClientId, Checks[] }
```

Both `TenantCommunicationApiReadOnlyPolicy`. One check per concern, each with a machine-readable
name (`association` — adapter variant only, `configuration`, `client`, `secret`, `roles`,
`delegation`, `tenant`, `issuerUri`), a status (`Healthy` / `Violation` / `Unknown` /
`NotApplicable`), a violation `Code` (`association-missing`, `configuration-missing`,
`client-missing`, `secret-missing`, `roles-drift` — with `MissingRoles` / `SuperfluousRoles`
lists, `delegation-drift`, `tenant-mismatch`, `issuer-uri-drift`) and a human message naming the
reconcile as the fix. `OverallStatus`: any violation → `Unhealthy`, else any unknown → `Unknown`,
else `Healthy`. The secret check reports **presence only** — the value never leaves the entity.
Roles are only compared for declarative accounts (`AssignedRoleNames != null`); the issuer rule is
the sweep's own (`PipelineServiceAccountProvisioningService.IsIssuerUriHealthy`, shared so
endpoint and sweep cannot disagree). **Degrades instead of failing**: an unreachable identity
service turns the identity-backed checks (`client`, `roles`, `delegation`) into `Unknown`, never
into a 5xx or a violation.

**Controller→identity read** — `IIdentityClientReader` / `IdentityClientReader`. The bus carries
no identity *query* surface (only the create/converge command) and the SDK service client stays
rejected (second transport, no bootstrap credential — see phase 2). What every path that needs the
read *does* have is the calling user's bearer token, so the reader forwards it verbatim to
identity's REST API at `AuthorityUrl` (`GET {tenantId}/v1/Clients/{id}`, `…/{id}/roles` + `GET
…/Roles` to map role rtIds to the names the declaration speaks in) — the caller's own privileges
answer the question, no new service credential, no escalation. Identity's read endpoints accept
exactly the `octo_api` scope family the controller's own policies require. No ambient HTTP
request, 401/403/5xx, timeout → `IdentityClientLookupStatus.Unavailable`, which every consumer
treats as "unknown", never as "missing". Named `HttpClient` with a 10 s timeout (Program.cs).

**Hardened deploy guard** (`EnsurePipelineHasServiceAccountAsync`, both deploy paths): after the
AB#5027 resolution check, (a) an account without a **client secret** is refused unconditionally —
a local fact, `AdapterServiceException.PipelineServiceAccountSecretMissing`; (b) the **identity
client's existence** is verified through the reader —
`AdapterServiceException.PipelineServiceAccountClientMissing` on an authoritative NotFound (or a
configuration naming no `ClientId` at all). Both messages name the reconcile endpoints and Studio
as the fix, AB#5027-style. Check (b) is gated by `ServiceAccountGuardOptions`
(`ServiceAccountGuard:CheckIdentityClient`, **default `true`**;
`OCTO_SERVICEACCOUNTGUARD__CHECKIDENTITYCLIENT=false` loosens an environment without a release —
the switch turns the whole identity round trip off, not just the refusal). 🔴 An **unreachable**
identity answer never blocks: warning log, deploy proceeds — identity downtime must not brick
pipeline deploys, and the adapter-side token request surfaces a real problem immediately anyway.

Phase 5 tests: `Services/ServiceAccountHealthTests` (all checks green on both variants, every
violation with its code, unlinked-but-existing entity, legacy roles opt-out, both delegation drift
directions, tenant mismatch naming the foreign tenant, issuer token case-insensitivity +
authority acceptance, identity-unreachable → Unknown aggregate, partial role-read degradation),
`Services/AdapterServiceTests/DeployPipelineServiceAccountHardenedGuardTests` (secret-missing and
client-missing refusals with remedy text on both deploy paths, secret check independent of the
option, option-disabled pass-through without any identity call, identity-unreachable
pass-through), `Controllers/ServiceAccountHealthEndpointTests` (routing, 404/400 mapping). The
integration fixture substitutes the reader with an `Unavailable` answer — it runs no identity
service, and the guard is non-blocking by contract.

### Phase 6 — rights analysis: touched types × data permissions (AB#5113)

**`IServiceAccountRightsAnalysisService` / `ServiceAccountRightsAnalysisService`** answers "which
roles does this pipeline service account need?" — read-only, side-effect free, entirely from
tenant-local data:

```
GET {tenantId}/v1/adapter/{adapterRtId}/serviceAccount/rightsAnalysis      (adapter-scoped)
GET {tenantId}/v1/serviceAccount/{configurationRtId}/rightsAnalysis       (configuration-bound)
  → 200 ServiceAccountRightsAnalysisDto { AnalyzedPipelines[], ProtectedTypes[],
        DynamicTypeUsages[], Warnings[], DeclaredRoles[]|null, MissingRoles[],
        SuperfluousRoles[], BaselineRoles[], Message }
```

Both `TenantCommunicationApiReadOnlyPolicy`. Three data sources are joined:

1. **Touched CK types** — extracted from the pipeline set's `PipelineDefinition` YAML via the
   existing `IPipelineDefinitionService` (the debugger's dictionary-based YamlDotNet parser; a new
   `TryGetAllNodes` distinguishes unparsable from empty). The scan walks every node's properties
   **recursively** (association updates carry `targetCkTypeId`/`originCkTypeId` in nested update
   records) looking for `ckTypeId` / `ckTypeIds` / `targetCkTypeId` / `originCkTypeId`; container
   nesting (`ForEach@1`, `If@1`, …) is already handled by the parser's `transformations`
   recursion. A `*ckTypeIdPath` property or a non-literal value (template token, JSONPath) lands
   in `DynamicTypeUsages` — **reported as not statically analyzable, never silently dropped**.
2. **Data policies/permissions** (Epic AB#4969) — untyped `RtEntity` reads through the tenant
   repository: `GetDataPoliciesAsync` (all `System.Identity/DataPolicy`),
   `GetDataPermissionsForPoliciesAsync` (outbound `PolicyPermission`),
   `GetGrantingRolesForDataPermissionsAsync` (inbound `GrantsPermission` → `Role`). The
   controller references **no generated System.Identity model** — the four stable ids live in
   `Repository/SystemIdentityCkIds`. Type matching is on the `Model/Type` name part,
   **version-insensitively** (pipeline YAML is unversioned, policy targets are canonical
   `SemanticVersionedFullName` and may carry an element version — `NormalizeCkTypeId` strips it).
3. **The declaration** — `AssignedRoleNames` (AB#5111); `null` = legacy → recommendation without
   a delta.

Pipeline-set semantics follow the AB#5027 resolver: adapter-scoped = executing pipelines minus
those with their own override; configuration-bound = the adapter-default pipelines resolving to
it plus pipelines overriding **to** it (new reverse read `GetPipelinesUsingServiceAccountAsync`,
inbound over `Uses`; the resolver still decides which link is effective). Unprotected touched
types are **dropped** (no policy targets them → no role needed). `ProtectedTypes` is the
(type × policy) join — one entry per pair, with `OwnerScoped` flagged: 🔴 **D7 — an owner-scoped
(`OwnedOnly`) grant never counts as coverage** (a service account owns nothing); a type with only
owner-scoped grants is called out as blind in the summary. Delta: `MissingRoles` = full-scope
granting roles of uncovered types; `SuperfluousRoles` = declared roles granting nothing for any
touched type — except the provisioning baseline (`CommunicationManagement`,
`PipelineServiceAccountProvisioningService.DefaultAssignedRoleNames`), which lands in
`BaselineRoles` instead (it grants no data access and every pipeline SA needs it). Robustness:
unparsable/empty definitions become `Warnings` entries, an empty pipeline set returns an
empty-but-valid result.

Phase 6 tests: `Services/ServiceAccountRightsAnalysisTests` (extraction incl. nested containers +
list/record recursion, dynamic-path and templated-id reporting, unprotected drop, versioned-target
matching, role join, owner-scope flag + blind-type handling, delta with baseline, legacy null
declaration, unparsable-YAML warning, empty set, both pipeline-set resolutions),
`Controllers/ServiceAccountRightsAnalysisEndpointTests` (routing, 404/400 mapping — AB#5112
pattern).

### Phase 7 — installation defaults (AB#5115) and MayActAs materialisation (AB#5114 v1)

**AB#5115** (CK **3.33.0**, minor): `IssuerUri`, `TenantId` and `ClientSecret` on
`ServiceAccountConfiguration` became **optional** — `isOptional` sits on the type's attribute
*references*, so the shared definitions (`ClientId`/`ClientSecret` with FinApi/MicrosoftGraph,
`${System}/TenantId` platform-wide) stay mandatory everywhere else. Empty now MEANS something,
mirrored by the adapter (octo-mesh-adapter `ServiceAccountTokenService`, commit acff5ac): empty
`IssuerUri` = the adapter's own installation, empty `TenantId` = the tenant the adapter runs for,
empty/`<placeholder>` `ClientSecret` = "no secret — impersonate instead" (AB#5114). The reconcile
**creates new accounts with issuer and tenant EMPTY** and converges the two historic installation
spellings to empty — the `{{service.authority}}` token (case-insensitive) and this installation's
concrete `AuthorityUrl` (trailing-slash-insensitive), plus a `TenantId` naming the current tenant.
Any **other** concrete issuer/tenant is a **deliberate foreign target and is left alone** (this
*inverts* the AB#5111 "converge foreign issuers onto the token" rule and retires the AB#5111
"always rewrite TenantId" rule). Shared helpers on `PipelineServiceAccountProvisioningService`:
`IsInstallationIssuer` (replaces `IsIssuerUriHealthy`), `ConvergeIssuerUri`, `ConvergeTenantId`,
`IsSecretUsable` (byte-for-byte the adapter's rule: empty or leading `<` = no secret). The
projection passes an empty `IssuerUri` through EMPTY; token resolution remains only for legacy
entities that still carry it. The secret transition stays dual-path: the reconcile still issues a
secret where none exists — an impersonation-only account is authored, never generated.

**AB#5114 v1** — the controller declares which clients may impersonate a pipeline service
account: `DistClientDto.MayActAsClientIds` (octo-common-services; additive + idempotent
identity-side, unknown actors skipped with a warning, `null` = no edge changes, removal stays a
v1 non-goal). The **adapter's own client** is the adapter-default account's client — exactly the
AB#5072 credentials in the pod's Helm values — so the adapter path never declares actors (a
self-edge authorizes nothing); the **standalone** branch of `ReconcileConfigurationAsync` resolves
its actors via `GetPipelinesUsingServiceAccountAsync` → `GetAdapterByPipelineAsync` → the adapter
defaults' `ClientId`s (deduplicated, sorted, self excluded; no actor resolvable → `null` + info
log, never a failure). Rotation sends `null` — like roles, edges are never touched by a rotation.

**Health** — new check `impersonation`: `NotApplicable` with message when the account has a
usable secret, when nothing links a standalone account to an adapter, or when no capable actor
exists (the adapter-scoped variant is *always* one of these — it evaluates the adapter's own
account, which cannot impersonate itself); **`Unknown`** when a capable actor exists, because 🔴
**the MayActAs edge is not verifiable controller-side**: the identity REST surface the
`IIdentityClientReader` uses (`GET {tenantId}/v1/Clients/…`) exposes clients and role edges but
**no client-to-client associations** — the edge lives in `System.Identity/MayActAs` and only the
identity token endpoint consults it (`ClientImpersonationStore`). The `secret` check became
credential-aware: empty is a `Violation` (`secret-missing`) **only when no impersonation is
possible**; with a capable actor it is `Healthy` with a message naming that actor. `issuerUri` /
`tenant` report `Healthy` with "installation default" when empty; the token/own-authority/current
tenant spellings stay healthy as legacy (message says the next reconcile empties them); a foreign
issuer is a healthy deliberate target (the `issuer-uri-drift` violation is retired), and a
foreign *tenant* is only a `tenant-mismatch` violation while the issuer resolves to *this*
installation — paired with a foreign issuer it is a healthy foreign pairing.

**Deploy guard** — refuses `PipelineServiceAccountSecretMissing` only when the account has
neither a usable secret NOR a capable impersonation actor (adapter default with usable secret,
different client) — both local facts; the edge itself stays best-effort exactly like the
client-existence check (identity unreachable never blocks), and the refusal message names both
repairs.

Phase 7 tests: `Services/PipelineServiceAccountReconcileTests` (+ `…ProvisioningServiceTests`,
`…RotationTests`) — create-with-empty, the full convergence matrix (token incl. case, own
authority incl. trailing slash, current tenant → empty; foreign issuer/tenant untouched incl.
no-write), MayActAs on the wire (adapter path null, standalone actors deduplicated, unused
standalone null); `Services/ServiceAccountHealthTests` — installation-default messages,
credential-aware secret, all four impersonation states, foreign pairing, placeholder secret;
`Services/AdapterServiceTests/DeployPipelineServiceAccountHardenedGuardTests` — the AB#5114
matrix (capable actor deploys incl. placeholder target secret, no/incapable actor refused with
remedy, identity-down non-blocking); `…/PipelineServiceAccountProjectionTests` — empty issuer
passes through empty.

### The tenant gate only started working in AB#5054

The section below describes a middleware that, until AB#5054, **never ran a single check in this
service**. `TenantAuthorizationMiddleware` inspects only principals whose
`Identity.AuthenticationType` reads `Bearer` — its guard against false 403s on cookie principals.
That label is not the scheme name; it comes from `TokenValidationParameters.AuthenticationType`,
which the JWT handler leaves at the framework default `AuthenticationTypes.Federation` unless the
host sets it. This service did not, so the `Use…`/`Add…` pair gated nothing: no user token was ever
checked, and the AB#5032 service-token audit log — the inventory an operator is supposed to read
before flipping to `Enforce` — was empty because nothing wrote to it, not because nothing was wrong.

Two pieces make it work now:

| Where | What |
|---|---|
| `Configuration/ConfigureJwtBearerOptions.cs` | Sets `TokenValidationParameters.AuthenticationType = "Bearer"`, and now also owns `Audience`, `NameClaimType` and `RoleClaimType`. |
| `Program.cs` | `AddAuthentication().AddJwtBearer()` — **without a configuration delegate**. |

🔴 **The second row is the load-bearing one.** `Program.cs` used to pass
`AddJwtBearer(jwt => { jwt.TokenValidationParameters = new TokenValidationParameters { … }; })`. The
options factory runs configurators in **registration order**, and `ConfigureOptions<ConfigureJwtBearerOptions>()`
is registered first — so that delegate ran last and replaced the whole instance, discarding both the
explicit `ValidIssuer` (the IDX10204 hardening) and the `AuthenticationType`. It compiles, and a unit
test of the configurator in isolation stays green, because neither ever sees the composed state:
octo-ai-services shipped a full release in exactly that condition (AB#5051 → AB#5056). Keep the rule
— one configurator owns the scheme, `AddJwtBearer()` takes no argument.
`tests/CommunicationControllerService.Tests/Configuration/TenantAuthorizationWiringTests.cs` pins the
label, the authority/issuer/audience contract, and a source-level guard that no second configurator
reappears.

### Tenant authorization for user tokens is staged (AB#5054)

`TenantAuthorizationOptions.UserTokenEnforcement` (no `Disabled`; `LogOnly` | `Enforce`, platform
default `Enforce`) is set to **`LogOnly`** in `Program.cs` — registered *before*
`AddOctoTenantAuthorization(builder.Configuration)` so configuration still wins. No request outcome
changes; every access an enforcing run would refuse is logged with subject, client id and both
tenants. Flip an environment with `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` once that
log is clean.

Why staged even though a static sweep found no cross-tenant user caller for this service (Studio
re-mints per tenant and guards the route client-side, octo-cli derives URL tenant and `acr_values`
from one context value, octo-mcp-service RFC 8693-exchanges before calling): that is an argument, and
the gate has never produced the evidence here. One release in `LogOnly` costs nothing and produces
it. Note also what the gate does **not** cover, and never will: `/{tenantId}/adapterHub` short-circuits
it twice over — no `Authorization: Bearer` header (a SignalR client sends `?access_token=` on the
WebSocket / SSE transports) and, until AB#5063, no principal at all. That hub's tenant isolation is
therefore checked by `AdapterHubAuthorizationFilter`, not here; see "The adapter hub, and why its gate
has a second half" above. `GET {tenantId}/v1/communication/ping` is `[AllowAnonymous]` and is skipped
by design.

### Tenant authorization for service tokens (AB#5032)

`Program.cs` calls `AddOctoTenantAuthorization(builder.Configuration)` so the staged narrowing of the
client-credentials exemption in `UseOctoTenantAuthorization()` is settable per environment
(`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` = `Disabled` | `LogOnly` (default) | `Enforce`,
plus `…__CROSSTENANTSERVICECLIENTIDS__n`). Defaults keep today's behaviour and only add an audit log.
Full rationale in octo-common-services CLAUDE.md § "Tenant Authorization — the service-token
exemption". Note this service sets `jwt.Audience = octoAPI`, so it is *not* among the repos running
with `ValidateAudience = false`.

## Project Structure Notes

- Main service: `src/CommunicationControllerServices/`
- Construction Kit model: `src/SystemCommunicationCkModel/`
- Resource strings: `src/CommunicationControllerServices.Resources/`
- Unit tests (TUnit): `tests/CommunicationControllerService.Tests/`
- Integration tests (xUnit): `tests/CommunicationControllerServices.IntegrationTests/`
- CI/CD: `azure-pipelines.yml` (root; uses shared `octo-pipeline-templates`)
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
- **System API** (`SystemApi/v1/Controllers/`): System-level, non-tenant operations. The
  `CommunicationController` here now only exposes `ping`; its `enable`/`disable` actions were
  **removed (AB#4287)** — tenant enable/disable is tenant-scoped only. `DiagnosticsController`
  (system-scoped) still uses `SystemCommunicationApiPolicy`.
- **Tenant API** (`TenantApi/v1/Controllers/`): Tenant-scoped operations for adapters, pools, pipelines, and tenant enable/disable

Routes follow pattern: `{tenantId:tenantId}/v{version:apiVersion}/[controller]`

Tenant enable/disable lives **only** on the Tenant API:
- `{tenantId}/v1/communication/enable` / `.../disable` (tenant read from route)
- The legacy `system/v1/communication/enable?tenantId=X` variant no longer exists (AB#4287).
  The SDK dropped its `system/v1` fallback and now requires a tenant on the client options.

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
owning data flow with `DeployDataFlowAsync`. (Historic note: before AB#4364,
`DeployPipelineAsync` force-enabled debug, which is why this path avoided it;
since AB#4364 all deploy paths preserve the persisted flag as-is.)
If the adapter is offline, `DeployDataFlowAsync` throws `AdapterServiceException`
(`AdapterNotLoaded`); the service swallows it, leaving the flag persisted
(`AppliedToRunningAdapter = false`, applies on next deploy). An `Information`
audit event tagged `(source: User)` is written per toggle.

**Deploy never touches debug (AB#4364).** `POST /pipeline/deploy` used to
force-enable `IsDebuggingEnabled` on every call — so the Studio editor's routine
"Deploy" (e.g. right after importing a pipeline) and the move-to-adapter redeploy
silently switched debug capture on, permanently. Observed on prod-1: the Loxone
event pipeline ran with debug for months. Since AB#4364, deploying pushes the
persisted `IsDebuggingEnabled` as-is: a pipeline in debug stays in debug across
redeploys, a pipeline without debug stays clean. Debug is toggled exclusively via
this PATCH endpoint / `SetPipelineDebuggingAsync`; the Studio's Enable/Disable
Debug buttons call it directly (frontend-libraries
`CommunicationService.setPipelineDebugging`).

**Debug-enabled pipelines are surfaced on every push (AB#4662).** A lingering
debug flag on a compute-heavy pipeline OOM-killed the FDA adapter without anyone
knowing debug capture was active (debug retains per-iteration snapshots — memory
bounds fixed SDK-side under the same AB#). `SendConfigurationAndWaitForResultAsync`
— the single choke point for every configuration push (adapter config, pipeline
deploy, data-flow deploy) — now writes a Warning audit event + NLog warning for
each pipeline in the pushed configuration whose `IsDebuggingEnabled` is true, so
sticky debug state is visible in the Studio event log instead of silent.

Tests: `Services/AdapterServiceTests/SetPipelineDebuggingAsyncTests` (online
enable/disable, offline persist-only, not-found paths),
`Services/AdapterServiceTests/DebugEnabledDeployWarningTests` (warning event on
debug-enabled push, silence otherwise) and
`Controllers/PipelineControllerDebugTests`.

## Execute-Pipeline Endpoint Key (AB#4312)

`FromExecutePipelineCommand@1` registers a distribution-event-hub receive endpoint whose
address **must be keyed by the pipeline rtId**, not the DataFlow rtId. Both sides of the bus
build the address and must stay in sync:

- **Consumer** — `FromExecutePipelineCommandNode.StartAsync` (octo-communication-sdk,
  `Sdk.Pipeline`): `…-pipeline-{PipelineRtEntityId.RtId}`.
- **Sender** — `TriggerManagementService.StartExecutePipelineAsync`:
  `…-pipeline-{pipelineRtId}` (no DataFlow lookup needed).

The address format is
`{PipelineQueueNames.ExecutePipelineCommand}-{tenantId}-pipeline-{pipelineRtId}` (lower-cased).

Historically the key was `…-data-flow-{dataFlowRtId}` (since AB#1586). That collided when a
single DataFlow held more than one `FromExecutePipelineCommand@1` pipeline — MassTransit
rejected the duplicate (`A receive endpoint with the same key was already added`), so only the
first pipeline deployed and the rest went to `DeploymentState = Error`. It was also
semantically wrong: `ExecutePipeline` targets one specific pipeline, so a DataFlow-scoped queue
with competing consumers would round-robin the message. Keying by pipeline rtId is the correct
scope. Regression guard: `MeshAdapter.Sdk.Tests` →
`FromExecutePipelineCommandNodeTests.StartAsync_KeysEndpointByPipelineRtId_NotDataFlowRtId`.

> Wire-contract note: controller + mesh-adapter + SDK must ship together (release train).
> Sibling `FromSendNotificationNode` still uses DataFlow-scoped keying and has the same latent
> collision if a DataFlow holds two `FromSendNotification@1` pipelines — tracked separately.

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

### Execution Durability Across Adapter Restart (AB#4280)

A pipeline execution runs as an in-memory `Task.Run` inside the adapter runtime
(`AdapterTriggerContext.StartExecutePipelineAsync`, octo-communication-sdk); its state is
tracked out-of-band in the `RtPipelineExecution` entity. When the adapter **process** restarts
(deployment, crash, pod eviction) the in-memory task is lost, so the record must be driven to a
terminal state instead of being left stuck in `Running` / `Interrupted`. Three cooperating
mechanisms guarantee this:

1. **Disconnect → `Interrupted`** (`AdapterHub.OnDisconnectedAsync` →
   `MarkExecutionsAsInterruptedAsync`). Fires whenever the controller detects an adapter drop
   *while the controller itself is healthy* — i.e. the common adapter-only restart. The
   `IShutdownState.IsShuttingDown` guard deliberately skips this during a **controller**
   rolling-upgrade: there the adapter is not gone, it is reconnecting to a surviving controller
   pod with its live tasks intact, so marking its executions `Interrupted` would be a false
   positive. (This is why AB#4280 does **not** touch the shutdown guard.)

2. **Fresh-startup orphan resolution** (`IAdapterHub.FailOrphanedExecutionsAsync(processStartUtc)`
   → `PipelineExecutionService.FailOrphanedExecutionsForAdapterAsync` →
   `CommunicationRepository.FailOrphanedExecutionsForAdapterAsync`). The **SDK** calls this once
   on a fresh process start (`isReconnect == false`), never on a transient reconnect. A fresh
   process has an empty in-memory registry, so any `Running` / `Interrupted` execution for this
   adapter whose `StartedAt` predates the process start is an orphan from the previous process and
   is transitioned to `Failed`. This is the primary fix and covers both the adapter-only restart
   and the simultaneous controller+adapter restart.

3. **Connection-aware reaper** (`ExecutionCleanupBackgroundService` →
   `FailStuckExecutionsAsync` → `CommunicationRepository.FailStuckExecutionsAsync`). The backstop
   for the case where a disconnect was never cleanly detected (e.g. the controller also restarted
   and missed the `OnDisconnected`). Past the `PipelineExecutionStuckGraceMinutes` grace period it
   fails **all** stale `Interrupted` executions (an `Interrupted` execution implies its adapter
   disconnected) **plus** stale `Running` executions **whose owning adapter is not `Online`**. A
   `Running` execution on a live (`Online`) adapter is **never** failed, regardless of how long it
   runs — this is what protects legitimate long-running ETL pipelines (e.g. multi-million-row
   CrateDB inserts) from being killed. The reaper keys off the adapter's persisted
   `CommunicationState`, so it is correct across controller pods.

SDK safety net: `AdapterExecutionService.GetLocalExecutionStatus` returns `Failed` (not
optimistic `Completed`) when an interrupted execution cannot be found in the local registry — an
execution interrupted mid-flight must never be recorded as a success.

Integration coverage lives in
`tests/CommunicationControllerServices.IntegrationTests/Repository/FailStuckAndOrphanedExecutionsTests.cs`
(connection-aware sparing of live long-runners; adapter-scoped pre-start orphan resolution).

### Hourly Statistics Buckets + Execution Fold (AB#4370)

Per-execution entities do not scale for high-frequency event pipelines (a Loxone
state-change pipeline produced ~300k executions/day; the 3-day retention window held
~700k documents and 1.4M association edges, saturating MongoDB and timing out the Studio
data-flow page). Executions are telemetry — `RtPipelineStatistics` is the durable record:

- `PipelineStatistics.HourlyBuckets` (CK model 3.25.0, record
  `PipelineStatisticsHourBucket`: HourStartAt, SuccessCount, FailureCount,
  TotalDurationMs (Int64), DurationCount) carries per-hour aggregates for a rolling
  30 days. Buckets are keyed by the UTC hour the execution STARTED in.
- **Fold-then-prune** (`PipelineExecutionService.FoldAndPruneExecutionsAsync`): every
  cleanup iteration drains terminal executions older than
  `PipelineExecutionRetentionHours` per pipeline in 500er batches — fold the batch into
  the buckets, persist the statistics entity, THEN erase exactly those executions
  (`DeleteExecutionsAsync`, `DeleteOptions.Erase`). Fold set == delete set, so nothing is
  double-counted or lost; a crash between upsert and delete double-counts at most one
  batch. Running executions are never drained regardless of age (protects long-running
  ETL pipelines — same contract as the AB#4280 reaper).
- **Window recompute** (`UpdateStatisticsAsync`): sliding windows (1h/12h/24h/30d) =
  bucket sums (`PipelineStatisticsFolder.SumBuckets`) + a live scan over the still-retained
  executions. The two sources are disjoint by construction. Buckets older than 30 days are
  pruned on write (rebuild + reassign the record list — never mutate an
  `AttributeValueList` in place, it materializes per read). `LastExecutionAt` never
  regresses when executions are folded away.
- Pure fold/merge/window rules live in `PipelineStatisticsFolder` (unit-tested); the
  MongoDB round-trip of the bucket record array is pinned by
  `FoldAndPruneRepositoryTests`.

### Background Services

| Service | Interval | Description |
|---------|----------|-------------|
| `PipelineExecutionReportProcessor` | Continuous | Drains execution reports from Channel in batches, bulk-inserts starts and bulk-updates completions |
| `ExecutionCleanupBackgroundService` | `PipelineExecutionStuckCheckIntervalMinutes` (default 5 min) | Per iteration: (1) connection-aware stuck reaper (AB#4280); (2) **execution fold** (AB#4370) — terminal executions older than `PipelineExecutionRetentionHours` (default 1h) are folded into the hourly buckets on `RtPipelineStatistics` and then **erased**, and the sliding-window counters are refreshed for every pipeline; (3) daily orphan sweep erasing executions older than `PipelineExecutionRetentionDays` unconditionally (safety net for executions whose pipeline no longer exists). All deletes use `DeleteOptions.Erase` (AB#4363) — the engine default `DeleteStrategies.Archive` only set `rtState=Archived` and let the collection grow unbounded (1M+ docs per tenant). The former hourly `PipelineStatisticsBackgroundService` was removed — folding owns statistics freshness. |

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
Task<int> TimeoutStaleExecutionsAsync(string tenantId, int timeoutHours);       // legacy, connection-unaware
Task<int> FailStuckExecutionsAsync(string tenantId, int graceMinutes);          // AB#4280 connection-aware reaper
Task<int> FailOrphanedExecutionsForAdapterAsync(string tenantId, RtEntityId adapterRtEntityId, DateTime beforeUtc); // AB#4280
```
