---
description: "Authentication and authorization: tenant gate, the staged SignalR hub gates and the adapter hub gate"
applies_to: "**/Hubs/**, **/Options/*HubAuthorizationOptions.cs"
---

### Authentication & Authorization

- JWT Bearer authentication with scope-based authorization
- Three policies:
  - `SystemCommunicationApiPolicy` - full system access
  - `TenantCommunicationApiReadWritePolicy` - tenant full access
  - `TenantCommunicationApiReadOnlyPolicy` - tenant read-only
- Scopes defined in `CommonConstants` from the Contracts library

#### The SignalR hubs are not covered by any of that

`app.MapHub<AdapterHub>` / `app.MapHub<OperatorHub>` carry **no** `RequireAuthorization()`, neither hub
class has an `[Authorize]` attribute, and the service registers no `FallbackPolicy`. The policies above
apply to the controller routes only. Both gaps are now closed by **staged** `IHubFilter` gates —
`/operatorHub` (the tenant-crossing control plane where an operator claims pools and acknowledges
workload deploy / scale outcomes) as the operator-hub gate, `/{tenantId}/adapterHub` (the adapter data plane)
as the adapter-hub gate. Same shape, one mode switch each, both defaulting to `LogOnly`.

`Hubs/OperatorHubAuthorizationFilter` is an `IHubFilter` registered per hub via
`AddSignalR().AddHubOptions<OperatorHub>(o => o.AddFilter<OperatorHubAuthorizationFilter>())`. On every
connection it evaluates **`Constants.SystemCommunicationApiPolicy`** — deliberately the policy the
service's own `system/v{version}` routes already use, not a new one: the hub is not tenant-scoped and
everything it does is a system-level write.

| `OperatorHubAuthorization:Mode` | Behaviour |
|---|---|
| `LogOnly` (**default**, and the enum's zero value) | Connection outcomes identical to no gate at all, but every connection an enforcing run would refuse is logged as a warning naming connection id, `client_id`, `sub` and scopes. That log **is** the consumer inventory. Every refusal/inventory line (all three hubs) also carries the request path, remote address, `X-Forwarded-For`, user agent and, for a rejected bearer token, its unverified `client_id` (`HubConnectionPrincipal.DescribeOrigin`; control characters stripped, 200-char cap, never the token). |
| `Enforce` | A connection that does not satisfy the policy is refused with a `HubException`. |

🔴 **It shipped on `LogOnly` because the operator had no credential — a precondition outside this
repo, not a knob to flip.** When the operator-hub gate was written, the connection was built by
`SignalRClient.CreateHubConnection` in **octo-sdk** (`src/Sdk.ServiceClient/SignalRClient.cs`), which
contained a literal `options.Headers["Authorization"] = "Bearer your-access-token"` under a
`// TODO: Handle authentication`, and **octo-communication-operator**'s `OperatorHubClientFactory`
handed the client a freshly constructed, never-populated `ServiceClientAccessToken`. Arming `Enforce`
then would have 401'd **every** operator in the estate, central and edge alike.

**That has since changed (octo-sdk `1197b9a`).** The placeholder header is gone; the SDK now
sets `options.AccessTokenProvider = () => ClientAccessToken.AccessToken` (returning `null` for a blank
token, i.e. sending *no* credential rather than a malformed one), and the operator got an
`OperatorAccessTokenService` that performs a client-credentials login and fills the holder. Whether
`/operatorHub` can now be armed is the enforce-rollout question — read an environment's `LogOnly` inventory
before answering it. Bind it per environment with `OCTO_OPERATORHUBAUTHORIZATION__MODE=Enforce`; no
release needed. The staging shape is the same one `TenantAuthorizationOptions` uses on purpose.

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

#### The adapter hub, and why its gate has a second half

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
administration rule deliberately does not apply**: it is scoped to *user* tokens on endpoints
marked `IAllowParentTenantAdministration`, a hub is not such an endpoint, and a service token's
`tenant_id` proves much less (mirrored clients share the parent's secret; a token minted without
`acr_values` falls back to the system tenant, the root of the hierarchy).

🔴 **`Enforce` is blocked, and the blocker is not the transport any more — it is that the adapter
never logs in.** The enforce rollout replaced the SDK's placeholder header with an `AccessTokenProvider` reading
the injected `IServiceClientAccessToken`, so the plumbing is in place and is re-read on every
reconnect. But `AdapterBuilder` / `WebAdapterBuilder` (octo-communication-sdk) register a bare mutable
`ServiceClientAccessToken` holder, `AdapterOptions` carries no client id / secret / authority to log in
with, and the **only** writer is the mesh adapter's `ServiceAccountTokenService`, whose only
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
`/negotiate` sub-path (written for both hubs), which is what makes the inventory meaningful
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
