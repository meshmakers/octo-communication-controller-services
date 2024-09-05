using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService(
    ILogger<TriggerManagementService> logger,
    ICommunicationRepository communicationRepository,
    ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
    IDistributionEventHubService distributionEventHubService)
    : ITriggerManagementService
{
    public async Task RemoveScheduleAsync(string tenantId)
    {
        logger.LogInformation("Removing triggers of tenant '{TenantId}'", tenantId);

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

            logger.LogInformation("Removal of triggers of tenant '{TenantId}' completed", tenantId);
        }
        catch (Exception e)
        {
            throw TriggerManagementServiceException.RemoveScheduleFailed(tenantId, e);
        }
    }

    public async Task UpdateScheduleAsync(string tenantId)
    {
        logger.LogInformation("Loading triggers of tenant '{TenantId}'", tenantId);

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
                    var executePipeline =
                        new PipelineTriggerSchedule(tenantId, pipelineTriggerKeyValue.Value.Select(x => x.RtId).ToList());
                    var recurringSchedulingOptions = new RecurringSchedulingOptions(
                        pipelineTrigger.CronExpression,
                        DateTime.Now, null, pipelineTrigger.RtId.ToString(), scheduleGroup,
                        pipelineTrigger.Description ?? pipelineTrigger.Name,
                        SchedulingMissedEventPolicy.Skip);
                    await distributionEventHubService.ScheduleRecurringSendAsync(executePipeline,
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

        logger.LogInformation("Startup of tenant '{TenantId}' completed", tenantId);
    }

    private static string CreateScheduleGroup(string tenantId)
    {
        var scheduleGroup = $"pipelineTrigger-{tenantId}";
        return scheduleGroup;
    }
}