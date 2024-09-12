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
    ICommandClient<ExecuteMeshPipelineRequest> executeMeshPipelineCommandClient,
    IDistributionEventHubService distributionEventHubService)
    : ITriggerManagementService
{
    
    public async Task<string?> ExecutePipelineAsync(string tenantId, RtEntityId meshPipelineRtEntityId, string? pipelineInput)
    {
        logger.LogInformation("[{TenantId}] Executing pipeline '{MeshPipelineRtEntityId}'", meshPipelineRtEntityId, tenantId);

        ExecuteMeshPipelineResponse? r;
        try
        {
            r = await executeMeshPipelineCommandClient.GetResponse<ExecuteMeshPipelineResponse>(
                new ExecuteMeshPipelineRequest(tenantId, meshPipelineRtEntityId, pipelineInput));

            if (r.IsSuccess)
            {
                logger.LogInformation("[{TenantId}] Execution of pipeline '{MeshPipelineRtEntityId}' completed", meshPipelineRtEntityId, tenantId);
                return r.PipelineOutput;
            }
        }
        catch (Exception e)
        {
            throw TriggerManagementServiceException.ExecutePipelineExecutionErrorFailed(tenantId, meshPipelineRtEntityId, e);
        }
        
        logger.LogError("[{TenantId}] Execution of pipeline '{MeshPipelineRtEntityId}' failed: {ErrorMessage}", tenantId, meshPipelineRtEntityId, r.ErrorMessage);
        throw TriggerManagementServiceException.ExecutePipelineFailed(tenantId, meshPipelineRtEntityId, r.ErrorMessage);

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
            var a = await communicationRepository.GetTriggersAndPipelinesAsync(tenantId);

            foreach (var pipelineTriggerKeyValue in a)
            {
                var pipelineTrigger = pipelineTriggerKeyValue.Key;
                try
                {
                    var pipelineTriggerSchedule =
                        new PipelineTriggerSchedule(tenantId, Guid.NewGuid(), DateTime.Now, 
                            pipelineTriggerKeyValue.Value.Select(x => x.RtId).ToList());
                    var recurringSchedulingOptions = new RecurringSchedulingOptions(
                        pipelineTrigger.CronExpression,
                        DateTime.Now, null, pipelineTrigger.RtId.ToString(), scheduleGroup,
                        pipelineTrigger.Description ?? pipelineTrigger.Name,
                        SchedulingMissedEventPolicy.Skip);
                    await distributionEventHubService.ScheduleRecurringSendAsync(pipelineTriggerSchedule,
                        QueueNames.PipelineTriggerQueue, recurringSchedulingOptions);

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