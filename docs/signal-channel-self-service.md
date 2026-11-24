---
description: "Signal channel self-service: registration, verification, history and the signal bridge client"
applies_to: **/SignalChannel*.cs, **/SignalBridge*.cs
---

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
