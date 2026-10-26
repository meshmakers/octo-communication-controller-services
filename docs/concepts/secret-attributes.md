# Secret attributes in the Communication Controller (AB#5528 / AB#5537)

Concept: `octo-construction-kit-engine/docs/concept-secret-attribute-type.md`. This page covers what
the controller does with `Secret` attributes (WP7): phase 2 (consumers handle ciphertext) and the
System.Communication model change of phase 3.

## One reveal point: `IWorkloadEncryptionService`

Every server-side read of a credential goes through `IWorkloadEncryptionService.Reveal(storedValue,
SecretAccessContext)`. It takes the **raw** attribute value (`GetAttributeValueOrDefault`, never the
generated property), so the same code works for a String attribute (before 4.8) and for a Secret
attribute (4.8 and later):

| Stored value | Result |
|---|---|
| `null` | `null` |
| plain `string` | returned as is (String attribute) |
| `string` `enc:v1:…` | decrypted with `CommunicationController:InstanceSecretKey`, else with the key ring's `LegacyV1Key` |
| `string` `enc:v2:<kid>:…` (or another `enc:` version) | **refused** (`InvalidOperationException`) — nothing legitimate writes one into a String slot |
| `RtSecretValue` protected / legacy | `ISecretAttributeProtector.Unprotect` (counted `octo.secrets.decrypt`; legacy clear text also counts `octo.secrets.plaintext_reads`) |
| `RtSecretValue` pending | `null` (write-only, never revealed) |

**No decryption oracle.** A Secret value is decrypted exactly once. A revealed plaintext that looks
like an envelope (ciphertext typed into a Secret attribute, for example the output of
`encrypt-value`) is the value the author wrote and is shipped as is, never decrypted again —
otherwise anyone allowed to write a Secret attribute could have an envelope copied from elsewhere
decrypted into a deployed workload. For the same reason a String slot only ever decrypts legacy
`enc:v1`.

`Encrypt` (the `encrypt-value` endpoint) keeps writing `enc:v1` with the instance key, exactly as
before the key ring; it never writes `enc:v2` (key-ring envelopes exist only inside Secret
attributes, written by the engine). Input that already is `enc:v1` passes through; any other `enc:`
envelope is rejected with 400. **Deprecated:** `encrypt-value` and `enc:v1` in
`ValueOverride.Value` with `IsSecret` keep working (decrypted at deploy, as before), but new secret
overrides belong in `ValueOverride.SecretValue` as plaintext — the engine encrypts them.

`Program.cs` registers `IInstanceSecretCrypto` next to `AddRuntimeEngine()`, so DI picks the
`InstanceSecretCrypto(ISecretAttributeProtector)` constructor and that one can decrypt `enc:v2` too.

## Where secrets leave the controller

| Sink | Code | Values |
|---|---|---|
| Adapter pipeline configuration (SignalR, authenticated adapter hub) | `AdapterService.CreatePipelineConfigurationAsync` → `ConfigurationSecretSerializer.SerializeWithRevealedSecrets` | every Secret attribute of every projected configuration (SFTP/SAP/e-mail/Loxone/finAPI passwords, private keys, Graph/finAPI/service-account client secrets, API keys, Discord bot token, Grafana admin password — the Grafana provisioning nodes run in the mesh adapter and read it from here) |
| Operator deploy notification (`WorkloadDeployedDto`) | `DeploymentSiteService.BuildWorkloadDeployedDtoAsync` | secret `ValueOverride`s, Helm repository password, the adapter's own client secret |
| Identity bus command | `PipelineServiceAccountProvisioningService` | the service account's client secret (reconcile re-sends it, rotation replaces it) |

`SerializeWithRevealedSecrets` produces the same camelCase JSON as `Serialize()`; only Secret values
change from `{"isSet":…}` to the plaintext string, in the `attributes` dictionary and in a typed
entity's generated property, at any record depth. Without any Secret value it returns `Serialize()`
itself. A value that cannot be read is shipped as `null` with an error log naming tenant, CK type,
attribute and key id, never the value (`IWorkloadEncryptionService.RevealOrNull`, decisions
2026-10-06 item 2): an unknown key id (`KeyMissing`, e.g. after a restore from another environment —
the ciphertext stays stored and becomes readable once its key is added), a tampered / wrong-key or a
corrupt value. A pod without any key ring logs the configuration error and ships `null` as well.
The rest of the configuration still deploys. The deploy notification uses the same `RevealOrNull`:
an unreadable Helm repository password is `null`, an unreadable `ValueOverride.SecretValue` deploys
as an empty value (no fallback to the legacy `Value`), and an unreadable adapter client secret
deploys the adapter without its own credentials.

Only the adapter channel reveals: `IAdapterService.GetAdapterConfigurationAsync` takes
`revealSecrets` (default `false`). The registration and deploy paths pass `true`; the REST read
`GET {tenantId}/v1/adapter/{adapterRtEntityId}` (read-only policy) keeps the `{"isSet":…}` marker.

Values are never logged. The adapter cache no longer dumps the configuration it reloads.

Note: like every projected configuration, a changed secret reaches a running adapter only after the
pipeline / data flow is redeployed.

## Presence checks without decrypting

The deploy guard, the identity health and the reconcile only need to know whether a client secret
is usable: `IWorkloadEncryptionService.DescribeSecret(raw)` (the engine's
`ISecretAttributeProtector.DescribeSecret` for a Secret value), `IsSet` = usable. It never
decrypts:

| Stored | Usable |
|---|---|
| protected, key id in the ring | yes |
| protected, key id **not** in the ring (`KeyMissing`) | no — health reports `secret-key-missing`, the reconcile does not replace it |
| legacy clear text / `enc:v1` in a Secret slot, pending value | yes |
| legacy clear text that is exactly a seed placeholder (`<…>`, `TODO_SET_<NAME>`) | no (not set until the migration converts it) |
| `enc:v2` text in a legacy slot (corrupt) | no |
| String attribute (before 4.8): blank or an exact seed placeholder | no; any other text yes |

Placeholders have no meaning anywhere else (decisions 2026-10-06, item 1): a `<…>` value entered
through an API is stored encrypted and is an ordinary secret — the reconcile re-sends it, the
adapter authenticates with it. `IsSecretUsable(string?)` on a revealed value only rejects blank text.

The impersonation-actor check additionally reveals a `LegacyPlaintext` value (strict mode rejecting
legacy clear text makes it "not usable").

## Service accounts and impersonation (decision 7, AB#5114)

* Reconcile reveals the stored secret before re-sending it. If it cannot be decrypted — no key
  ring, tampered, or its key id is not in the ring (`KeyMissing`, logged as an error naming the key
  id) — the reconcile stops before writing anything, so a configuration problem never rotates a live
  credential, and a restored secret is not overwritten while its key could still be added. Re-entry,
  `rotateSecret` or adding the key resolves it. The impersonation switch skips such an account
  (`SkippedIncomplete`).
* A **standalone** account (per-pipeline override) without a usable secret stays on impersonation
  only when **every** adapter running one of its pipelines has its own account with a usable secret
  and a different client — the same strict check the switch uses. Then the reconcile issues no
  secret for it and the identity client receives a throwaway secret that is stored nowhere (a
  legacy clear-text seed placeholder reads as not set and is never sent as the client secret). If a single using adapter
  cannot act as it, the reconcile issues and stores a fresh secret, as before AB#5114.
* `POST {tenantId}/v1/serviceAccount/switchToImpersonation?apply=false&redeployDataFlows=true`
  (`IServiceAccountImpersonationSwitchService`): dry run by default, reports per account
  `WouldSwitch`, `Switched`, `SwitchedPendingInvalidation`, `AlreadyImpersonating`,
  `SkippedAdapterOwnIdentity`, `SkippedNoCapableActor`, `SkippedIncomplete`, `Failed`. `apply=true`
  needs `OCTO_SERVICEACCOUNTIMPERSONATION__ALLOWSECRETCLEARING=true` (else 409) and then runs, in this
  order:
  1. (a) per eligible account, converge the identity client with its MayActAs edges and the
     **current** secret (nothing rotates);
  2. (b) per eligible account, clear the stored secret — a failed write is `Failed`, nothing else
     happens;
  3. (c) redeploy the data flows of the affected pipelines (each once) — adapters cache the
     configuration with the old secret until then;
  4. (d) only for accounts whose data flows **all** redeployed: send identity a throwaway secret so
     the old one stops working (`Switched`).

  Without the redeploy (`redeployDataFlows=false`), after a failed redeploy or an unresolvable data
  flow, or when identity cannot be reached in (d), the old secret stays valid and the account is
  reported as `SwitchedPendingInvalidation`. A re-run with `redeployDataFlows=true` completes it: a
  secret-less standalone account whose actors all qualify is redeployed and invalidated again
  (idempotent — the identity client already holds only a throwaway; the cost is one redeploy of its
  data flows per run). Before (d) the account is re-read; if it carries a stored secret again (a
  reconcile issued one meanwhile) nothing is invalidated.
  Eligible = standalone, usable secret, and every adapter running one of its pipelines has its own
  account with a usable secret and a different client.

**CLI.** `octo-cli -c SwitchServiceAccountsToImpersonation` (communication services group, since
release r3.5.20) calls this endpoint for the tenant of the current context (`--context` selects
another). It is a **dry run by default**: it prints the outcome per account and changes nothing.
What it switches: only the eligible accounts above, i.e. standalone accounts with a usable secret
whose adapters all have their own identity; adapter-own accounts are skipped by design
(`SkippedAdapterOwnIdentity`, see below). `-a` / `--apply` runs the switch (confirmation prompt;
`-y` skips it), `-rdf` / `--redeployDataFlows` `true|false` controls the data-flow redeploy
(default `true`) and `-j` / `--json` prints the raw report. Applying additionally needs the server
flag `OCTO_SERVICEACCOUNTIMPERSONATION__ALLOWSECRETCLEARING=true`, which is off by default; without
it the server answers 409.

**What is missing for adapter-own accounts.** The adapter's own account (the `PipelineServiceAccount`
edge) is the identity the pod logs in with (AB#5072: client id and secret travel in the Helm
values), and it is the actor of every impersonation. Clearing its secret would leave the adapter
without any credential. Impersonation for these accounts needs an installation-level adapter
credential that is not stored per tenant entity (for example an operator-issued or workload-identity
token the identity service accepts as the actor). Nothing in the controller, the operator, the
communication SDK or identity provides that today, so the switch skips these accounts
(`SkippedAdapterOwnIdentity`).

## System.Communication 4.8 (phase 3)

Minor version on the 4.x line (concept decision 2; 4.7.0 → 4.8.0), dependency `System-[2.5,3.0)`
(decision 12). The switch was first prepared as 3.42.0; that version was never released (it exists
on local tenants only) — the SECRET switch ships on the 4.x line as the next free minor after 4.7.0
(4.6.0 carried the DeploymentSite line's status history, 4.7.0 is the AB#6312 `ownership: Secret` on
`Adapter.AdapterConfiguration`); at the time of writing that is 4.8.0 — `ckModel.yaml` is authoritative.

| Attribute definition | Used by | Change |
|---|---|---|
| `Password` | Sap, Sftp, EMailSender, EMailReceiver, Loxone, Mqtt, HelmRepository, FinApi | `valueType: Secret`, `ownership: Secret` |
| `PrivateKey`, `PrivateKeyPassphrase` | Sftp | same |
| `ClientSecret` | FinApi, MicrosoftGraph, ServiceAccount | same |
| `ApiKey` | Ai, WeClapp | same |
| `BotToken` | Discord | same |
| `AdminPassword` | Grafana | same |
| new `SecretValue` | record `ValueOverride` (optional), `recordKey: Path` | the value of an `IsSecret` entry |

Every credential definition is used by credential slots only, so none had to be split.

**Every Secret slot is optional** (`isOptional: true`). A Secret is never seeded with a value (the
seed lint allows empty values only, and the app blueprints dropped their `TODO_SET_*`
placeholders), so a mandatory Secret slot would report `MandatoryAttributeMissing` on every fresh
install and fail under `StrictMandatoryValidation`. "Not set" is a valid state: the controller ships
the configuration without the credential, and the adapter node reports the missing credential when
it needs it. Mandatory → optional only relaxes the schema; no migration script is needed.

**`ValueOverride`: a new `SecretValue` member instead of turning `Value` into Secret.** `Value` also
holds the non-secret overrides (image tags, `publicUri`, `{{service.*}}` templates) that app
blueprints seed and the Studio shows and edits. As a Secret they would be hidden, and the seed lint
would reject the blueprints that seed them (MeshmakersAccounting.Host, EnergyCommunity.App,
SmartMeterInsights.Base, voest-app). So `Value` stays String (now optional) and secret entries keep
their value in `SecretValue`. `DeploymentSiteService` takes `SecretValue` for `IsSecret` entries and falls back
to a legacy `Value` (`enc:v1` written through `encrypt-value`, deprecated), so existing entries keep
deploying. `Path` is the record key (carry-over of an omitted `SecretValue` on update/replace): the
overrides are written through the asset repository only, so the deploy refuses a workload where two
entries carrying a `SecretValue` share a `Path` (ordinal comparison, like the carry-over). Entries
without a `SecretValue` keep the last-wins behaviour. Moving existing secret entries from `Value` to `SecretValue` is a follow-up (Studio writes
new ones to `SecretValue`, WP12).

No migration script: the engine's `CkModelMigrationService` runs
`ISecretMaintenanceService.NormalizePlaceholdersAsync` after every successful migration, which turns
stored placeholders (`<…>`, `TODO_SET_*`) and empty strings in the new Secret slots into "not set".
The seeds of the two service-managed blueprints only carry empty values for `Password`.

The engine source generator now types these properties as `RtSecretValue?`. Inside this repository
only `PipelineServiceAccountProvisioningService.BuildServiceAccount` assigned one (it now writes
`RtSecretValue.Pending(secret)`); every other reader already went through the raw value (phase 2).
