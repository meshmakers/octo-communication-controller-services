# Switching the `/adapterPoolHub` gate to `Enforce` per cluster

This is the operator plan for the last hub gate of the communication controller that still runs `LogOnly`: the adapter
pool hub (`AdapterPoolHubAuthorization:Mode`, environment variable `OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE`). The two
sibling hubs `/operatorHub` and `/{tenantId}/adapterHub` already run `Enforce`; their rollout, the decision counter and
the refusal triage are in [hub-authorization-enforce-rollout.md](hub-authorization-enforce-rollout.md). This plan only adds
what is different for the pool hub. The gate itself is described in `CLAUDE.md`, section "The adapter pool hub".

| Mode | A pool-member connection without a token, without scope `octo_api` or without a `tenant_id` claim | A registration into a pool of another tenant than the token's |
|---|---|---|
| `LogOnly` (code and chart default) | connects; warning line, counter outcome `would_refuse` | registered; warning line, counter `would_refuse` / `tenant_mismatch` |
| `Enforce` | refused with `HubException` "Adapter pool connection refused: …"; warning line, counter outcome `refused` | refused with `HubException` "Pool member registration refused: …"; error event in the pool's tenant; counter `refused` / `tenant_mismatch` |

Nothing in this document changes an environment. Every switch, test client and rollback is an environment write that the
ops agent executes per cluster. Who gives the go: on **test-2** the approval of the security work item that owns the switch
(the developer who runs the agent confirms it in the session); on **staging-1, prod-1 and prod-2** Gerald, in his own words,
for that cluster and that step, a rollback included.

## What the gate protects, and what it does not

The gate protects the **management channel of adapter pool members** (shared adapter leasing): a process that connects to
`/adapterPoolHub`, registers as a member of a pool and is then handed leases. A lease carries the **borrowing** tenant's
pipeline service-account credential. Under `LogOnly` a process without any token, or with a token of tenant A that
registers into a pool of tenant B, still becomes a member and receives leases, so it can obtain a third tenant's
service-account secret. `Enforce` closes that: only a token of the pool's own (lending) tenant with `octo_api` connects and
registers. The same binding applies to a late lease release that arrives on a new connection.

It does **not** cover, by design:

- **Authority inside the borrowing tenant.** That never comes from this connection; it arrives on the lease as the
  borrower's own credential and dies with the lease. Widening this gate (for instance accepting a system token) would make
  every pool member a standing cross-tenant credential.
- **The other two hubs.** `/operatorHub` and `/{tenantId}/adapterHub` have their own modes (`operatorMode`, `adapterMode`)
  and already run `Enforce`; this switch does not touch them.
- **Parent or ancestor tenants and the cross-tenant service-client allow-list.** The pool hub consults neither: a member
  of a child tenant's pool needs a token of exactly that tenant.
- **What a legitimate member does with a lease.** A member with a valid token of the lending tenant gets leases as
  before; the scheduler, lease expiry and the reaper are unchanged.

## Order

`test-2` → `staging-1` → `prod-1` → `prod-2`, one cluster at a time. A cluster is switched only after the previous one has
run `Enforce` for **1 day** (the observation window) without a refusal of a legitimate pool member (step 6 below). The
approval of one cluster does not carry over to the next. The dated schedule is in
[hub-authorization-enforce-rollout.md](hub-authorization-enforce-rollout.md#schedule-for-adapterpoolhub).

## Preconditions per cluster

- **P1** the octo-mesh chart deployed on the cluster renders `services.communication.hubAuthorization.adapterPoolMode`:
  the first chart release that contains it (3.5.80 or later; check the chart version of the cluster's last octo-mesh CD
  run, not only the pin on `main`). An older chart ignores the key without an error and the controller keeps `LogOnly`.
- **P2** the controller on the cluster exports the full decision baseline (the presence query of the rollout runbook,
  section 2, shows `adapterPoolHub` with 10 series). Without it a zero means nothing.
- **P3** the hub rules are live on the cluster and cover the pool hub: `OctoMeshHubAuthorizationWouldRefuse` and
  `OctoMeshHubAuthorizationRefused` (rule set `octomesh-communication-hub-rules`) aggregate `by (octo_hub_name, …)` without a
  hub filter, so `adapterPoolHub` is included. Their descriptions name the stop-gap variables of the other two hubs only;
  the alerting work item adds `OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE` there and confirms the coverage before the first
  switch. The positive control (below) must show up in `OctoMeshHubAuthorizationWouldRefuse` on the cluster.
- **P4** every pool member that is meant to run on the cluster authenticates: its pod presents a client-credentials token
  of the pool's own tenant with `octo_api` (the pool's service account). A member that has not reconnected since its
  credentials changed is absent from the inventory: roll the controller once after the last fix and count the window from
  there (rollout runbook, section 2).

Record P1 to P4 per cluster in a comment on the security work item before the gate check.

## Gate (per cluster, before the switch)

**An empty result only counts when the queries are proven to see the cluster's gate signals** (otherwise the gate fails
open: a dataset without controller logs, a controller without the zero baseline, or a changed log text also give zero).
Both of these are part of every gate check, on every cluster:

- **Every member was evaluated in the window**: a member is evaluated only when it connects, and a long-lived connection
  that predates the window leaves no decision and no line in it. So the window starts at a **controller restart** (the
  octo-mesh CD that brought the chart of P1 is one; otherwise roll the controller once, rollout runbook section 2): every
  member reconnects within minutes and is evaluated. Record the restart time. Read the counter as the raw value since that
  restart (it restarts at 0 with every pod), and let the window run at least 72 hours after it. A gate check whose window
  holds no controller restart is "not checked". A controller restart inside the window later on is fine (the members
  reconnect again); `increase()` handles it.
- **Coverage**: the cluster's Dash0 dataset holds communication-controller log lines on every day of the 7-day window
  (`log_attributes['logger'] LIKE 'Meshmakers.Octo.Backend.CommunicationControllerServices.%'`, count per day > 0), and the
  presence query of P2 returns `adapterPoolHub`. A day without lines is a gap: extend the window until 7 covered days are
  reached, or record the gap and do not switch.
- **Positive control**: right before the gate check, connect one throw-away SignalR client **without a token** to
  `https://<communication-host>/adapterPoolHub` (named in the pre-write list, closed right after). Under `LogOnly` it
  connects; the log query below must return its line with reason code `unauthenticated`, the counter query must show
  `would_refuse` / `unauthenticated` +1, and `OctoMeshHubAuthorizationWouldRefuse` must count it. Without that, the result
  is "not checked", not clean.

Then, over the window (the **last 7 days** before the gate check, starting at or after the restart above when that is more
recent; at least 72 hours), apart from the positive control: no `would_refuse` decision and no
"would be refused when AdapterPoolHubAuthorization:Mode is Enforce" line of a legitimate pool member. Every caller found
(token tenant, client id, reason code, the pool tenant of a registration) is, before the switch, either

1. **fixed**: the member gets the service account of its pool's own tenant with `octo_api` (pool redeploy, or the client
   configured with the right tenant), and a fresh `allowed` connection of that member is seen after the fix; or
2. **declared not legitimate** (the process must not be a pool member; `Enforce` refuses it on purpose), with the reason.

A cluster where the window shows **no pool member at all** passes the gate with that finding written down: `Enforce`
cannot lock anybody out there, and a pool created later starts under `Enforce` with a correctly provisioned member. "No
pool member" is proven by the decision counter, not by the gauge: P2 shows the `adapterPoolHub` series are exported, and
the sum of **all** `adapterPoolHub` decisions (allowed and would-refuse) since the restart above, apart from the positive
control, is 0, so nothing connected after the restart. A missing `octo.lease.pool.members` series alone is "not
checked", never "no members"; where the gauge has a series it must agree (0).

### Log lines (exact text)

All are `Warning`. A token is never logged; caller details are the connection id, client id, subject, scopes, token
tenant and the origin (path, remote address, forwarded chain, user agent).

| Mode | Logger | Fixed part to search for |
|---|---|---|
| `LogOnly`, connect | `…CommunicationControllerServices.Hubs.AdapterPoolHubAuthorizationFilter` | `Pool-member connection to /adapterPoolHub … and would be refused when AdapterPoolHubAuthorization:Mode is Enforce: … (reason code …)` |
| `LogOnly`, registration | `…CommunicationControllerServices.Hubs.AdapterPoolHub` | `Pool-member registration on connection '…' … and would be refused when AdapterPoolHubAuthorization:Mode is Enforce` |
| `LogOnly`, late release | `…CommunicationControllerServices.Services.LeaseService` | `A late release of lease '…' arrives on a connection with a token of tenant '…', but the lease belongs to a pool of tenant '…' and would be refused when AdapterPoolHubAuthorization:Mode is Enforce` |
| `Enforce`, connect | `…Hubs.AdapterPoolHubAuthorizationFilter` | `Refused a pool-member connection to /adapterPoolHub: …` |
| `Enforce`, registration | `…Hubs.AdapterPoolHub` | `Refusing a pool-member registration on connection '…': …` |
| `Enforce`, late release without a tenant-bound token | `…Services.LeaseService` | `Refusing a late release of lease '…': the connection presents no tenant-bound token` |
| `Enforce`, late release with another tenant's token | `…Services.LeaseService` | `A late release of lease '…' arrives on a connection with a token of tenant '…', but the lease belongs to a pool of tenant '…'; refused` |
| any, a member connected | `…Hubs.AdapterPoolHub` (`Info`) | `Pool member connected with connection id '…'` (absent where the cluster logs at `Warn`) |

The connect lines carry the structured attribute `HubAuthorizationReason` (`unauthenticated`, `missing_scope`,
`no_tenant_claim`); a registration into another tenant's pool is reason `tenant_mismatch` on the counter.

### Dash0 queries

The Dash0 dataset is named after the cluster (`k8s.cluster.name` is empty; select the dataset). Time range: the last
7 days. Metric (PromQL):

```promql
# Gate check: decisions of the pool hub over the window, by mode, outcome and reason (would_refuse must be 0)
sum by (octo_hub_authorization_mode, octo_hub_authorization_outcome, octo_hub_authorization_reason) (
  increase({otel_metric_name="octo.communication.hub.authorization.decisions", octo_hub_name="adapterPoolHub"}[7d]))

# The same as raw values since the controller restart that starts the window (run when no restart followed it);
# the sum over every outcome is the number of pool-hub connections and registrations evaluated since the restart
sum by (octo_hub_authorization_mode, octo_hub_authorization_outcome, octo_hub_authorization_reason) (
  {otel_metric_name="octo.communication.hub.authorization.decisions", octo_hub_name="adapterPoolHub"})
```

Logs with D0QL (`sql`):

```sql
-- Gate check: every pool-hub line the switch would turn into a refusal
SELECT log_attributes['logger'] AS logger, log_attributes['HubAuthorizationReason'] AS reason,
       body, count() AS lines, min(timestamp) AS first_seen, max(timestamp) AS last_seen
FROM logs
WHERE body LIKE '%would be refused when AdapterPoolHubAuthorization:Mode is Enforce%'
GROUP BY logger, reason, body ORDER BY first_seen

-- The same lines by kind, with the token tenant and the client of the connect lines
SELECT multiIf(body LIKE 'Pool-member connection%', 'connection', body LIKE 'Pool-member registration%', 'registration',
               body LIKE 'A late release%' OR body LIKE 'Refusing a late release%', 'late_release', 'other') AS kind,
       regexpExtract(body, 'token (?:of )?tenant ''([^'']*)''') AS token_tenant,
       regexpExtract(body, 'pool of tenant ''([^'']*)''') AS pool_tenant,
       regexpExtract(body, '(no token presented|client_id ''[^'']*'')') AS client,
       log_attributes['Reason'] AS registration_reason, count() AS lines, min(timestamp), max(timestamp)
FROM logs
WHERE body LIKE '%AdapterPoolHubAuthorization:Mode is Enforce%'
GROUP BY kind, token_tenant, pool_tenant, client, registration_reason ORDER BY kind

-- Coverage: controller lines per day (D0QL has no toDate; cut the date from the timestamp)
SELECT substring(toString(timestamp), 1, 10) AS day, count() AS lines
FROM logs
WHERE log_attributes['logger'] LIKE 'Meshmakers.Octo.Backend.CommunicationControllerServices.%'
GROUP BY day ORDER BY day
```

Are there pool members at all? The `Info` line "Pool member connected" disappears where a cluster logs at `Warn`, so read
the pool gauge instead (PromQL):

```promql
max(max_over_time({otel_metric_name="octo.lease.pool.members"}[7d]))
```

D0QL rejects `position()`, `CASE`, `toDate` and `toStartOfDay`; use `LIKE`, `multiIf` and `substring`.

The extracted columns only describe the lines; the `WHERE` clause alone decides which lines are counted, so an extract that does not match leaves a column empty and never hides a line. `token_tenant` is `<none>` for a connection
without a token; `pool_tenant` is set on registration and late-release lines; `client` is `no token presented` or the
`client_id '…'` of the caller (marked `unverified` when the token was presented and rejected).

After the switch, use the same query with `body LIKE 'Refused a pool-member connection%' OR body LIKE 'Refusing a
pool-member registration%' OR body LIKE 'Refusing a late release%' OR body LIKE 'A late release of lease%; refused'`, and
the counter query with `octo_hub_authorization_outcome="refused"`. A refused late release has no counter series and no
alert rule: the 1-day watch reads it from this log query.

## Exact value

In `octo-mesh-deployment`, `clusters/<cluster>/values-octo-mesh.yaml`, in the existing `hubAuthorization` block:

```yaml
services:
  communication:
    hubAuthorization:
      adapterMode: Enforce
      operatorMode: Enforce
      # /adapterPoolHub gate: Enforce since <date>, go: <who, date>, exit: set LogOnly and run the octo-mesh CD
      adapterPoolMode: Enforce   # LogOnly | Enforce -> OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE
```

Without the key the chart renders `LogOnly`. Any other value (also a lower-case mode or a YAML boolean), a misspelled key
under `hubAuthorization` or the key under another service fails the render. `kubectl set env` is not a substitute (the
next CD run undoes it). The CD rolls the controller pods, so every pool member reconnects and is evaluated at once.

## Switch, per cluster

1. **Gate check**, recorded as a comment on the security work item: P1 to P4, coverage per day, the presence query, the
   positive control (time, and that the log query, the counter and the rule saw it), the queries with their time range,
   every caller found with token tenant, client id and reason, and for each the fix or the reason it is not legitimate.
2. **Go** for this cluster: on test-2 the approval of the security work item, confirmed by the developer who runs the
   agent; on staging-1, prod-1 and prod-2 Gerald's go, in his own words.
3. **Change** (ops): PR in `octo-mesh-deployment` that sets the value with the comment above, plus the pre-write impact
   list: the rendered diff of the octo-mesh release changes only `OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE` on the
   communication controller deployment.
4. **Roll out** with the cluster's octo-mesh CD run. Within 24 hours before the rollout, run the gate queries again
   (counter and log query, window up to the moment of the re-run): the result is empty apart from callers already
   resolved in step 1. Otherwise repeat the gate check; a go given on an older gate check does not cover new callers.
5. **Smoke test** right after the rollout:
   - a throw-away SignalR client **without a token** is refused at connect ("Adapter pool connection refused"), the
     counter shows `refused` / `unauthenticated` with `octo_hub_authorization_mode="enforce"`, and the `Enforce` line appears;
   - the same client with a client-credentials token of an internal test tenant A (scope `octo_api`, created for the test
     and named in the pre-write list) connects, and its `RegisterPoolMemberAsync` with a pool tenant B ≠ A is refused
     ("Pool member registration refused"), counter `refused` / `tenant_mismatch`;
   - every legitimate pool member of the cluster reconnects (`allowed` decisions after the restart, "Pool member
     connected" lines where the cluster logs at `Info`; `octo.lease.pool.members` back at its value before the rollout) and
     takes a lease: a pipeline execution on a borrowing tenant runs green. On a cluster without pool members write that down instead.

   Remove the test client and its credentials afterwards. Write the evidence into a comment on the security work item.
6. **1-day watch**: the `Enforce` lines (the post-switch log query, which also covers refused late releases) and
   `OctoMeshHubAuthorizationRefused` name no legitimate pool member. A legitimate
   refusal means: fix the member's credentials or roll back (on staging-1, prod-1 and prod-2 a rollback only with Gerald's
   go), and record it. Then the next cluster may start.

## Rollback

Set `adapterPoolMode` back to `LogOnly` in the cluster values (removing the key works only while the chart default is
`LogOnly`; after the last step below it would keep `Enforce`) and run the same octo-mesh CD again (go
as in step 2). Refused members retry on their own and are let in on the next attempt; a member still offline after about
5 minutes: restart its pod. Faster stop-gap through Breakglass or Semaphore, never by asking for RBAC:
`OCTO_ADAPTERPOOLHUBAUTHORIZATION__MODE=LogOnly` on the controller deployment, always followed by the values change, or the
next deploy sets `Enforce` again. First read who is refused (rollout runbook, section 4): a probing client needs no action.

## Last step: code default

Once all four clusters have run `Enforce` for one release train, the code default of `AdapterPoolHubAuthorization:Mode` and
the chart default can flip to `Enforce` (separate small PRs, CHANGELOG entries). The cluster values stay until every
cluster runs a release and chart built with the new defaults.

## Per-cluster checklist

Dates are decided by Gerald; the empty cells are filled at the gate check and the switch.

| Cluster | P1 chart deployed | P3 rules cover the pool hub | Gate check comment (7 days, coverage, positive control) | Go (who, date) | `Enforce` set (values commit) | Smoke test comment | 1-day watch recorded |
|---|---|---|---|---|---|---|---|
| test-2 | | | | security work item approval, confirmed by the developer running the agent | | | |
| staging-1 | | | | Gerald, own words | | | |
| prod-1 | | | | Gerald, own words | | | |
| prod-2 | | | | Gerald, own words | | | |
