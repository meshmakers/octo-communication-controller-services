using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v2;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService(
    ILogger<TriggerManagementService> logger,
    ICommunicationRepository communicationRepository,
    ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
    IRoutedCommandClient<ExecuteMeshPipelineRequest> executeMeshPipelineCommandClient,
    IDistributionEventHubService distributionEventHubService,
    ICommunicationEventService eventService)
    : ITriggerManagementService
{
    public async Task<PipelineExecutionDataDto> StartExecutePipelineAsync(string tenantId,
        OctoObjectId dataPipelineRtId, string? pipelineInput)
    {
        logger.LogInformation("[{TenantId}] Executing pipeline '{DataPipelineRtId}'", tenantId, dataPipelineRtId);

        ExecuteMeshPipelineResponse? r;
        try
        {
            var address =
                $"{QueueNames.ExecuteMeshPipelineCommand.ToLower()}-{tenantId.ToLower()}-data-pipeline-{dataPipelineRtId.ToString().ToLower()}";

            r = await executeMeshPipelineCommandClient.GetResponse<ExecuteMeshPipelineResponse>(address,
                new ExecuteMeshPipelineRequest(tenantId, pipelineInput));

            if (r.IsSuccessStartingExecution)
            {
                if (r is { PipelineExecutionId: not null, ExecutionStartTime: not null })
                {
                    logger.LogInformation(
                        "[{TenantId}] Start execution of pipeline '{DataPipelineRtId}' (ExecutionId {PipelineExecutionId}) successful",
                        tenantId, dataPipelineRtId, r.PipelineExecutionId);

                    await eventService.StoreInformationEventAsync(tenantId,
                        $"Pipeline '{dataPipelineRtId}' execution started (ExecutionId: {r.PipelineExecutionId}).");

                    return new PipelineExecutionDataDto
                               { Id = r.PipelineExecutionId.Value, DateTime = r.ExecutionStartTime.Value } ??
                           throw TriggerManagementServiceException.ExecutePipelineExecutionIdNull(tenantId,
                               dataPipelineRtId);
                }

                throw TriggerManagementServiceException.ExecutePipelineExecutionIdNull(tenantId,
                    dataPipelineRtId);
            }
        }
        catch (Exception e)
        {
            await eventService.StoreErrorEventAsync(tenantId,
                $"Pipeline '{dataPipelineRtId}' execution failed: {e.Message}");
            throw TriggerManagementServiceException.ExecutePipelineExecutionErrorFailed(tenantId, dataPipelineRtId, e);
        }

        await eventService.StoreErrorEventAsync(tenantId,
            $"Pipeline '{dataPipelineRtId}' execution failed: {r.ErrorMessage}");
        logger.LogError("[{TenantId}] Execution of pipeline '{DataPipelineRtId}' failed: {ErrorMessage}"
            , tenantId, dataPipelineRtId, r.ErrorMessage);
        throw TriggerManagementServiceException.ExecutePipelineFailed(tenantId, dataPipelineRtId, r.ErrorMessage);
    }

    public async Task RemoveScheduleAsync(string tenantId)
    {
        logger.LogInformation("[{TenantId}] Removing triggers of tenant", tenantId);

        try
        {
            var r = await communicationRepository.GetTriggersAsync(tenantId);
            foreach (var rtDataPipelineTrigger in r)
            {
                await communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
                    rtDataPipelineTrigger.RtId, RtDeploymentStateEnum.Pending);
            }

            var scheduleGroup = CreateScheduleGroup(tenantId);
            await removeRecurringJobsByScheduleGroupCommandClient.GetResponse<GenericCommandResponse>(
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
                        await communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
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
                    }

                    await communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
                        pipelineTrigger.RtId, RtDeploymentStateEnum.Deployed);
                }
                catch (Exception)
                {
                    await communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
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