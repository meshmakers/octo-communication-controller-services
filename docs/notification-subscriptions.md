---
description: "Notification subscriptions: schedule, first run, cron and the notification engine"
applies_to: **/Notification*.cs
---

### Notification Subscriptions — schedule and engine

A `System.Notification/NotificationSubscription` (a SimpleRtQuery + a template, `Enabled` = Deployed) has one
`NotificationPreference` per recipient (cron, time zone, Onwards / Catch up). This service owns WHEN; the
mesh adapter's engine pipeline decides WHAT (see octo-mesh-adapter `AGENTS.md`, `PrepareNotification@1`).

| Route | Policy | Behaviour |
|---|---|---|
| `POST {tenantId}/v1/notifications/subscriptions/{rtId}/deploy` | ReadWrite | `Enabled=true` and `DeployedAt=now` (undeploy → deploy is a new start for Onwards; Catch up reads on from its watermark), only this subscription's preferences rescheduled, engine DataFlow deployed if it is not, then one immediate tick per Onwards preference that needs a first run. On an already deployed subscription (a retry, CLI, MCP) `DeployedAt` is kept, so nothing starts over; the rest still runs, which is what finishes a deploy whose engine step failed. Each call writes a "deployed" event. |
| `POST {tenantId}/v1/notifications/subscriptions/{rtId}/undeploy` | ReadWrite | `Enabled=false`, this subscription's recurring sends cancelled; preferences and delivery states are kept. Writes an "undeployed" event. |
| `POST {tenantId}/v1/notifications/schedule/sync[?preferenceRtId=]` | ReadWrite | With `preferenceRtId`: schedule that preference again if its subscription is deployed, otherwise cancel its ticks (immediate tick when it is an Onwards preference that needs a first run; Catch up waits for its schedule); Studio calls this after a preference is saved or deleted. Without it: full rebuild, the orphaned-state sweep, plus an immediate tick for every Onwards preference that needs a first run — the call to make after a subscription's query changed. |

Error codes (`NotificationScheduleServiceException.Kind`): `SubscriptionNotFound` → 404; `EngineUnavailable`
(pipeline missing or disabled, DataFlow deploy failed) → 409; `ScheduleIncomplete` (some preferences could not
be scheduled; the rest are) → 503 plus one error event naming them (the tenant-start wrapper does not store a second one); anything else → 500.

- `NotificationScheduleService.UpdateScheduleAsync` mirrors `TriggerManagementService`: remove the group
  `notification-{tenant}`, then one `ScheduleRecurringSendAsync(NotificationScheduleTick)` per preference of a
  deployed subscription, `TimeZoneId` = the preference's zone (UTC when unset or unknown, with a warning),
  misfire policy Skip. One preference failing does not stop the others. Runs on tenant start; `StopTenantAsync`
  removes the group. A failure there is logged and stored as an error event and never stops the tenant
  (`DefaultConfigurationCreatorService.RunNotificationScheduleStepAsync`). A preference with an empty cron, or
  one that fires more than once a minute (`NotificationCron`: the seconds field of a six- or seven-field cron
  must be a single value), is skipped with a warning; the Studio enforces the same limit, but an API write
  must not get 86 400 ticks a day.
- **The immediate tick records an Onwards start early, and only Onwards gets it.** The engine anchors an Onwards
  start at the moment that caused it (the deployment, the preference change, the subscription change;
  `NotificationDigest.Plan` in octo-mesh-adapter), not at the run, so a late or lost tick loses no row: the next
  scheduled run mails what changed since. The tick only writes the start at once instead of at the first scheduled
  time. A Catch up preference gets no immediate tick: otherwise a recipient of a 07:00 digest received the backlog the moment the preference was saved or the
  subscription deployed (`NotificationPreferenceSchedule.TakesBaseline`, from `SkipExistingRows`). The tick
  goes to the same queue as the recurring ones; a failed send only logs a warning, the next scheduled tick
  covers it.
- **"Needs a first run" is the engine's restart rule, mirrored** (`NotificationFirstRun.IsDue`, a copy of
  `NotificationDigest.Plan` in octo-mesh-adapter): no state or no run yet, for Onwards a run before
  `max(preference created, DeployedAt)` (Catch up reads on from its watermark after a redeploy, so the rows of
  the pause are mailed and the older ones are not mailed again), a run for another recipient (`SubjectId`), another query
  (`QueryRtId` against the subscription's `SubscriptionQuery` target), another start option
  (`SkipExistingRows`), or an Onwards state without `ProcessedUntil`. "Never ran" alone was not enough: a
  recipient changed on Tuesday under a Monday cron would get no immediate tick. Change this together with `Plan`.
- **A per-preference sync never cancels a tick it is about to schedule again.** Cancel and schedule are both
  fire-and-forget messages to the Hangfire scheduler queue, which is consumed concurrently, so a cancel
  processed after the add removed the job for good while the API answered 204. Scheduling under the same
  schedule id replaces the job (`AddOrUpdate`), so the tick is only cancelled when the preference has no
  schedule any more, and the co-wake only when the engine adapter is not OnDemand. The full rebuild is
  unaffected: its group removal is a request/response command, answered before anything is scheduled.
- **Co-wake.** When the engine adapter's `LifecycleMode` is OnDemand, every preference gets a companion
  recurring `LifecycleWakeMessage` (`{pref}-notification-wake-{tenant}`) on the same cron, as cron triggers do.
  The immediate ticks get one `LifecycleWakeMessage` of their own, sent before them to the same durable wake queue:
  a hibernated engine would otherwise consume them only at its next scheduled wake. A failed wake only logs a
  warning.
- Ticks go to `queue:{NotificationScheduleChannelName}-{tenant}-{NotificationEngine.PipelineRtId}`; the adapter
  trigger listens on the same name, so the engine pipeline's rtId is a cross-repo contract.
- The engine is the service-managed blueprint `System.Communication.Notifications` (DataFlow
  `670000000000000000002580`, Pipeline `670000000000000000002581` on the default adapter). It is registered in
  `Program.cs` AFTER Release / MainLatest because its `Executes` edge needs the adapter they seed — the apply
  loop runs in registration order. It ships undeployed; subscription Deploy deploys it
  (`NotificationScheduleServiceException.EngineMissing` / `EngineDeployFailed` otherwise). The blueprint requires
  `System.Notification-[2.6.0,3.0)`: the engine writes the delivery state's `ProcessedUntil` and `SentRows`, which 2.6.0 introduced.
- **Delivery states without a recipient are erased** on every full rebuild — tenant start and a sync
  without `preferenceRtId` — never on a per-preference sync
  (`NotificationRepository.DeleteOrphanedDeliveryStatesAsync`: a state whose `DeliveryStatePreference` target is
  gone). The Studio deletes the state with its preference, but an API delete, an import or a failed second write
  leaves one behind, the way Kubernetes garbage-collects objects whose owner is gone. The sweep reads every
  state in full, `SentRows` included (the repository API has no projection), which is affordable once per
  rebuild and was not on every Save click. A failure is logged and never stops the schedule.
- SMTP follows the EnergyCommunity pattern: the pipeline names `serverConfiguration: EmailSender`, and an
  operator associates (`Uses`) the tenant's EMailSenderConfiguration of that name, then redeploys the DataFlow.
- **Not routed through leases (yet).** Ticks go straight to the engine pipeline's queue, the way manual
  adapters receive them. If an operator switches the default adapter to `LifecycleMode=Leased`,
  it has no process of its own and the ticks are not consumed - `TriggerManagementService` routes cron
  triggers of leased adapters through the lease queue, the notification schedule does not.

Tests: `Services/NotificationScheduleServiceTests.cs`, `Repository/NotificationFirstRunTests.cs`,
`Controllers/NotificationsControllerTests.cs`, integration `Repository/NotificationRepositoryTests.cs` (including
the orphan sweep and the first-run rule against stored states). The integration fixture imports System.Notification
after System.Communication (`InitializeTenantForTestingAsync`).
