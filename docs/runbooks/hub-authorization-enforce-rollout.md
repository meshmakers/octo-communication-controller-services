# Rollout: `Enforce` on the adapter and operator hubs (AB#5063, AB#5059, AB#5528 phase 3)

The controller gates both SignalR hubs with a staged check (`CLAUDE.md` § "The SignalR hubs are not
covered by any of that"). Decision D2: **`Enforce` on every cluster before the communication SECRET
switch.** `LogOnly` is the review stage and the emergency exit.

| Hub | `Enforce` refuses | Who connects | Credential they need |
|---|---|---|---|
| `/operatorHub` (`operatorMode`) | no token, or a token without scope `octo_api` | one central operator per installation (+ the test-2 dev lane), the edge-device operators (they dial into **test-2**) | operator client (`operator.authentication.*`, AB#5062) |
| `/{tenantId}/adapterHub` (`adapterMode`) | no token, no `octo_api`, no route tenant, no `tenant_id` claim, `tenant_id` ≠ route tenant (unless the client is in `TenantAuthorization:CrossTenantServiceClientIds`) | every adapter pod — central and edge, mesh adapter and the separately built ones (finAPI, EDA, Loxone, SAP, MQTT, …) | the adapter's own pipeline service account (AB#5027), projected at workload deploy (AB#5072) and used by `AdapterAccessTokenService` (octo-communication-sdk r3.4.113+) |

Schedule: Mon 12.10. release train (chart + operator credentials, everything still `LogOnly`) →
12.–15.10. log review → 15.–19.10. `Enforce` per cluster, one hub at a time.
Cluster order: **test-2 (incl. edge) → staging-1 → prod-2 → prod-1**. Per cluster: **operator hub
first** (few, known consumers), **adapter hub a day later**.

## 1. Preconditions (per cluster)

1. **Controller** with the decision counter (this repo, AB#5528 `octo.communication.hub.authorization.decisions`)
   and the `octo-mesh` chart with `services.communication.hubAuthorization.*` deployed. The deployment
   pins both modes in `octo-mesh-deployment/clusters/<cluster>/values-octo-mesh.yaml`:
   `kubectl -n octo get deploy <controller> -o yaml | grep -A1 HUBAUTHORIZATION` shows `LogOnly` twice.
2. **Operator client provisioned** (Gerald, per cluster — see the provisioning list in the AB#5528 work
   item): confidential `client_credentials` client, scope `octo_api`, in the system tenant, secret in
   Vault `meshmakers/<cluster>/octomesh` as `operator_client_id` / `operator_client_secret`; operator
   (chart 0.12.0+, image r3.4.149+) redeployed. Operator log after start:
   `Operator access token acquired for client … in tenant 'octosystem'` — and **not**
   `No Operator:Authentication:ClientId / IssuerUri configured` or `Could not acquire an access token`.
3. **Adapters authenticate**: every adapter image is built against octo-communication-sdk r3.4.113+
   (externally built adapters must be re-released after each SDK/System bump anyway), every adapter
   has a provisioned service account (Studio → adapter → service account health = Healthy), and its
   workload was **deployed after** the account existed (credentials are projected only at deploy
   time). Adapter log: `Adapter access token acquired for client octo-pipeline-sa-…`; a
   `No Adapter:ClientId / Adapter:IssuerUri configured` line means the pod still connects anonymously
   — redeploy the workload (`DeployWorkload`).
4. **Edge (test-2 only)**: `edge_operator_client_*` in `meshmakers/test-2/octomesh`, each
   `deploy-communication-operator-edge-mm-octo-mesh-edge-000N` pipeline run once after that. A device
   that is offline during the switch cannot be fixed remotely and will be refused once it comes back —
   list the devices that were seen in the inventory and decide explicitly about the rest.

## 2. Read the LogOnly evidence

**Make the inventory complete first.** A connection is only evaluated when it is made; an adapter
that has not reconnected since its credentials changed is simply absent (absence is not evidence).
After the last fix on a cluster, roll the controller once (`deploy-octo-mesh-core-services*` or
`kubectl rollout restart` via Breakglass/Semaphore) — every operator and adapter reconnects and is
evaluated within minutes. Count the review window from that restart.

**Metric (Dash0, dataset = cluster)** — the go / no-go signal. The counter is cumulative and restarts
at 0 with every controller pod; the controller writes a 0 for every series at startup, so both the raw
value (count since the last restart) and `increase()` work. Use the raw value since the restart from
the step above, it covers the whole review window:

```promql
# Must be 0 (or empty) on every series for the whole review window (>= 72 h after the restart), per hub
sum by (octo_hub_name, octo_hub_authorization_reason) (
  {otel_metric_name="octo.communication.hub.authorization.decisions",
   octo_hub_authorization_outcome="would_refuse"})

# The gate is alive (allowed > 0 since the restart) and shows the mode in effect
sum by (octo_hub_name, octo_hub_authorization_mode, octo_hub_authorization_outcome) (
  {otel_metric_name="octo.communication.hub.authorization.decisions"})

# Trend over the window (e.g. a dashboard panel); a controller restart inside the window is handled by increase()
sum by (octo_hub_name, octo_hub_authorization_reason) (
  increase({otel_metric_name="octo.communication.hub.authorization.decisions",
            octo_hub_authorization_outcome="would_refuse"}[72h]))
```

Note that long-lived connections are counted once, when they connect: a quiet `increase(allowed[24h])`
on day three is normal, the raw value is the liveness check.

`reason` tells you what kind of work is left: `unauthenticated` (no token — client not configured,
image too old, or not redeployed), `missing_scope` (token without `octo_api` — wrong client setup),
`tenant_mismatch` / `no_tenant_claim` (adapter token of another tenant / minted without
`acr_values`), `no_route_tenant` (malformed connection). `allowed` with `cross_tenant_client` means
the allow-list was used — every entry there must be intentional.

**Logs — whom to fix.** Every `would_refuse` decision has a warning line on the controller:

```
Adapter connection to /<tenant>/adapterHub <reason> and would be refused when AdapterHubAuthorization:Mode is Enforce: connection '…', client_id '…', sub '…', scopes '…', token tenant '…', route tenant '…' (reason code unauthenticated)
Operator connection to /operatorHub does not satisfy 'SystemCommunicationApiPolicy' and would be refused when OperatorHubAuthorization:Mode is Enforce: connection '…', unauthenticated (reason code unauthenticated)
```

Dash0 logs: body contains `would be refused when` (structured attribute `HubAuthorizationReason`).
Loki (`/octo-logs`): `{namespace="octo"} |= "would be refused when"`; group by route tenant to get
the list of adapters per tenant. An anonymous line carries no client id — match it to a pod via the
route tenant and the time of the connect (adapter pod start / reconnect), or via the adapter's own
`No Adapter:ClientId` warning.

**Go criteria per hub:** `would_refuse` = 0 over the window, `allowed` > 0, every known consumer
(central operator, dev operator on test-2, each edge device; each deployed adapter) has an `allowed`
connection or a token-acquired log line in the window.

## 3. Switch one hub on one cluster

1. In `octo-mesh-deployment/clusters/<cluster>/values-octo-mesh.yaml`:

   ```yaml
   services:
     communication:
       hubAuthorization:
         operatorMode: Enforce   # adapterMode stays LogOnly until its own switch
   ```

2. Deploy the core services through the regular CD (`deploy-octo-mesh-core-services-test-2.yml`, or
   `deploy-octo-mesh-core-services.yml` for staging/prod). The controller restarts, so every
   connection is re-evaluated immediately.
3. Verify within 15 minutes:
   - the raw `octo_hub_authorization_outcome="refused"` value stays 0 since the restart (first query above
     with `refused`), and the series for the switched hub now carry `octo_hub_authorization_mode="enforce"`;
   - operator hub: every pool `CommunicationState=Online` (`octo-cli -c GetPools` per tenant, or Studio);
     test-2: the edge pools too;
   - adapter hub: adapters `Online` + `Configured`, a manual pipeline execution on one adapter per
     tenant succeeds, one `DeployWorkload` round trip works;
   - controller log has no `Refused an operator connection` / `Refused an adapter connection` lines.
4. Note the switch (date, cluster, hub) in AB#5528 before moving to the next one.

## 4. Rollback

Set the mode back to `LogOnly` in the same values file and run the same CD pipeline. Refused clients
retry on their own (operator and adapter hub clients reconnect in a loop) and are let in on the next
attempt; a pool or adapter still offline after ~5 minutes: restart that operator / adapter pod.

Faster stop-gap (Breakglass / Semaphore, never by asking for RBAC):
`kubectl -n octo set env deploy/<controller> OCTO_OPERATORHUBAUTHORIZATION__MODE=LogOnly`
(or `OCTO_ADAPTERHUBAUTHORIZATION__MODE`). Always follow up with the values change, or the next
deploy sets `Enforce` again.

## 5. Operator credential rotation

Rotate by client, not by secret: octo-cli has client secret verbs (`CreateApiSecretClient` /
`UpdateApiSecretClient`), but whether a client can hold two valid secrets at once after the OpenIddict
migration (AB#4989) is not verified, and a second client works regardless. Create a second client
(`AddClientCredentialsClient -id octo-communication-operator-<n>`, same shape), put its id and secret
into Vault (`vault kv patch secret/meshmakers/<cluster>/octomesh operator_client_id=… operator_client_secret=…`
or `setup-vault-octomesh-secrets.yml`, which preserves the other keys), redeploy the operator (the
chart's checksum annotation restarts the pod), check `Operator access token acquired for client
octo-communication-operator-<n>`, then `DeleteClient` the old one. Deleting the old client before the
new pod is up refuses the operator at its next reconnect once the hub enforces.
