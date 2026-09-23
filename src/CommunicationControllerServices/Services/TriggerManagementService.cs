using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService(
    ILogger<TriggerManagementService> logger,
    ICommunicationRepository communicationRepository,
    ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
    IRoutedCommandClient<ExecutePipelineRequest> executeMeshPipelineCommandClient,
    IDistributionEventHubService distributionEventHubService,
    ICommunicationEventService eventService,
    IWorkloadLifecycleService workloadLifecycleService,
    ILifecycleConfigurationService lifecycleConfigurationService,
    ILeaseSchedulerWakeSignal leaseSchedulerWakeSignal,
    IWorkloadEncryptionService encryptionService)
    : ITriggerManagementService
{
    public async Task<PipelineExecutionDataDto> StartExecutePipelineAsync(string tenantId,
        OctoObjectId pipelineRtId, string? pipelineInput, bool isDryRun = false,
        ExecutePipelineCaller? caller = null, string? callerAccessToken = null)
    {
        logger.LogInformation("[{TenantId}] Executing pipeline '{PipelineRtId}' (dry-run={IsDryRun})",
            tenantId, pipelineRtId, isDryRun);

        // AB#4924 §9.1 — a Leased adapter has no process of its own, so there is nothing to send the
        // execute command to. The work item is ENQUEUED instead and the scheduler starts it when a
        // pool member is leased to this tenant. Everything below this branch is the manual-adapter
        // path and is unchanged: a manual adapter has no queue and executes immediately, and that
        // asymmetry is intended (concept §5, "One queue per pool").
        var queued = await TryEnqueueForLeasedAdapterAsync(tenantId, pipelineRtId, pipelineInput, isDryRun,
            caller, callerAccessToken);
        if (queued is not null)
        {
            return queued;
        }

        // AB#4918 wake gate — MUST complete before the send below: the execute-pipeline queue is
        // non-durable/auto-delete, so publishing while the adapter is scaled to 0 silently drops
        // the message. No-op unless the tenant has scale-to-zero on and the adapter is OnDemand.
        await workloadLifecycleService.EnsureWorkloadRunningForPipelineAsync(tenantId, pipelineRtId);

        ExecutePipelineResponse? r;
        try
        {
            // The FromExecutePipelineCommandNode listens on a queue keyed by the PIPELINE rtId.
            // (It used to be keyed by DataFlowRtId, which collided when a DataFlow held more than
            // one FromExecutePipelineCommand pipeline — only the first one could register its
            // endpoint.) Must stay in sync with FromExecutePipelineCommandNode.StartAsync.
            var address =
                $"{PipelineQueueNames.ExecutePipelineCommand.ToLower()}-{tenantId.ToLower()}-pipeline-{pipelineRtId.ToString().ToLower()}";

            r = await executeMeshPipelineCommandClient.GetResponse<ExecutePipelineResponse>(address,
                // AB#5126: carry the invoker through so a FromExecutePipelineCommand pipeline can run
                // as them. Token-free principal on Caller; the raw token travels separately and never
                // reaches the pipeline data root.
                new ExecutePipelineRequest(tenantId, pipelineInput)
                {
                    IsDryRun = isDryRun,
                    Caller = caller,
                    CallerAccessToken = callerAccessToken
                });

            if (r.IsSuccessStartingExecution)
            {
                if (r is { PipelineExecutionId: not null, ExecutionStartTime: not null })
                {
                    logger.LogInformation(
                        "[{TenantId}] Start execution of pipeline '{PipelineRtId}' (ExecutionId {PipelineExecutionId}) successful",
                        tenantId, pipelineRtId, r.PipelineExecutionId);

                    await eventService.StoreInformationEventAsync(tenantId,
                        $"Pipeline '{pipelineRtId}' execution started (ExecutionId: {r.PipelineExecutionId}).");

                    return new PipelineExecutionDataDto
                               { Id = r.PipelineExecutionId.Value, DateTime = r.ExecutionStartTime.Value } ??
                           throw TriggerManagementServiceException.ExecutePipelineExecutionIdNull(tenantId,
                               pipelineRtId);
                }

                throw TriggerManagementServiceException.ExecutePipelineExecutionIdNull(tenantId,
                    pipelineRtId);
            }
        }
        catch (Exception e)
        {
            await eventService.StoreErrorEventAsync(tenantId,
                $"Pipeline '{pipelineRtId}' execution failed: {e.Message}");
            throw TriggerManagementServiceException.ExecutePipelineExecutionErrorFailed(tenantId, pipelineRtId, e);
        }

        await eventService.StoreErrorEventAsync(tenantId,
            $"Pipeline '{pipelineRtId}' execution failed: {r.ErrorMessage}");
        logger.LogError("[{TenantId}] Execution of pipeline '{PipelineRtId}' failed: {ErrorMessage}"
            , tenantId, pipelineRtId, r.ErrorMessage);
        throw TriggerManagementServiceException.ExecutePipelineFailed(tenantId, pipelineRtId, r.ErrorMessage);
    }

    /// <summary>
    ///     Enqueues the work item when the pipeline's adapter borrows its process from a pool, or
    ///     returns null when it does not (AB#4924 §9.1).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         🔴 <b>The execution entity is created here, in <c>Queued</c>, before anything runs.</b>
    ///         That is what makes one work item one <c>PipelineExecution</c> from enqueue to
    ///         completion (concept §5) — the three surfaces show a continuous history instead of
    ///         joining a queue to an execution log.
    ///     </para>
    ///     <para>
    ///         A dry run is deliberately <b>not</b> queued. A dry run is a synchronous answer to a
    ///         caller holding the request open; parking it behind a rotation would turn "validate
    ///         this pipeline" into something that returns minutes later, and the leased adapter has
    ///         no process to answer it from anyway. It falls through to the normal path, which fails
    ///         it with the established "no adapter" error rather than pretending to have run.
    ///     </para>
    /// </remarks>
    private async Task<PipelineExecutionDataDto?> TryEnqueueForLeasedAdapterAsync(string tenantId,
        OctoObjectId pipelineRtId, string? pipelineInput, bool isDryRun, ExecutePipelineCaller? caller,
        string? callerAccessToken)
    {
        if (isDryRun)
        {
            return null;
        }

        RtAdapter? adapter;
        try
        {
            adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
                new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId));
        }
        catch (Exception e)
        {
            // Let the established path produce its own error for an unreadable pipeline rather than
            // inventing a second one here.
            logger.LogWarning(e,
                "[{TenantId}] Could not resolve the adapter of pipeline '{PipelineRtId}' while checking whether it " +
                "is leased",
                tenantId, pipelineRtId);
            return null;
        }

        if (adapter is null || adapter.LifecycleMode != RtLifecycleModeEnum.Leased)
        {
            return null;
        }

        // AB#5271: the lending pool is the adapter's LentFrom mirror, not two attributes on the
        // adapter. Resolved once here and used for the metric tags below.
        var lentFrom = LentFromReference.FromMirror(
            await communicationRepository.GetLentAdapterPoolForAdapterAsync(tenantId, adapter.RtId));

        // 🔴 AB#5329 — a borrower that names no pool is refused HERE, and the comment this replaced
        // said the opposite ("the lease itself will refuse it with a named reason"). It does not:
        // LeaseSchedulerService.GetTopologyAsync drops such an adapter from the topology with a
        // warning, so nothing downstream ever reaches GrantLeaseAsync and no refusal is ever
        // produced. Measured on 23.09.2026: the execute call answered 200, the execution sat in
        // Queued with errorMessage null indefinitely, and CancelQueuedExecution could not reach it
        // either — that verb is keyed by pool, and an adapter with no mirror belongs to none. The
        // only signal was a controller warning repeated on every scheduling round.
        //
        // Writing nothing is the same stance as the kill switch below: no execution entity, no
        // QueuedAt, no event that looks like progress — and the caller learns at once.
        if (lentFrom is null)
        {
            AdapterLeasingMetrics.RecordRefused(tenantId, string.Empty, string.Empty, LeaseStage.Enqueue,
                LeaseRefusalReason.BorrowerNamesNoPool);

            logger.LogWarning(
                "[{TenantId}] Pipeline '{PipelineRtId}' is executed by leased adapter '{AdapterName}', which names " +
                "no usable adapter pool (no LentFrom mirror, or its Lender record is incomplete); nothing was queued",
                tenantId, pipelineRtId, adapter.Name);
            await eventService.StoreErrorEventAsync(tenantId,
                $"Pipeline '{pipelineRtId}' was NOT queued: its leased adapter names no usable adapter pool. " +
                "Link the adapter to a lent adapter pool (Studio → Adapter → Lent adapter pool), or refresh the " +
                "mirrors with POST {tenantId}/v1/adapterPool/mirrors/refresh.");
            throw TriggerManagementServiceException.LeasedAdapterNamesNoPool(tenantId, pipelineRtId,
                adapter.Name ?? adapter.RtId.ToString());
        }

        // 🔴 AB#4924 §14 — the per-tenant kill switch, enqueue half. Nothing is written: no execution
        // entity, no QueuedAt, no event that looks like progress. The other half sits in
        // LeaseService.GrantLeaseAsync, and both are needed — a switch that stopped only new enqueues
        // would leave the existing queue draining after somebody turned leasing off, which is not what
        // an operator means by "off"; a switch that stopped only grants would keep growing a queue
        // nobody is going to serve.
        if (!await lifecycleConfigurationService.IsLeasingEnabledAsync(tenantId))
        {
            // 🔴 AB#4924 increment 9 (plan §11). Counted at the ENQUEUE stage and with the
            // borrowing half named, because "leasing is off" is two different operator decisions on
            // two different tenants and an aggregate cannot tell them apart. This half is always
            // the borrower's own switch — the lender's is checked at grant, in LeaseService.
            AdapterLeasingMetrics.RecordRefused(tenantId, lentFrom?.LenderTenantId ?? string.Empty,
                lentFrom?.AdapterPoolRtId ?? string.Empty, LeaseStage.Enqueue,
                LeaseRefusalReason.LeasingDisabledBorrower);

            logger.LogWarning(
                "[{TenantId}] Pipeline '{PipelineRtId}' is executed by leased adapter '{AdapterName}', but adapter " +
                "pool leasing is disabled for this tenant; nothing was queued",
                tenantId, pipelineRtId, adapter.Name);
            await eventService.StoreErrorEventAsync(tenantId,
                $"Pipeline '{pipelineRtId}' was NOT queued: adapter pool leasing is disabled for this tenant.");
            throw TriggerManagementServiceException.LeasingDisabled(tenantId, pipelineRtId,
                adapter.Name ?? adapter.RtId.ToString());
        }

        var executionId = Guid.NewGuid();
        var queuedAt = DateTime.UtcNow;

        var execution = new RtPipelineExecution
        {
            RtId = OctoObjectId.GenerateNewId(),
            ExecutionId = executionId.ToString(),
            TriggerType = RtPipelineTriggerTypeEnum.Manual,
            InputData = pipelineInput
        };

        // AB#5279: the invoker survives the queue on the entity, exactly like the input. On a
        // dedicated adapter it rides the execute command and never touches the database; here the
        // work item waits in another request's lifetime, so the entity is the only carrier. The
        // token is stored encrypted or not at all — a plaintext bearer at rest is not an option, and
        // a run without the token is a run as the caller without delegation, which nodes already
        // cope with.
        QueuedCaller.Apply(execution, caller, EncryptCallerAccessTokenOrNull(tenantId, pipelineRtId, callerAccessToken));

        await communicationRepository.EnqueueExecutionAsync(tenantId, execution,
            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, pipelineRtId),
            new RtEntityId(adapter.CkTypeId ?? SystemCommunicationCkIds.RtCkAdapterTypeId, adapter.RtId),
            queuedAt);

        // The queue's in-rate. Against octo.lease.granted.count (the out-rate) this is the only
        // honest answer to "is the queue growing or draining"; a depth gauge alone shows the level
        // but not which way it is moving. Re-queued attempts are counted separately on
        // octo.lease.requeued.count, so the full in-rate is the sum of the two.
        AdapterLeasingMetrics.RecordEnqueued(tenantId, lentFrom?.LenderTenantId ?? string.Empty,
            lentFrom?.AdapterPoolRtId ?? string.Empty);

        // 🔴 AB#4924 §9.6 — the half that decides whether Interactive means anything. Work has just
        // arrived, and a member of the pool may be idle ALREADY: without this the execution waits for
        // the scheduler's next tick before anything starts, so a Studio Execute pays up to a full
        // interval while nobody is busy. The release-side wake cannot cover it — nothing was released.
        //
        // After the enqueue, never before: a round that ran between the two would not see this item
        // and the wake would be spent on nothing.
        leaseSchedulerWakeSignal.RequestRound(
            $"execution '{executionId}' of tenant '{tenantId}' enqueued for a leased adapter");

        logger.LogInformation(
            "[{TenantId}] Pipeline '{PipelineRtId}' is executed by leased adapter '{AdapterName}'; queued as " +
            "execution '{ExecutionId}'",
            tenantId, pipelineRtId, adapter.Name, executionId);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Pipeline '{pipelineRtId}' was queued for an adapter pool lease (ExecutionId: {executionId}).");

        // The DateTime a caller gets back is the QUEUE time, not a start time — the execution has
        // not started and StartedAt is deliberately unset (concept §8, Q5).
        return new PipelineExecutionDataDto { Id = executionId, DateTime = queuedAt };
    }

    private string? EncryptCallerAccessTokenOrNull(string tenantId, OctoObjectId pipelineRtId, string? callerAccessToken)
    {
        if (string.IsNullOrEmpty(callerAccessToken))
        {
            return null;
        }

        try
        {
            return encryptionService.Encrypt(callerAccessToken);
        }
        catch (InvalidOperationException e)
        {
            // No instance key configured. Loud, because the run will visibly lack delegation, but
            // not fatal: the principal still travels, and the queue must not stop over a missing
            // key on a cluster that never configured one.
            logger.LogWarning(e,
                "[{TenantId}] The invoker's token for queued pipeline '{PipelineRtId}' was NOT kept: the controller " +
                "has no encryption key (secrets.communicationInstanceSecretKey). The lease will carry the caller " +
                "without delegation.",
                tenantId, pipelineRtId);
            return null;
        }
    }

    public async Task RemoveScheduleAsync(string tenantId)
    {
        logger.LogInformation("[{TenantId}] Removing triggers of tenant", tenantId);

        try
        {
            var r = await communicationRepository.GetTriggersAsync(tenantId);
            foreach (var rtPipelineTrigger in r)
            {
                await communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId,
                    rtPipelineTrigger.RtId, RtDeploymentStateEnum.Pending);
            }

            var scheduleGroup = CreateScheduleGroup(tenantId);
            await removeRecurringJobsByScheduleGroupCommandClient.GetResponseWithRetry<GenericCommandResponse>(
                new RemoveRecurringJobsByScheduleGroupRequest(scheduleGroup));

            logger.LogInformation("[{TenantId}] Removal of triggers completed", tenantId);
        }
        catch (Exception e)
        {
            throw TriggerManagementServiceException.RemoveScheduleFailed(tenantId, e);
        }
    }

    public async Task UpdateScheduleAsync(string tenantId)
    {
        logger.LogInformation("[{TenantId}] Loading triggers", tenantId);

        var scheduleGroup = CreateScheduleGroup(tenantId);
        await RemoveScheduleAsync(tenantId);

        try
        {
            var pipelineTriggerKeyValues = await communicationRepository.GetTriggersAndPipelinesAsync(tenantId);

            foreach (var pipelineTriggerKeyValue in pipelineTriggerKeyValues)
            {
                var pipelineTrigger = pipelineTriggerKeyValue.Key;
                try
                {
                    if (pipelineTriggerKeyValue.Value.Count == 0)
                    {
                        logger.LogError(
                            "[{TenantId}] Trigger '{TriggerRtId}' has no associated pipelines and cannot be deployed",
                            tenantId, pipelineTrigger.RtId);
                        await eventService.StoreErrorEventAsync(tenantId,
                            $"Trigger '{pipelineTrigger.RtId}' has no associated pipelines and cannot be deployed.");
                        await communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId,
                            pipelineTrigger.RtId, RtDeploymentStateEnum.Error);
                        continue;
                    }

                    foreach (var meshPipeline in pipelineTriggerKeyValue.Value)
                    {
                        // Resolved first because it decides WHERE the cron tick goes (AB#5278):
                        // a dedicated adapter consumes its own per-pipeline trigger queue, a Leased
                        // adapter has no process to consume anything and gets its ticks through the
                        // controller instead.
                        var executingAdapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
                            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, meshPipeline.RtId));

                        if (executingAdapter?.LifecycleMode == RtLifecycleModeEnum.Leased)
                        {
                            // AB#5278: a Leased adapter has no process of its own, so the per-pipeline
                            // trigger queue below would have no consumer — and a pool member that
                            // holds a lease at the moment the cron fires MUST NOT consume it either,
                            // because a run started that way bypasses the lease bookkeeping (no
                            // queued execution, no queue position, no lease span — runbook §11.6).
                            // The tick therefore goes to the controller's own durable queue, whose
                            // consumer turns it into a queued work item exactly as an explicit
                            // ExecutePipeline does. Same cron, same schedule group, so it is added and
                            // removed together with every other schedule of this tenant. No adapter
                            // queue schedule and no co-wake: there is nothing to buffer for and
                            // nothing to wake. Decided at registration time like the co-wake — a
                            // LifecycleMode change takes effect with the next trigger deploy; the
                            // consumer re-resolves the adapter at tick time, so a tick that outlives
                            // the change simply runs the pipeline the way its current mode requires.
                            var leaseAddress = $"queue:{PipelineQueueNames.LeaseTriggerQueue.ToLower()}";
                            var leaseTriggerOptions = new RecurringSchedulingOptions(
                                pipelineTrigger.CronExpression,
                                DateTime.Now, null,
                                $"{pipelineTrigger.RtId.ToString()}-lease-{meshPipeline.RtId.ToString()}",
                                scheduleGroup,
                                pipelineTrigger.Description ?? pipelineTrigger.Name ?? "Pipeline Trigger (leased adapter)",
                                SchedulingMissedEventPolicy.Skip);

                            await distributionEventHubService.ScheduleRecurringSendAsync(
                                new LeaseTriggerMessage(tenantId, meshPipeline.RtId.ToString(),
                                    pipelineTrigger.RtId.ToString()),
                                leaseAddress, leaseTriggerOptions);

                            logger.LogInformation(
                                "[{TenantId}] Trigger '{TriggerRtId}' of pipeline '{PipelineRtId}' is routed through the " +
                                "lease queue: adapter '{AdapterName}' is Leased (AB#5278)",
                                tenantId, pipelineTrigger.RtId, meshPipeline.RtId, executingAdapter.Name);
                            continue;
                        }

                        var address =
                            $"{QueueNames.PipelineTriggerQueue.ToLower()}-{tenantId.ToLower()}-{meshPipeline.RtId.ToString().ToLower()}";

                        var pipelineTriggerSchedule =
                            new PipelineTriggerSchedule(tenantId, Guid.NewGuid(), DateTime.Now);

                        var recurringSchedulingOptions = new RecurringSchedulingOptions(
                            pipelineTrigger.CronExpression,
                            DateTime.Now, null,
                            $"{pipelineTrigger.RtId.ToString()}-pipeline-{meshPipeline.RtId.ToString()}", scheduleGroup,
                            pipelineTrigger.Description ?? pipelineTrigger.Name ?? "Pipeline Trigger",
                            SchedulingMissedEventPolicy.Skip);

                        await distributionEventHubService.ScheduleRecurringSendAsync(pipelineTriggerSchedule,
                            address, recurringSchedulingOptions);

                        // AB#4918 cron co-wake: for pipelines on an OnDemand workload, register a
                        // companion recurring send (same cron, same schedule group so it is
                        // added/removed together with the trigger schedule) to the controller's
                        // durable wake queue. The trigger message above buffers durably on the
                        // per-pipeline queue while the adapter is hibernated; the co-wake tick
                        // brings the adapter up to consume it. Registered independently of the
                        // tenant's ScaleToZeroEnabled flag — the consumer-side gate no-ops when
                        // the feature is off, and flipping the flag later must not require a
                        // trigger redeploy.
                        if (executingAdapter?.LifecycleMode == RtLifecycleModeEnum.OnDemand)
                        {
                            var wakeAddress = $"queue:{PipelineQueueNames.LifecycleWakeQueue.ToLower()}";
                            var coWakeOptions = new RecurringSchedulingOptions(
                                pipelineTrigger.CronExpression,
                                DateTime.Now, null,
                                $"{pipelineTrigger.RtId.ToString()}-wake-{meshPipeline.RtId.ToString()}",
                                scheduleGroup,
                                "Lifecycle co-wake (AB#4918)",
                                SchedulingMissedEventPolicy.Skip);

                            await distributionEventHubService.ScheduleRecurringSendAsync(
                                new LifecycleWakeMessage(tenantId, executingAdapter.RtId.ToString()),
                                wakeAddress, coWakeOptions);
                        }
                    }

                    await communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId,
                        pipelineTrigger.RtId, RtDeploymentStateEnum.Deployed);
                }
                catch (Exception)
                {
                    await communicationRepository.SetPipelineTriggerDeploymentStateAsync(tenantId,
                        pipelineTrigger.RtId, RtDeploymentStateEnum.Error);
                    throw;
                }
            }

            await eventService.StoreInformationEventAsync(tenantId,
                $"Trigger schedule updated with {pipelineTriggerKeyValues.Count} trigger(s).");
        }
        catch (Exception e)
        {
            await eventService.StoreErrorEventAsync(tenantId,
                $"Trigger schedule update failed: {e.Message}");
            throw TriggerManagementServiceException.UpdateScheduleFailed(tenantId, e);
        }

        logger.LogInformation("[{TenantId}] Startup completed", tenantId);
    }

    private static string CreateScheduleGroup(string tenantId)
    {
        var scheduleGroup = $"pipelineTrigger-{tenantId}";
        return scheduleGroup;
    }
}