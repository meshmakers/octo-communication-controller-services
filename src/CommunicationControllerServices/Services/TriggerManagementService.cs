using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v1;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class TriggerManagementService : ITriggerManagementService
{
    private readonly ILogger<TriggerManagementService> _logger;
    private readonly ISystemContext _systemContext;
    private readonly ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> _removeRecurringJobsByScheduleGroupCommandClient;
    private readonly IDistributionEventHubService _distributionEventHubService;

    public TriggerManagementService(ILogger<TriggerManagementService> logger, ISystemContext systemContext,
        ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
        IDistributionEventHubService distributionEventHubService)
    {
        _logger = logger;
        _systemContext = systemContext;
        _removeRecurringJobsByScheduleGroupCommandClient = removeRecurringJobsByScheduleGroupCommandClient;
        _distributionEventHubService = distributionEventHubService;
    }
    
    public async Task RemoveScheduleAsync(string tenantId)
    {
        var scheduleGroup = CreateScheduleGroup(tenantId);
        await _removeRecurringJobsByScheduleGroupCommandClient.GetResponse<GenericCommandResponse>(new RemoveRecurringJobsByScheduleGroupRequest(scheduleGroup));
    }

    public async Task UpdateScheduleAsync(string tenantId)
    {
        var scheduleGroup = CreateScheduleGroup(tenantId);
        await RemoveScheduleAsync(tenantId);

        _logger.LogInformation("Loading triggers of tenant '{TenantId}'", tenantId);
        var tenantRepository = await _systemContext.FindTenantRepositoryAsync(tenantId);

        using var session = await tenantRepository.GetSessionAsync();

        try
        {
            session.StartTransaction();

            DataQueryOperation dataQueryOperation = DataQueryOperation.Create()
                .FieldEquals(nameof(RtDataPipelineTrigger.Enabled), true);

            var r = await tenantRepository.GetRtEntitiesByTypeAsync<RtDataPipelineTrigger>(session, dataQueryOperation);

            dataQueryOperation = DataQueryOperation.Create();
            var ckRoleId =
                new CkId<CkAssociationRoleId>(SystemCommunicationCkIds.ModelId, SystemCommunicationCkIds.Triggers);
            var a = await tenantRepository.GetRtAssociationTargetsAsync<RtDataPipelineTrigger, RtDataPipeline>(session,
                r.Items.Select(x => x.RtId).ToList(),
                ckRoleId, GraphDirections.Outbound, null, dataQueryOperation);

            foreach (var rtDataPipelineTrigger in r.Items)
            {
                if (a.TryGetValue(rtDataPipelineTrigger.RtId, out var resultSet))
                {
                    var executePipeline =
                        new PipelineTriggerSchedule(tenantId, resultSet.Items.Select(x => x.RtId).ToList());
                    var recurringSchedulingOptions = new RecurringSchedulingOptions(
                        rtDataPipelineTrigger.CronExpression,
                        DateTime.Now, null, rtDataPipelineTrigger.RtId.ToString(), scheduleGroup,
                        rtDataPipelineTrigger.Description ?? rtDataPipelineTrigger.Name,
                        SchedulingMissedEventPolicy.Skip);
                    await _distributionEventHubService.ScheduleRecurringSendAsync(executePipeline,
                        "queue:pipelineTriggers", recurringSchedulingOptions);
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }

        _logger.LogInformation("Startup of tenant '{TenantId}' completed", tenantId);
    }
    
    private static string CreateScheduleGroup(string tenantId)
    {
        var scheduleGroup = $"pipelineTrigger-{tenantId}";
        return scheduleGroup;
    }
}