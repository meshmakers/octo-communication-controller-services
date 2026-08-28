using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
    IWorkloadLifecycleService workloadLifecycleService)
    : ITriggerManagementService
{
    public async Task<PipelineExecutionDataDto> StartExecutePipelineAsync(string tenantId,
        OctoObjectId pipelineRtId, string? pipelineInput, bool isDryRun = false)
    {
        logger.LogInformation("[{TenantId}] Executing pipeline '{PipelineRtId}' (dry-run={IsDryRun})",
            tenantId, pipelineRtId, isDryRun);

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
                new ExecutePipelineRequest(tenantId, pipelineInput) { IsDryRun = isDryRun });

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
                        var executingAdapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId,
                            new RtEntityId(SystemCommunicationCkIds.RtCkPipelineTypeId, meshPipeline.RtId));
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