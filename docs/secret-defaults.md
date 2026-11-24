---
description: "Secret defaults and platform-only secrets: a SecretDefault never reaches an adapter, provisioning and removal"
applies_to: **/Services/SecretDefault*.cs, **/Controllers/SecretDefaultsController.cs
---

## Platform-only secrets: a SecretDefault never reaches an adapter

A secret default an Application declares (`SecretDefaults`, System.Communication 4.15.0) and the controller
projects into every served tenant as `System.Communication/SecretDefault` is **platform-only**: it may be used only inside
platform building blocks that return a result and never the value. No tenant pipeline, user or role, tenant admins
included, may obtain it. The rule is enforced **by type, in code**, in two repositories; there is no CK or engine change.

- **Here, the configuration list of an adapter.** The controller ships the configuration entities of a pipeline to its
  adapter with their Secret values revealed (`ConfigurationSecretSerializer`), and `GetPipelineConfigByCkTypeId@1` puts
  them into the pipeline's data. `AdapterService.CreatePipelineConfigurationAsync` is the single place that builds that
  list (deploy, register, leased pipeline, `GetAdapterConfigurationAsync` with and without `revealSecrets`). It removes
  every entity whose type `PlatformOnlyConfigurationTypes.IsPlatformOnly` names (today `System.Communication/SecretDefault`)
  and logs a warning naming tenant, pipeline, entity and type, never a value. `SecretDefault` is also not a Configuration
  type (it derives from `System/Entity`), so no `Uses` association can bring it in by itself; the exclusion makes that a
  rule instead of a property of the model. Test: `SecretDefaultExclusionTests`. A new platform-only type is one line in
  `PlatformOnlyConfigurationTypes` plus a case in that test.
- **In the mesh adapter**: `RevealSecret@1` and `GetPipelineConfigByCkTypeId@1` refuse the
  type (and the `SecretDefaults` record array of the Application) before they read, for every identity, and an
  architecture test lists every `Unprotect` call site against an allow-list. `PlatformOnlySecrets` there is the shared
  predicate. `GetPipelineConfigByCkTypeId@1` lives in the mesh adapter, not in this repository, so its test is there.
  The SDK node `GetPipelineConfigByWellKnownName@1` (octo-communication-sdk) reads the same list and needs no rule of
  its own: the list never contains a SecretDefault.
- The projection writes `SecretDefault` entities only through the child tenant context and must never
  add one to a configuration list. The platform's own consumer is the only reader.

## Secret defaults

An Application's `SecretDefaults` (System.Communication 4.15.0) are projected by the controller into the platform-owned
`System.Communication/SecretDefault` of every tenant the Application serves. Nothing else is written, so a
tenant's own secret of the same purpose (e.g. `RegistrationCaptchaSecret`) is never touched; the platform node that
consumes the default (`VerifyCaptcha@1`, mesh adapter) prefers the tenant's value and falls back to it.

- **The deploy is the release; a running release keeps its defaults (decision 3a, refined by decision 2 ii).**
  `IsSourceFor(application, sourceTenant, targetInventory)` decides per target tenant. Pending and Deployed are sources.
  **Error** (a deploy the guard refused, or a Helm rollout that failed) is a source only in a tenant where the Application
  still holds a default it projected: a refused or failed redeploy leaves the previous Helm release running
  (`helm upgrade --install --rollback-on-failure` rolls a failed upgrade back, a refusal never reaches Helm), and that
  release keeps its defaults. It keeps them **as stored** (`HoldReleasedDefaults`): its declarations were edited after
  that release and never deployed, so a reconcile neither removes a key the edit dropped nor writes a value the edit
  changed or a key it added, and an Application of the same depth (another one in the same source tenant) does not
  replace a held default; only a source closer to the root takes the key over. Known limit (follows from "as stored"):
  while the Application is in Error, a copy a tenant edited stays edited and a copy a tenant deleted is not restored;
  both come back with the next successful deploy. An Application refused before it was
  ever deployed holds none and offers nothing, and an
  Application in Error offers nothing to a tenant it did not serve before (e.g. a tenant created meanwhile): it gets the
  defaults with the next successful deploy. Never deployed, undeployed and disabled offer nothing. The stored projection
  is the signal because neither the Application (`DeploymentState`, `LastDeploymentError`) nor the operator's status
  report (`WorkloadDeploymentStatusDto`: success and message only) records whether a release is running; the error text
  is not parsed, so `LastDeploymentError` keeps the form the Studio's field marks read
  (`[tenant] Cannot deploy workload 'x': <sentence>`), and Applications refused before this rule need no
  migration. Known limit: a first install whose rollout failed was Pending when it projected, so it keeps its defaults
  although Helm removed the release, until it is undeployed or deployed successfully. A guard check that throws without
  recording (e.g. a missing chart name) leaves the previous state. **Undeploying an Application withdraws its
  defaults**, also one in Error: e.g. undeploying a customer app removes the CAPTCHA default from every community without its
  own key pair; the next deploy projects it again.
- **Triggers.** The controller writes SecretDefaults on five paths, all through
  `ISecretDefaultProvisioningService`, all best effort (a failure is an Error event, never a failed deploy, undeploy or tenant load):
  1. **Deploy** - `DeploymentSiteService.DeployWorkloadAsync` calls `ProvisionForSourceAsync(tenantId, applicationRtId)`
     after the Helm notification for **every** `RtApplication`, also one without entries, so a redeploy removes what it no
     longer declares. It writes the valid declarations first, then sweeps: in every tenant that holds a default this
     Application no longer provides, the target reconcile runs **before** the removal (see "Removal rule"). A value that
     cannot be revealed on this host (no key ring, strict mode against clear text) fails only its own key, with an Error
     event naming that cause; the other declarations and the sweep still run. Every Error event of the deploy (invalid entry, unreadable value, failed write) is best effort: an event store that throws stops neither the other keys nor the sweep (`SecretDefaultDeployEventFailureTests`). A tenant the entry still reaches keeps
     the value it holds; from a tenant the entry no longer reaches (e.g. left `AllowedTenantIds`) the sweep removes it,
     also while the value cannot be revealed. A failure of the whole projection (e.g. tenant tree unreadable) is an Error event on the Application; once the cause
     is fixed, a deploy or the reconcile endpoint repeats it (a redeploy alone does not help while the cause persists).
  2. **Deployment-site deploy** - `DeployDeploymentSiteAsync` deploys no workload (decoupled in f2f8bbd; the unused
     `DeployManagedWorkloadsAsync` fan-out was removed earlier), so every Application reaches Helm through
     `DeployWorkloadAsync` and its validation and projection; the re-dispatch of a Pending workload on site registration
     (`ReconcilePendingWorkloadsAsync`) calls it too. Tests: `DeployWorkloadSecretDefaultsTests`.
  3. **Tenant load** - `DefaultConfigurationCreatorService.EnsureSecretDefaultsAsync` runs `ProvisionForTargetAsync(tenantId)`
     next to the lent-pool mirrors (service start, Enable, `PosUpdateTenant`), so a tenant created or loaded after the deploy
     receives its defaults without a redeploy. It never fails the load. The Information event of the reconcile (below,
     "Target events") is stored by the service, not by the hook, so a load stores it once.
  4. **Undeploy** - `DeploymentSiteService.UndeployWorkloadAsync` calls `WithdrawForSourceAsync(tenantId, applicationRtId)`
     after the resting state is set: the source-side sweep of a redeploy without entries, over its own tenant and subtree.
     In every tenant that holds a withdrawn default the target reconcile runs first, so another deployed source takes the
     key over, and removes the withdrawn default after (also when that replacement fails). A failure is an Error event on
     the Application; the target reconcile removes the defaults on the next load.
  5. **Reconcile endpoint** - `POST {tenantId}/v1/secretDefaults/reconcile` (`SecretDefaultsController`, policy
     `TenantCommunicationApiReadWritePolicy` like `adapterPool/mirrors/refresh`) runs the target reconcile for the route
     tenant and every tenant of its subtree and answers with one `SecretDefaultTargetResult` per tenant: status
     (`Reconciled`, `TenantUnavailable`, `ModelMissing`, `TreeUnreadable`, `Failed` when the tenant's reconcile threw)
     and counts (created, updated, unchanged, removed, kept, unreadable sources, failed), never a value. Every tenant
     where it wrote or removed a default gets the same Information event as on tenant load; the controller log line sums
     created, updated, unchanged, removed and failed. Use it after deleting an Application, which reaches no hook.
- **Target events.** `ProvisionForTargetAsync`, the per-tenant reconcile behind the tenant load and the reconcile
  endpoint, stores one Information event `Secret defaults reconciled: <n> created, <n> updated, <n> removed, <n> kept.`
  in the reconciled tenant when it wrote or removed a default: counts only, no value, no key, no link (a tenant's
  reconcile can concern several Applications), never `StatusMessage`. A failing event store does not fail the reconcile.
  The hand-over of a withdrawal or redeploy sweep runs the same reconcile without this event: that change is reported by
  the source's own Information event on the Application (`withdrawn: n removed, n failed`), so an undeploy stores one
  event, not one per tenant. Tests: `SecretDefaultTargetEventTests`.
- **Target set.** The Application's own tenant plus its whole subtree, read through
  `ITenantLendingScopeResolver.ResolveLendableTenantsAsync(source, LendingScope.Descendants)` (the descendant walk with
  the ancestor guard). Never siblings. A non-empty `AllowedTenantIds` intersects that set; a listed tenant outside it is
  ignored, never granted.
- **Value.** Each `SecretValue` is revealed once in the source tenant (`IWorkloadEncryptionService.RevealOrNull`,
  attribute path `SecretDefaults.SecretValue`) and handed to the write as `RtSecretValue.Pending`; the engine encrypts it
  in the target. A redeploy overwrites (rotation). An unreadable value (empty, or a reveal that throws because the host
  has no key ring or strict mode refuses clear text) writes nothing for that key and is an Error event; it never stops
  the other keys, on the deploy or on the reconcile.
- **Supplying the value (blueprint-supplied value).** A blueprint seeds the Application **without** a
  `SecretDefaults` entry: the engine's secret seed lint allows only an empty or omitted Secret in a seed (a value, a
  `<...>` / `TODO_SET_*` placeholder and a `${...}` variable are all refused), blueprints have no install-time
  parameters, and an entry without `SecretValue` would make the deploy guard below refuse the Application. An operator
  adds the entry after the install through the asset repository: Studio (Application -> Further attributes ->
  `SecretDefaults`: `Key`, `SecretValue`, optional `AllowedTenantIds` and `Description` -> Save -> Deploy), or
  `systemCommunicationApplications.update` with the whole `secretDefaults` list and the value passed as a GraphQL
  variable, never inline; or a CD that reads it from Vault (a solution on a test environment). A re-apply or blueprint update keeps it
  (`SecretDefaults` is TenantOwned, a Secret record member is carried over by record key). Never as a `ValueOverride`,
  `ValuesYaml` or Helm value. Since `SecretDefault.Value` is Hidden (4.16.0), a projected entity in the target tenant is
  the "set" signal. Worked sample with a tenant override and `VerifyCaptcha@1`: `samples/Blueprints/SecretDefaultDemo/`
  (README + `verify/`).
- **Write.** `ICommunicationRepository.UpsertSecretDefaultAsync(targetTenantId, SecretDefaultWrite)` matches on
  `RtWellKnownName` = key (the name the consumer reads), sets `Name`, `Value`, `SourceTenantId`, `SourceWorkloadRtId` and
  `LastProjectedAt`, and erases further SecretDefaults with the same well-known name so the consumer finds exactly one. A
  tenant that cannot be resolved (`TenantUnavailable`) or lacks System.Communication 4.15.0 (`ModelMissing`) is
  skipped with a Warning event in the source tenant naming tenant and key; the other tenants are written.
  `SecretDefaultWrite` is a class, not a record, so no generated `ToString` renders the value.
- **Precedence: the source closer to the root wins.** Each write carries the ancestors of its source tenant
  (`ITenantLendingScopeResolver.ResolveAncestorTenantsAsync`, the resolver's own parent map) as
  `ProtectedSourceTenantIds`. That call throws when the tenant tree could not be read completely (a partial parent map
  would drop ancestors), which stops the projection before any write; lending keeps using the partial map. Incomplete
  means: a read failed, a tenant of the walk cannot be resolved (its children are unknown), a child's registry entry
  names another parent than the tenant listing it, a child is listed by two parents, or a root names a walked parent that
  does not list it. An inconsistent map is cached like a complete one (30 s), a failed read is retried at once. A tenant
  that is still not in the tree after a rebuild (other than the system tenant) is refused the same way. An existing SecretDefault whose `SourceTenantId` is one of them is kept
  (`KeptAncestorSource`), and a Warning event in the deploying tenant names the target tenant, the key, the kept source and
  the refused source. The same source (redeploy, rotation) or a descendant source overwrites; so does an unrelated source
  (last deploy wins). Example: P projects `k`, then C1 deploys `k` -> C1 and its subtree keep P's value; C1 projects first,
  then P deploys -> P overwrites.
- **Deploy guard.** `SecretDefaultDeclarationValidator` runs in `EnsureWorkloadIsHelmDeployableAsync`, before anything
  reaches the operator, and records the rejection on the workload: empty or null key, key not matching
  `^[A-Za-z0-9][A-Za-z0-9._-]*$` (at most 128 characters), a key declared twice, or an entry without `SecretValue` (an
  export/import drops it). Each message names the Application and the key, never the value.
- **The service validates on its own.** `ProvisionForSourceAsync` runs the same checks (`SecretDefaultDeclarationValidator.Check`)
  on the entries it reads, because it is also called outside the deploy. It skips every invalid entry, all entries of
  a duplicated key included, with an Error event, and writes the valid ones. The key is read raw
  (`GetAttributeValueOrDefault`): the generated `Key` getter throws on a null value.
- **Platform-only.** SecretDefaults never reach the `WorkloadDeployedDto`: no Helm value, no `ValuesYaml`, no
  `{release}-octo-secrets`. The plaintext appears in no log line, event, exception message or workload status
  (`ProvisionForSourceAsyncTests.ThePlaintext_ReachesNoLogLineAndNoEvent`, `DeployWorkloadSecretDefaultsTests`). See
  also "Platform-only secrets" above: a SecretDefault is never part of an adapter's configuration list.
- **Target reconcile.** `ProvisionForTargetAsync(target)` brings one tenant to the desired state. Sources are the
  tenant itself and all its ancestors (`ResolveAncestorTenantsAsync`, complete tree required; never siblings); every
  Application of a source whose valid entry reaches the target (`AllowedTenantIds` empty or naming it) is an offer. Per key
  the offer of the source closest to the root wins (same precedence as the deploy); between Applications of one tenant the
  one that already holds the default stays, else the lowest RtId. **Write only on change:** the winner is written when the
  tenant does not hold exactly it already (one entity for the key, same source tenant and Application, `Name` = key, same
  value), so an edited or deleted SecretDefault gets the projected value back, while an unchanged tenant load writes
  nothing (`DefaultsUnchanged`; `LastProjectedAt` is the time of the last real write). A tenant's delete through the
  generic `runtimeEntities.delete` only archives the entity, which every typed read then skips; `UpsertSecretDefaultAsync`
  erases such a tombstone under the key's RtId and inserts the default again (`Created`), so it is restored rather than
  an archived document being updated forever (`SecretDefaultArchivedRestoreTests`). The value check reveals the stored
  value in the target (`RevealOrNull`, attribute `Value`) and compares it with the source value in constant time; neither
  is logged or kept, and an unreadable stored value counts as changed. **Write before remove:** the winners are written
  first, then the unprovided defaults are removed, so a holder change replaces the default in place and the consumer never
  sees a gap. **Revocation wins over keeping the old value:** when the winner's write fails (or its value cannot be
  read), a default whose source still reaches the tenant (source tenant still the tenant or an ancestor, Application
  still a source, entry still allowing the tenant, an invalid entry included) is provided and keeps its old value; a default whose source no longer
  reaches it is removed anyway, and the tenant has no default for that key until the replacement can be written
  (`SecretDefaultRevocationTests`). The write names the defaults it found unprovided (`SecretDefaultWrite.SupersededDefaults`); such a default is
  replaced even when its source is protected, as long as it is unchanged since the read. A source tenant that cannot be
  resolved or lacks System.Communication 4.15.0 is unreadable: its defaults are kept (Warning event; for a missing model
  only when the target holds defaults of it, because a root without Communication would otherwise warn on every load). The
  ancestor walk re-reads the tenant tree when the tenant is not in the cached parent map, so a tenant created seconds
  ago finds its ancestors.
- **Removal rule (desired state).** A SecretDefault is deleted (Erase) when no Application provides it to that tenant any
  more: its entry was removed, the Application was deleted or is not deployed (see above), the tenant left the entry's
  `AllowedTenantIds`, or its source is no ancestor any more. Matching is by `SourceTenantId` + `SourceWorkloadRtId` + key,
  so a default of another Application stays. A default without `SourceTenantId` or `SourceWorkloadRtId` was not written by
  a projection (or lost them on an import) and is always removed, also from a tenant whose sources cannot be read.
  **Conditional removal:** `DeleteSecretDefaultIfUnchangedAsync` re-reads the entity in the delete transaction and erases
  it only while RtId, well-known name, `SourceTenantId`, `SourceWorkloadRtId`, `LastProjectedAt` and `RtVersion` (raised
  by every write, so a rotation within the same tick is seen too) match what was read;
  a default a concurrent deploy wrote meanwhile is kept (`Changed`, a write conflict counts as `Changed`), a gone one is
  `NotFound`, and one the tenant archived since the read is erased and also `NotFound`; neither is a failure. The target reconcile and the source sweep both use it. The removal pass of a target reconcile skips every key its own write pass wrote (created or updated): that write already replaced or erased each entity of the key, so a default whose source fields a tenant edited is restored and not also tried for removal. An entry that is present but invalid (no value, duplicated key, ...) is never written, and its `AllowedTenantIds` still apply, with no exception for invalid entries: a tenant it allows keeps the default it holds, a tenant it excludes loses it. Of duplicates of one key a tenant keeps the default only while every duplicate allows it, so a tenant any of them excludes loses it (revocation wins when they disagree; `SecretDefaultInvalidEntryRevocationTests`). On deploy the
  source side sweeps its own tenant and subtree for defaults of this Application that it no longer provides; in every
  tenant that holds one it runs the target reconcile, which writes another source's winner first and removes the withdrawn
  default after, so the key has no gap; a failed replacement does not keep the withdrawn default (revocation wins). Only when that reconcile does not
  end `Reconciled` (tree unreadable, exception) does the sweep remove the withdrawn defaults itself (conditionally). The
  counts of the sweep (its Information event on the Application, `SecretDefaultProvisioningResult`) are this
  Application's own: removed = its withdrawn defaults the reconcile removed, failed = its withdrawn defaults it had to leave
  (failed removal, unreadable source); what the reconcile changes for other sources in that tenant is not
  counted. Integration test of the `RtVersion` read: `SecretDefaultRepositoryTests` (a same-tick rotation is `Changed`
  for the stale read and removable after a new read). **Unknown, never empty:** a source
  tenant that cannot be read keeps the defaults it projected and is named in a Warning event in the target; a tenant tree
  that cannot be read completely changes nothing (`TreeUnreadable`, Warning event).
- **Exactly one per name under concurrency.** A new SecretDefault is created with an RtId derived from its key
  (`SecretDefaultIdentity.RtIdFor`, SHA-256 of a namespace plus the key). Two writers creating the same key at the same
  time insert the same document id; MongoDB refuses one (write conflict or duplicate key), and `UpsertSecretDefaultAsync`
  repeats it (at most 3 attempts) as an update. An entity found by that RtId but renamed by the tenant is taken over.
  `VerifyCaptcha@1` refuses duplicates, which is why this matters.
- **Visibility (decision: events).** Events carry tenant ids, keys, Application names and counts only: per-target Warning
  (skipped, kept, unreadable source), Error (write or delete failed, unreadable value) and an Information summary after a
  deploy or an undeploy that wrote or removed something. Every event about an Application's default is **linked to the
  Application** (`associatedRtEntityId`, the Related association, so `relatesFrom(System.Notification/Event)` on the
  Application finds it) when it is stored in the Application's own tenant: the deploy and withdrawal summaries, invalid
  entries, skipped targets, and write or removal failures in the source tenant. An event in another tenant cannot relate
  to it and names the Application and its tenant in the text instead. Events about the tree or a source tenant as a whole
  link nothing. Application `StatusMessage` is not used: it is the live deployment status line that every operator report
  overwrites.
- Tests: `tests/CommunicationControllerService.Tests/Services/SecretDefaultProvisioningServiceTests/` (target side:
  `ProvisionForTargetAsyncTests`, `ProvisionForTargetWriteTests`, `ProvisionForTargetIncompleteTreeTests`, deployed
  sources and undeploy: `SecretDefaultReleasedSourceTests`, concurrent removal: `SecretDefaultConcurrentRemovalTests`, revocation on a failed replacement:
  `SecretDefaultRevocationTests`, values that cannot be revealed: `SecretDefaultUnrevealableOfferTests`,
  removal on deploy: `ProvisionForSourceRemovalTests`), `TenantLendingScopeResolverTests`, `Repository/StoredSecretDefaultTests.cs`,
  `DeploymentSiteServiceTests/DeployWorkloadSecretDefaultsTests.cs`,
  `DefaultConfigurationCreatorServiceTests/EnsureSecretDefaultsAsyncTests.cs`, `Controllers/SecretDefaultsControllerTests.cs`,
  `Repository/SecretDefaultIdentityTests.cs`, integration `Repository/SecretDefaultRepositoryTests.cs` (encryption,
  rotation in place, another secret of the tenant unchanged, duplicate erase, a protected source kept, a superseded
  protected default replaced and a re-projected one kept, two concurrent creates leave one entity (forced overlap through
  the internal `CommunicationRepository.AfterSecretDefaultUpsertReadAsync` hook), list, conditional delete).
