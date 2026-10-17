# Rollout: `Enforce` on the user-token tenant gate of asset-repo and communication-controller (AB#6728, AB#6833)

Both services run `UseOctoTenantAuthorization()` (octo-common-services). It compares the `tenant_id`
claim of a **user token** (a token with a `sub` claim) with the tenant in the route
(`{tenantId}/v1/...`, `tenants/{tenantId}/GraphQL`). Mode `LogOnly` logs "would be denied" and lets the
call through; mode `Enforce` answers **403** with a JSON body `{"message": ...}` and the action never
runs. Both services ship on `LogOnly` as code default (registered before the configuration binding, so
the operator value wins). **The flip is configuration only: no code change, no image change.**

Companion runbook for the SignalR hubs: [`hub-authorization-enforce-rollout.md`](hub-authorization-enforce-rollout.md)
(a different switch, see "What this gate does not cover").

## 1. The value to set, per service

| Service | Value (environment variable) | Chart value (octo-helm-core, AB#6834) |
|---|---|---|
| octo-asset-repo-services | `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` | `services.assetRepository.userTokenEnforcement: Enforce` |
| octo-communication-controller-services | `OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce` | `services.communication.userTokenEnforcement: Enforce` |

- Allowed values: `LogOnly` (default) and `Enforce`. There is no `Disabled`.
- The value goes into the per-cluster values file of `octo-mesh-deployment`
  (`clusters/<cluster>/values-octo-mesh.yaml`), never into a pipeline definition. One value per service,
  so one service can be rolled back alone.
- The chart value exists only from the octo-helm-core release of AB#6834 on. Before that release the
  chart renders no such variable and the cluster cannot be flipped; check the chart version the cluster
  runs first.
- The equivalent configuration key is `TenantAuthorization:UserTokenEnforcement`.

## 2. Order

1. **test-2**, then **staging-1**, then **prod-2**, then **prod-1**.
2. **Each cluster only with Gerald's explicit go for that cluster, given in the session that does the
   flip.** A go for one cluster is no go for the next. staging-1, prod-2 and prod-1 additionally wait
   for the Studio fix AB#6731 (released), and prod inventories wait for it too.
3. Per cluster, one service at a time: the communication-controller first (few callers), asset-repo
   after a clean look at the first one. Roll the pod once after the deployment change and count the
   observation window from that restart.
4. Gates before the first flip on test-2 (AB#6728): chart released (AB#6834), octo-cli authenticates per
   target tenant (AB#6836), tests of both services merged (AB#6832, AB#6833).

## 3. Known callers (what Enforce changes for them)

| Caller | Today | Outcome after Enforce |
|---|---|---|
| octo-cli | sends the context's root-tenant token with `-tid <tenant>` to child tenants (about 90 percent of the LogOnly volume on test-2, including all hits on the communication-controller) | 403 until the per-tenant authentication of **AB#6836** is released. Operator sweeps (LibraryStatus, blueprints, clearCache, pipeline debug) stop working before that. |
| Refinery Studio | a stale token of another tenant on a page load (guard fall-through), bug **AB#6731** | 403 on the page's calls until AB#6731 is released. No exemption. |
| meshmakers-app | not seen on test-2 or staging-1 | none expected; the app degrades on a 403. No exemption unless its need is evidenced. |
| energy-community-app | tokens of tenants energytest/eegroider/mrotest on the host tenant's GraphQL (asset-repo only) | open owner decision (Frigg/Sol), not a communication-controller caller. |

No exemption mechanism exists or is built here. No endpoint of either service carries
`IAllowParentTenantAdministration`; marking one would let a parent tenant's user token address a child's
data, so the marker is not an exemption route.

## 4. Steps per cluster

**Before**

1. Dash0 (dataset = cluster), read-only, **counts only, no token content, no payloads**. Logger
   `TenantAuthorizationMiddleware`, severity WARN, body containing
   `would be denied with UserTokenEnforcement=Enforce`. Group by the attributes `ClientId`,
   `TokenTenantId`, `RouteTenantId` and the service; do not select subjects or headers. Note the
   7-day and 24-hour counts per service and client.
2. Every client in the list must be one that the table in section 3 explains and that is fixed (octo-cli
   per-tenant, Studio released). An unknown client is a no-go: find its owner first.
3. Confirm the running value: the deployment values show `LogOnly` and the warnings exist.

**Flip**

4. Set the value of section 1 for one service in the cluster's values file, merge via the normal
   `octo-mesh-deployment` pull request, deploy through the normal pipeline. Confirm the rollout finished
   (pod restarted).

**After (wait about 7 minutes: Dash0 lags the real state by 5 to 7 minutes)**

5. The `would be denied` warnings of that service **stop** (zero after the restart).
6. `Denied: user token of subject ... addresses tenant ...` lines appear, if any foreign-tenant call
   happens. Count them per `ClientId`. Only expected callers may show up; a denied unexpected caller
   is a finding, not noise.
7. A same-tenant call succeeds (a normal Studio page or an octo-cli call with a token of that tenant)
   and a foreign-tenant user token on a tenant route returns 403 with the JSON body. Record status codes
   only; never print a token.
8. Failed checks and issues of the cluster in Dash0 do not grow beyond what step 6 explains.

**Record** the state of the cluster (service, value, time of the restart, counts before and after) in a
comment on AB#6728.

## 5. Rollback

Set the same value back to `LogOnly` (values file, pull request, deploy; or the environment variable
directly where the cluster is managed that way) and restart the pod. Effect: foreign-tenant calls pass
again and the warnings reappear; nothing else changes. Check with Dash0 that the `would be denied`
warnings are back for the callers that were denied, and that the 403 from the gate stopped. Rolling one
service back does not touch the other.

## 6. What this gate does not cover

- **SignalR hubs.** A SignalR client sends its token as `?access_token=`, not as an `Authorization`
  header, so `TenantAuthorizationMiddleware` returns early on every hub request. The tenant isolation of
  `/{tenantId}/adapterHub` is the hub authorization filter (`AdapterHubAuthorizationFilter`,
  switch `OCTO_ADAPTERHUBAUTHORIZATION__MODE`), already `Enforce` on all clusters (AB#5977). Flipping
  this gate changes nothing for hubs. Reference tests: `TenantGateHubReferenceTests`.
- **Tokens without a `sub` claim** (client credentials) are judged by the separate service-token switch
  `OCTO_TENANTAUTHORIZATION__SERVICETOKENENFORCEMENT`, not by this one.
- **`[AllowAnonymous]` routes** and requests without a bearer token are skipped by design.

## 7. Proof in the repositories

- communication-controller: `tests/CommunicationControllerService.Tests/Configuration/TenantGate*Tests.cs`
  (real Kestrel host, real `UseOctoTenantAuthorization()` and route constraint; 403 and the action does
  not run; `Program.cs` order pinned at the source; `Enforce` wins from the key and from the
  environment variable; no endpoint carries the parent-tenant marker).
- asset-repo: the tests of AB#6832 (REST and GraphQL).
