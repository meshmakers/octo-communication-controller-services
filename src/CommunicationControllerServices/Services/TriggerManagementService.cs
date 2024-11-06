using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService(
    ILogger<TriggerManagementService> logger,
    ICommunicationRepository communicationRepository,
    ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
    IRoutedCommandClient<ExecuteMeshPipelineRequest> executeMeshPipelineCommandClient,
    IDistributionEventHubService distributionEventHubService)
    : ITriggerManagementService
{
    
    public async Task<Guid> StartExecutePipelineAsync(string tenantId, OctoObjectId dataPipelineRtId, string? pipelineInput)
    {
        logger.LogInformation("[{TenantId}] Executing pipeline '{DataPipelineRtId}'", tenantId, dataPipelineRtId);

        ExecuteMeshPipelineResponse? r;
        try
        {
            var address =
                $"{QueueNames.ExecuteMeshPipelineCommand.ToLower()}-{tenantId.ToLower()}-data-pipeline-{dataPipelineRtId.ToString()?.ToLower()}";

            r = await executeMeshPipelineCommandClient.GetResponse<ExecuteMeshPipelineResponse>(address,
                new ExecuteMeshPipelineRequest(tenantId, pipelineInput));

            if (r.IsSuccessStartingExecution)
            {
                logger.LogInformation("[{TenantId}] Start execution of pipeline '{DataPipelineRtId}' (ExecutionId {PipelineExecutionId}) successful", tenantId, dataPipelineRtId, r.PipelineExecutionId);
                return r.PipelineExecutionId ?? throw TriggerManagementServiceException.ExecutePipelineExecutionIdNull(tenantId, dataPipelineRtId);
            }
        }
        catch (Exception e)
        {
            throw TriggerManagementServiceException.ExecutePipelineExecutionErrorFailed(tenantId, dataPipelineRtId, e);
        }
        
        logger.LogError("[{TenantId}] Execution of pipeline '{DataPipelineRtId}' failed: {ErrorMessage}", tenantId, dataPipelineRtId, r.ErrorMessage);
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
                    foreach (var meshPipeline in pipelineTriggerKeyValue.Value)
                    {
                        var address =
                            $"{QueueNames.PipelineTriggerQueue.ToLower()}-{tenantId.ToLower()}-{meshPipeline.RtId.ToString().ToLower()}";
                        
                        var pipelineTriggerSchedule =
                            new PipelineTriggerSchedule(tenantId, Guid.NewGuid(), DateTime.Now);
                    
                        var recurringSchedulingOptions = new RecurringSchedulingOptions(
                            pipelineTrigger.CronExpression,
                            DateTime.Now, null, $"{pipelineTrigger.RtId.ToString()}-pipeline-{meshPipeline.RtId.ToString()}", scheduleGroup,
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
        }
        catch (Exception e)
        {
           
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