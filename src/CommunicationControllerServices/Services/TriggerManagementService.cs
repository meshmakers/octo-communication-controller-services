using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService : ITriggerManagementService
{
    private readonly ILogger<TriggerManagementService> _logger;
    private readonly ICommunicationRepository _communicationRepository;

    private readonly ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>
        _removeRecurringJobsByScheduleGroupCommandClient;

    private readonly IDistributionEventHubService _distributionEventHubService;

    public TriggerManagementService(ILogger<TriggerManagementService> logger, 
        ICommunicationRepository communicationRepository,
        ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
        IDistributionEventHubService distributionEventHubService)
    {
        _logger = logger;
        _communicationRepository = communicationRepository;
        _removeRecurringJobsByScheduleGroupCommandClient = removeRecurringJobsByScheduleGroupCommandClient;
        _distributionEventHubService = distributionEventHubService;
    }

    public async Task RemoveScheduleAsync(string tenantId)
    {
        _logger.LogInformation("Removing triggers of tenant '{TenantId}'", tenantId);

        try
        {
            var r = await _communicationRepository.GetTriggersAsync(tenantId);
            foreach (var rtDataPipelineTrigger in r)
            {
                await _communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
                    rtDataPipelineTrigger.RtId, RtDeploymentStateEnum.Pending);
            }
            
            var scheduleGroup = CreateScheduleGroup(tenantId);
            await _removeRecurringJobsByScheduleGroupCommandClient.GetResponse<GenericCommandResponse>(
                new RemoveRecurringJobsByScheduleGroupRequest(scheduleGroup));

            _logger.LogInformation("Removal of triggers of tenant '{TenantId}' completed", tenantId);
        }
        catch (Exception e)
        {
            throw TriggerManagementServiceException.RemoveScheduleFailed(tenantId, e);
        }
    }

    public async Task UpdateScheduleAsync(string tenantId)
    {
        _logger.LogInformation("Loading triggers of tenant '{TenantId}'", tenantId);

        var scheduleGroup = CreateScheduleGroup(tenantId);
        await RemoveScheduleAsync(tenantId);

        try
        {
            var a = await _communicationRepository.GetTriggersAndPipelinesAsync(tenantId);

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
                    await _distributionEventHubService.ScheduleRecurringSendAsync(executePipeline,
                        QueueNames.PipelineTriggerQueue, recurringSchedulingOptions);

                    await _communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
                        pipelineTrigger.RtId, RtDeploymentStateEnum.Deployed);
                }
                catch (Exception)
                {
                    await _communicationRepository.SetDataPipelineTriggerDeploymentStateAsync(tenantId,
                        pipelineTrigger.RtId, RtDeploymentStateEnum.Error);
                    throw;
                }
            }
        }
        catch (Exception e)
        {
           
            throw TriggerManagementServiceException.UpdateScheduleFailed(tenantId, e);
        }

        _logger.LogInformation("Startup of tenant '{TenantId}' completed", tenantId);
    }

    private static string CreateScheduleGroup(string tenantId)
    {
        var scheduleGroup = $"pipelineTrigger-{tenantId}";
        return scheduleGroup;
    }
}