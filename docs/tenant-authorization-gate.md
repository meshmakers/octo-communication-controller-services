---
description: Tenant gate history, inbound claim mapping and staged tenant authorization for user and service tokens
applies_to: **/Configuration/ConfigureJwtBearerOptions.cs, **/Controllers/PrincipalClaimExtensions.cs
---

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

### 🔴 Inbound claim mapping: never ask for the raw JWT name (AB#5328)

The same scheme sets `MapInboundClaims` on, so by the time a `ClaimsPrincipal` exists the handler has
already renamed the token's `sub` to `ClaimTypes.NameIdentifier`, `role` to `ClaimTypes.Role`, `email`
to `ClaimTypes.Email` and `name` to `ClaimTypes.Name`. Code that asks for the raw spelling gets
nothing back.

`PipelineController.BuildExecutePipelineCaller` did exactly that, and because every read in it has a
fallback the miss produced a **plausible wrong answer** instead of an error. Measured on 2026-09-23
with a token carrying a subject and seventeen roles, the persisted `PipelineExecution` said:

| field | expected | stored |
|---|---|---|
| `CallerSubjectId` | the `sub` | **`octo-cli`** — the `client_id` fallback |
| `CallerRoles` | 17 roles | **empty** |
| `CallerName` | — | survived, via the unmapped `preferred_username` fallback |

Effects: audit and billing name the OAuth client instead of the person, anything later authorizing on
`CallerRoles` sees a caller with no roles, and because a caller *is* present `CallerBinding` is
satisfied and nothing complains.

The counter-example sat in the same folder the whole time: `PrincipalRoleExtensions` (AB#5111) exists
for this reason and says *"always probe the mapped and the raw claim type together"*, citing the
AB#5030 finding on the MCP side. Every caller read now goes through `PrincipalClaimExtensions`
(`SubjectId()`, `DisplayName()`, `Email()`, `RoleValues()`), which probes both spellings and
de-duplicates roles; `client_id` stays as the **last** resort, which is the honest answer for a
client-credentials token.

🔴 **Why the tests did not catch it, and the rule that follows:** they build principals with
`new Claim("sub", …)` / `new Claim("role", …)` — the shape the runtime never delivers. A test that
constructs its own principal is testing its own assumption. `Controllers/PrincipalClaimExtensionsTests`
therefore leads with the **mapped** shape and keeps the raw one only as a compatibility case.

### Tenant authorization for user tokens is staged (AB#5054)

`TenantAuthorizationOptions.UserTokenEnforcement` (no `Disabled`; `LogOnly` | `Enforce`, platform
default `Enforce`) is set to **`LogOnly`** in `Program.cs` — registered *before*
`AddOctoTenantAuthorization(builder.Configuration)` so configuration still wins. No request outcome
changes; every access an enforcing run would refuse is logged with subject, client id and both
tenants. Flip an environment with `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` once that
log is clean.

Why staged: the gate had never produced evidence here, and the inventory of the LogOnly warnings
(AB#6728) shows real foreign-tenant callers. Do not repeat the older claim that none exist. octo-cli
sends the context's root-tenant token with `-tid <tenant>` to child tenants (per-tenant
authentication is AB#6836, and it is the whole communication-controller volume), and Refinery Studio
can load a page with a stale token of another tenant (AB#6731). `Enforce` therefore waits for both.
meshmakers-app was not seen in the inventory and gets no exemption; no exemption mechanism exists.

**The flip is configuration only**, no code or image change: `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce`
(chart value `services.communication.userTokenEnforcement`, AB#6834). The environment provider is added
with the `OCTO_` prefix in `Program.cs`, which is why the variable has that name. Order, Dash0 checks
and rollback (set `LogOnly` again): `docs/runbooks/user-token-tenant-gate-enforce-rollout.md`.

Tests (AB#6833), in `tests/CommunicationControllerService.Tests/Configuration/`:
`TenantGateRestNegativeTests` (a real Kestrel host through the real `UseOctoTenantAuthorization()` and
the `{tenantId:tenantId}` constraint: a user token of tenant A on a tenant-B route is 403 with the JSON
body and the action does not run; matching tenant passes; no `tenant_id` is 403; `LogOnly` is the
rollback), `TenantGateProgramPinTests` (the order in `Program.cs` pinned at the source, `Enforce` wins
from the key and from the environment variable over the code default, no endpoint carries
`IAllowParentTenantAdministration`) and `TenantGateHubReferenceTests`.

**What the gate does not cover, and never will:** `/{tenantId}/adapterHub` short-circuits it, because a
SignalR client sends `?access_token=` and no `Authorization: Bearer` header on the WebSocket / SSE
transports, so the middleware returns early. The hubs are outside this gate; the tenant isolation of the
adapter hub is `AdapterHubAuthorizationFilter` (see "The adapter hub, and why its gate has a second
half" above), and `TenantGateHubReferenceTests` documents both halves for a user token.
`GET {tenantId}/v1/communication/ping` is `[AllowAnonymous]` and is skipped by design.

### Tenant authorization for service tokens (AB#5032)

`Program.cs` calls `AddOctoTenantAuthorization(builder.Configuration)` so the staged narrowing of the
client-credentials exemption in `UseOctoTenantAuthorization()` is settable per environment
(`OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT` = `Disabled` | `LogOnly` (default) | `Enforce`,
plus `…__CROSSTENANTSERVICECLIENTIDS__n`). Defaults keep today's behaviour and only add an audit log.
Full rationale in octo-common-services CLAUDE.md § "Tenant Authorization — the service-token
exemption". Note this service sets `jwt.Audience = octoAPI`, so it is *not* among the repos running
with `ValidateAudience = false`.
