using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class NotificationScheduleService(
    ILogger<NotificationScheduleService> logger,
    INotificationRepository notificationRepository,
    ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> removeRecurringJobsByScheduleGroupCommandClient,
    IDistributionEventHubService distributionEventHubService,
    ICommunicationRepository communicationRepository,
    IAdapterService adapterService,
    ICommunicationEventService communicationEventService,
    TimeProvider timeProvider)
    : INotificationScheduleService
{
    private const string Utc = "UTC";

    private static readonly RtEntityId EnginePipelineId =
        new(SystemCommunicationCkIds.RtCkPipelineTypeId, NotificationEngine.PipelineRtId);

    public async Task RemoveScheduleAsync(string tenantId)
    {
        await removeRecurringJobsByScheduleGroupCommandClient.GetResponseWithRetry<GenericCommandResponse>(
            new RemoveRecurringJobsByScheduleGroupRequest(CreateScheduleGroup(tenantId)));
    }

    public async Task UpdateScheduleAsync(string tenantId)
    {
        await RemoveScheduleAsync(tenantId);

        var schedules = await notificationRepository.GetDeployedPreferenceSchedulesAsync(tenantId);
        var failures = await ScheduleAsync(tenantId, schedules);
        await SendBaselineTicksAsync(tenantId, schedules.Where(schedule => schedule.HasNeverRun));

        logger.LogInformation("[{TenantId}] Notification schedule rebuilt with {Count} preference(s)", tenantId,
            schedules.Count - failures.Count);
        await ThrowIfIncompleteAsync(tenantId, failures);
    }

    public async Task DeploySubscriptionAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        await EnsureSubscriptionExistsAsync(tenantId, subscriptionRtId);
        await notificationRepository.SetSubscriptionDeployedAsync(tenantId, subscriptionRtId,
            timeProvider.GetUtcNow().UtcDateTime);

        var schedules = await notificationRepository.GetDeployedPreferenceSchedulesAsync(tenantId, subscriptionRtId);
        var failures = await ScheduleAsync(tenantId, schedules);
        await EnsureEngineDeployedAsync(tenantId);
        await SendBaselineTicksAsync(tenantId, schedules);

        await ThrowIfIncompleteAsync(tenantId, failures);
    }

    public async Task UndeploySubscriptionAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        await EnsureSubscriptionExistsAsync(tenantId, subscriptionRtId);
        await notificationRepository.SetSubscriptionDeployedAsync(tenantId, subscriptionRtId, null);

        foreach (var preferenceRtId in await notificationRepository.GetPreferenceRtIdsAsync(tenantId, subscriptionRtId))
        {
            await CancelPreferenceAsync(tenantId, preferenceRtId);
        }
    }

    public async Task SyncPreferenceAsync(string tenantId, OctoObjectId preferenceRtId)
    {
        await CancelPreferenceAsync(tenantId, preferenceRtId);

        var schedule = await notificationRepository.GetDeployedPreferenceScheduleAsync(tenantId, preferenceRtId);
        if (schedule == null)
        {
            return;
        }

        var failures = await ScheduleAsync(tenantId, [schedule]);
        await SendBaselineTicksAsync(tenantId, new[] { schedule }.Where(candidate => candidate.HasNeverRun));

        await ThrowIfIncompleteAsync(tenantId, failures);
    }

    private async Task CancelPreferenceAsync(string tenantId, OctoObjectId preferenceRtId)
    {
        var scheduleGroup = CreateScheduleGroup(tenantId);
        await distributionEventHubService.CancelScheduledRecurringSendAsync(
            CreateScheduleId(tenantId, preferenceRtId), scheduleGroup);
        await distributionEventHubService.CancelScheduledRecurringSendAsync(
            CreateWakeScheduleId(tenantId, preferenceRtId), scheduleGroup);
    }

    private async Task EnsureSubscriptionExistsAsync(string tenantId, OctoObjectId subscriptionRtId)
    {
        if (!await notificationRepository.SubscriptionExistsAsync(tenantId, subscriptionRtId))
        {
            throw NotificationScheduleServiceException.SubscriptionNotFound(tenantId, subscriptionRtId);
        }
    }

    private async Task<IReadOnlyList<(OctoObjectId PreferenceRtId, Exception Error)>> ScheduleAsync(string tenantId,
        IReadOnlyList<NotificationPreferenceSchedule> schedules)
    {
        var failures = new List<(OctoObjectId, Exception)>();
        if (schedules.Count == 0)
        {
            return failures;
        }

        var scheduleGroup = CreateScheduleGroup(tenantId);
        var address = NotificationEngine.CreateScheduleAddress(tenantId);
        var wakeAdapterRtId = await GetOnDemandEngineAdapterAsync(tenantId);

        foreach (var schedule in schedules)
        {
            try
            {
                var timeZoneId = ResolveTimeZone(tenantId, schedule);
                await distributionEventHubService.ScheduleRecurringSendAsync(CreateTick(tenantId, schedule.PreferenceRtId),
                    address, CreateOptions(schedule, CreateScheduleId(tenantId, schedule.PreferenceRtId), scheduleGroup,
                        $"Notification preference {schedule.PreferenceRtId}", timeZoneId));

                if (wakeAdapterRtId != null)
                {
                    await distributionEventHubService.ScheduleRecurringSendAsync(
                        new LifecycleWakeMessage(tenantId, wakeAdapterRtId.Value.ToString()),
                        $"queue:{PipelineQueueNames.LifecycleWakeQueue.ToLower()}",
                        CreateOptions(schedule, CreateWakeScheduleId(tenantId, schedule.PreferenceRtId), scheduleGroup,
                            "Lifecycle co-wake for a notification preference", timeZoneId));
                }
            }
            catch (Exception e)
            {
                logger.LogError(e, "[{TenantId}] Notification preference '{PreferenceRtId}' could not be scheduled",
                    tenantId, schedule.PreferenceRtId);
                failures.Add((schedule.PreferenceRtId, e));
            }
        }

        return failures;
    }

    private async Task ThrowIfIncompleteAsync(string tenantId,
        IReadOnlyList<(OctoObjectId PreferenceRtId, Exception Error)> failures)
    {
        if (failures.Count == 0)
        {
            return;
        }

        var exception = NotificationScheduleServiceException.ScheduleIncomplete(tenantId,
            failures.Select(failure => failure.PreferenceRtId).ToList(), failures[0].Error);
        await communicationEventService.StoreErrorEventAsync(tenantId, exception.Message);
        throw exception;
    }

    private async Task SendBaselineTicksAsync(string tenantId, IEnumerable<NotificationPreferenceSchedule> schedules)
    {
        var baselines = schedules.Where(schedule => schedule.TakesBaseline).ToList();
        if (baselines.Count == 0)
        {
            return;
        }

        await WakeOnDemandEngineAsync(tenantId);

        var address = new Uri(NotificationEngine.CreateScheduleAddress(tenantId));
        foreach (var schedule in baselines)
        {
            try
            {
                await await distributionEventHubService.SendAsync(address, CreateTick(tenantId, schedule.PreferenceRtId));
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "[{TenantId}] First run of notification preference '{PreferenceRtId}' could not be started; it starts with its next scheduled tick",
                    tenantId, schedule.PreferenceRtId);
            }
        }
    }

    private async Task WakeOnDemandEngineAsync(string tenantId)
    {
        try
        {
            var wakeAdapterRtId = await GetOnDemandEngineAdapterAsync(tenantId);
            if (wakeAdapterRtId == null)
            {
                return;
            }

            var address = new Uri($"queue:{PipelineQueueNames.LifecycleWakeQueue.ToLower()}");
            await await distributionEventHubService.SendAsync(address,
                new LifecycleWakeMessage(tenantId, wakeAdapterRtId.Value.ToString()));
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "[{TenantId}] The on-demand notification engine could not be woken for the first runs; they start when it next wakes",
                tenantId);
        }
    }

    private async Task<OctoObjectId?> GetOnDemandEngineAdapterAsync(string tenantId)
    {
        var adapter = await communicationRepository.GetAdapterByPipelineAsync(tenantId, EnginePipelineId);
        return adapter?.LifecycleMode == RtLifecycleModeEnum.OnDemand ? adapter.RtId : null;
    }

    private async Task EnsureEngineDeployedAsync(string tenantId)
    {
        var pipeline = await communicationRepository.GetPipelineAsync(tenantId, EnginePipelineId);
        if (pipeline == null)
        {
            logger.LogError("[{TenantId}] Notification engine pipeline {PipelineRtId} does not exist", tenantId,
                NotificationEngine.PipelineRtId);
            throw NotificationScheduleServiceException.EngineMissing(tenantId);
        }

        if (pipeline.Enabled == false)
        {
            logger.LogError("[{TenantId}] Notification engine pipeline {PipelineRtId} is disabled", tenantId,
                NotificationEngine.PipelineRtId);
            throw NotificationScheduleServiceException.EngineDisabled(tenantId);
        }

        if (pipeline.DeploymentState == RtDeploymentStateEnum.Deployed)
        {
            return;
        }

        try
        {
            await adapterService.DeployDataFlowAsync(tenantId, NotificationEngine.DataFlowRtId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[{TenantId}] Notification engine could not be deployed", tenantId);
            throw NotificationScheduleServiceException.EngineDeployFailed(tenantId, e);
        }
    }

    private string ResolveTimeZone(string tenantId, NotificationPreferenceSchedule schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule.TimeZone))
        {
            return Utc;
        }

        if (TimeZoneInfo.TryFindSystemTimeZoneById(schedule.TimeZone, out _))
        {
            return schedule.TimeZone;
        }

        logger.LogWarning("[{TenantId}] Notification preference '{PreferenceRtId}' has the unknown time zone '{TimeZone}'; it is scheduled in UTC",
            tenantId, schedule.PreferenceRtId, schedule.TimeZone);
        return Utc;
    }

    private NotificationScheduleTick CreateTick(string tenantId, OctoObjectId preferenceRtId) =>
        new(tenantId, preferenceRtId.ToString(), Guid.NewGuid(), timeProvider.GetUtcNow().UtcDateTime);

    private static RecurringSchedulingOptions CreateOptions(NotificationPreferenceSchedule schedule, string scheduleId,
        string scheduleGroup, string description, string timeZoneId) =>
        new(schedule.CronExpression, scheduleId, scheduleGroup, description, SchedulingMissedEventPolicy.Skip)
        {
            TimeZoneId = timeZoneId
        };

    private static string CreateScheduleGroup(string tenantId) => $"notification-{tenantId.ToLowerInvariant()}";

    private static string CreateScheduleId(string tenantId, OctoObjectId preferenceRtId) =>
        $"{preferenceRtId}-notification-{tenantId.ToLowerInvariant()}";

    private static string CreateWakeScheduleId(string tenantId, OctoObjectId preferenceRtId) =>
        $"{preferenceRtId}-notification-wake-{tenantId.ToLowerInvariant()}";
}
