using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

[SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
[SuppressMessage("Usage", "NS5000:Unused received check.")]
internal class NotificationScheduleServiceTests
{
    private const string TenantId = "Tenant1";
    private const string Group = "notification-tenant1";
    private static readonly DateTime Now = new(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);

    private static readonly RtEntityId EnginePipelineId =
        new(SystemCommunicationCkIds.RtCkPipelineTypeId, NotificationEngine.PipelineRtId);

    private readonly INotificationRepository _notificationRepository = Substitute.For<INotificationRepository>();

    private readonly ICommandClient<RemoveRecurringJobsByScheduleGroupRequest> _removeRecurringJobsCommandClient =
        Substitute.For<ICommandClient<RemoveRecurringJobsByScheduleGroupRequest>>();

    private readonly IDistributionEventHubService _distributionEventHubService =
        Substitute.For<IDistributionEventHubService>();

    private readonly ICommunicationRepository _communicationRepository = Substitute.For<ICommunicationRepository>();
    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();
    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();
    private readonly NotificationScheduleService _service;

    public NotificationScheduleServiceTests()
    {
        var timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(new DateTimeOffset(Now));

        _removeRecurringJobsCommandClient
            .GetResponseWithRetry<GenericCommandResponse>(Arg.Any<RemoveRecurringJobsByScheduleGroupRequest>(),
                Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new GenericCommandResponse());
        _distributionEventHubService.SendAsync(Arg.Any<Uri>(), Arg.Any<NotificationScheduleTick>(),
                Arg.Any<CancellationToken?>())
            .Returns(Task.FromResult(Task.CompletedTask));
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId, Arg.Any<OctoObjectId?>()).Returns([]);
        _notificationRepository.SubscriptionExistsAsync(TenantId, Arg.Any<OctoObjectId>()).Returns(true);
        _communicationRepository.GetPipelineAsync(TenantId, EnginePipelineId)
            .Returns(RtEntityCreator.CreatePipeline(id: NotificationEngine.PipelineRtId.ToString()));

        _service = new NotificationScheduleService(Substitute.For<ILogger<NotificationScheduleService>>(),
            _notificationRepository, _removeRecurringJobsCommandClient, _distributionEventHubService,
            _communicationRepository, _adapterService, _eventService, timeProvider);
    }

    private static NotificationPreferenceSchedule Schedule(string cron = "0 7 * * *", string? zone = null,
        bool hasNeverRun = false, bool takesBaseline = true) =>
        new(OctoObjectId.GenerateNewId(), cron, zone, hasNeverRun, takesBaseline);

    [Test]
    public async Task UpdateScheduleAsync_SchedulesOneTickPerPreferenceInItsTimeZone()
    {
        var berlin = Schedule("0 7 * * *", "Europe/Berlin");
        var none = Schedule("*/5 * * * *");
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([berlin, none]);

        await _service.UpdateScheduleAsync(TenantId);

        var address = NotificationEngine.CreateScheduleAddress(TenantId);
        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == berlin.PreferenceRtId.ToString()), address,
            Arg.Is<RecurringSchedulingOptions>(o =>
                o.CronExpression == "0 7 * * *" && o.TimeZoneId == "Europe/Berlin" && o.ScheduleGroup == Group &&
                o.ScheduleId == $"{berlin.PreferenceRtId}-notification-tenant1" &&
                o.MisfirePolicy == SchedulingMissedEventPolicy.Skip));
        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == none.PreferenceRtId.ToString()), address,
            Arg.Is<RecurringSchedulingOptions>(o => o.TimeZoneId == "UTC"));
    }

    [Test]
    public async Task UpdateScheduleAsync_UnknownTimeZone_FallsBackToUtc()
    {
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([Schedule(zone: "Europe/Viena")]);

        await _service.UpdateScheduleAsync(TenantId);

        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(Arg.Any<NotificationScheduleTick>(),
            Arg.Any<string>(), Arg.Is<RecurringSchedulingOptions>(o => o.TimeZoneId == "UTC"));
    }

    [Test]
    public async Task UpdateScheduleAsync_RemovesTheTenantGroupBeforeScheduling()
    {
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([Schedule()]);

        await _service.UpdateScheduleAsync(TenantId);

        Received.InOrder(() =>
        {
            _removeRecurringJobsCommandClient.GetResponseWithRetry<GenericCommandResponse>(
                Arg.Is<RemoveRecurringJobsByScheduleGroupRequest>(r => r.ScheduleGroup == Group),
                Arg.Any<int>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
            _distributionEventHubService.ScheduleRecurringSendAsync(Arg.Any<NotificationScheduleTick>(),
                Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
        });
    }

    [Test]
    public async Task UpdateScheduleAsync_OneFailingPreference_SchedulesTheRestAndReportsIt()
    {
        var failing = Schedule();
        var next = Schedule();
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([failing, next]);
        _distributionEventHubService.ScheduleRecurringSendAsync(
                Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == failing.PreferenceRtId.ToString()),
                Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>())
            .ThrowsAsync(new InvalidOperationException("bus down"));

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.UpdateScheduleAsync(TenantId));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.ScheduleIncomplete);
        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == next.PreferenceRtId.ToString()),
            Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains(failing.PreferenceRtId.ToString()!)));
    }

    [Test]
    public async Task UpdateScheduleAsync_StartsTheFirstRunOfPreferencesThatNeverRan()
    {
        var fresh = Schedule(hasNeverRun: true);
        var running = Schedule();
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([fresh, running]);

        await _service.UpdateScheduleAsync(TenantId);

        await _distributionEventHubService.Received(1).SendAsync(Arg.Any<Uri>(),
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == fresh.PreferenceRtId.ToString()),
            Arg.Any<CancellationToken?>());
        await _distributionEventHubService.DidNotReceive().SendAsync(Arg.Any<Uri>(),
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == running.PreferenceRtId.ToString()),
            Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task UpdateScheduleAsync_CatchUpPreferenceThatNeverRan_WaitsForItsOwnSchedule()
    {
        var catchUp = Schedule(hasNeverRun: true, takesBaseline: false);
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([catchUp]);

        await _service.UpdateScheduleAsync(TenantId);

        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == catchUp.PreferenceRtId.ToString()),
            Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
        await _distributionEventHubService.DidNotReceive().SendAsync(Arg.Any<Uri>(),
            Arg.Any<NotificationScheduleTick>(), Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task DeploySubscriptionAsync_SendsTheFirstRunToOnwardsPreferencesOnly()
    {
        var subscriptionRtId = OctoObjectId.GenerateNewId();
        var onwards = Schedule();
        var catchUp = Schedule(takesBaseline: false);
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId, subscriptionRtId)
            .Returns([onwards, catchUp]);

        await _service.DeploySubscriptionAsync(TenantId, subscriptionRtId);

        await _distributionEventHubService.Received(1).SendAsync(Arg.Any<Uri>(),
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == onwards.PreferenceRtId.ToString()),
            Arg.Any<CancellationToken?>());
        await _distributionEventHubService.DidNotReceive().SendAsync(Arg.Any<Uri>(),
            Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == catchUp.PreferenceRtId.ToString()),
            Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task UpdateScheduleAsync_OnDemandEngineAdapter_AlsoSchedulesACoWake()
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.LifecycleMode = RtLifecycleModeEnum.OnDemand;
        _communicationRepository.GetAdapterByPipelineAsync(TenantId, EnginePipelineId).Returns(adapter);
        var schedule = Schedule(zone: "Europe/Vienna");
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId).Returns([schedule]);

        await _service.UpdateScheduleAsync(TenantId);

        await _distributionEventHubService.Received(1).ScheduleRecurringSendAsync(
            Arg.Is<LifecycleWakeMessage>(m => m.WorkloadRtId == adapter.RtId.ToString()),
            $"queue:{PipelineQueueNames.LifecycleWakeQueue.ToLower()}",
            Arg.Is<RecurringSchedulingOptions>(o =>
                o.ScheduleId == $"{schedule.PreferenceRtId}-notification-wake-tenant1" &&
                o.CronExpression == schedule.CronExpression && o.TimeZoneId == "Europe/Vienna"));
    }

    [Test]
    public async Task DeploySubscriptionAsync_PersistsThenSchedulesItsPreferencesThenTheEngineThenTheFirstRun()
    {
        var subscriptionRtId = OctoObjectId.GenerateNewId();
        var schedule = Schedule();
        _notificationRepository.GetDeployedPreferenceSchedulesAsync(TenantId, subscriptionRtId).Returns([schedule]);
        var engine = RtEntityCreator.CreatePipeline(id: NotificationEngine.PipelineRtId.ToString());
        engine.DeploymentState = RtDeploymentStateEnum.Undeployed;
        _communicationRepository.GetPipelineAsync(TenantId, EnginePipelineId).Returns(engine);

        await _service.DeploySubscriptionAsync(TenantId, subscriptionRtId);

        Received.InOrder(() =>
        {
            _notificationRepository.SetSubscriptionDeployedAsync(TenantId, subscriptionRtId, Now);
            _distributionEventHubService.ScheduleRecurringSendAsync(Arg.Any<NotificationScheduleTick>(),
                Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
            _adapterService.DeployDataFlowAsync(TenantId, NotificationEngine.DataFlowRtId);
            _distributionEventHubService.SendAsync(Arg.Any<Uri>(),
                Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == schedule.PreferenceRtId.ToString()),
                Arg.Any<CancellationToken?>());
        });
        await _removeRecurringJobsCommandClient.DidNotReceive().GetResponseWithRetry<GenericCommandResponse>(
            Arg.Any<RemoveRecurringJobsByScheduleGroupRequest>(), Arg.Any<int>(), Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task DeploySubscriptionAsync_EngineDeployed_LeavesItRunning()
    {
        await _service.DeploySubscriptionAsync(TenantId, OctoObjectId.GenerateNewId());

        await _adapterService.DidNotReceive().DeployDataFlowAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeploySubscriptionAsync_UnknownSubscription_IsNotFoundAndChangesNothing()
    {
        var subscriptionRtId = OctoObjectId.GenerateNewId();
        _notificationRepository.SubscriptionExistsAsync(TenantId, subscriptionRtId).Returns(false);

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.DeploySubscriptionAsync(TenantId, subscriptionRtId));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.SubscriptionNotFound);
        await _notificationRepository.DidNotReceive().SetSubscriptionDeployedAsync(Arg.Any<string>(),
            Arg.Any<OctoObjectId>(), Arg.Any<DateTime?>());
    }

    [Test]
    public async Task DeploySubscriptionAsync_EngineMissing_SaysWhichBlueprintSeedsIt()
    {
        _communicationRepository.GetPipelineAsync(TenantId, EnginePipelineId).Returns((RtPipeline?)null);

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.DeploySubscriptionAsync(TenantId, OctoObjectId.GenerateNewId()));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.EngineUnavailable);
        await Assert.That(exception.Message).Contains("System.Communication.Notifications");
    }

    [Test]
    public async Task DeploySubscriptionAsync_EngineDisabled_IsReportedInsteadOfASilentSuccess()
    {
        var engine = RtEntityCreator.CreatePipeline(id: NotificationEngine.PipelineRtId.ToString());
        engine.DeploymentState = RtDeploymentStateEnum.Undeployed;
        engine.Enabled = false;
        _communicationRepository.GetPipelineAsync(TenantId, EnginePipelineId).Returns(engine);

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.DeploySubscriptionAsync(TenantId, OctoObjectId.GenerateNewId()));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.EngineUnavailable);
        await _adapterService.DidNotReceive().DeployDataFlowAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task DeploySubscriptionAsync_EngineDeployFails_ReportsThatNoMailIsSentYet()
    {
        var engine = RtEntityCreator.CreatePipeline(id: NotificationEngine.PipelineRtId.ToString());
        engine.DeploymentState = RtDeploymentStateEnum.Undeployed;
        _communicationRepository.GetPipelineAsync(TenantId, EnginePipelineId).Returns(engine);
        _adapterService.DeployDataFlowAsync(TenantId, NotificationEngine.DataFlowRtId)
            .Returns(Task.FromException(new InvalidOperationException("adapter offline")));

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.DeploySubscriptionAsync(TenantId, OctoObjectId.GenerateNewId()));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.EngineUnavailable);
        await Assert.That(exception.Message).Contains("adapter offline");
    }

    [Test]
    public async Task UndeploySubscriptionAsync_CancelsOnlyThatSubscriptionsSchedules()
    {
        var subscriptionRtId = OctoObjectId.GenerateNewId();
        var preferenceRtId = OctoObjectId.GenerateNewId();
        _notificationRepository.GetPreferenceRtIdsAsync(TenantId, subscriptionRtId).Returns([preferenceRtId]);

        await _service.UndeploySubscriptionAsync(TenantId, subscriptionRtId);

        await _notificationRepository.Received(1).SetSubscriptionDeployedAsync(TenantId, subscriptionRtId, null);
        await _distributionEventHubService.Received(1)
            .CancelScheduledRecurringSendAsync($"{preferenceRtId}-notification-tenant1", Group);
        await _distributionEventHubService.Received(1)
            .CancelScheduledRecurringSendAsync($"{preferenceRtId}-notification-wake-tenant1", Group);
        await _removeRecurringJobsCommandClient.DidNotReceive().GetResponseWithRetry<GenericCommandResponse>(
            Arg.Any<RemoveRecurringJobsByScheduleGroupRequest>(), Arg.Any<int>(), Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task SyncPreferenceAsync_ReschedulesOnlyThatPreference()
    {
        var schedule = Schedule(hasNeverRun: true);
        _notificationRepository.GetDeployedPreferenceScheduleAsync(TenantId, schedule.PreferenceRtId).Returns(schedule);

        await _service.SyncPreferenceAsync(TenantId, schedule.PreferenceRtId);

        Received.InOrder(() =>
        {
            _distributionEventHubService.CancelScheduledRecurringSendAsync(
                $"{schedule.PreferenceRtId}-notification-tenant1", Group);
            _distributionEventHubService.ScheduleRecurringSendAsync(
                Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == schedule.PreferenceRtId.ToString()),
                Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
            _distributionEventHubService.SendAsync(Arg.Any<Uri>(),
                Arg.Is<NotificationScheduleTick>(t => t.PreferenceRtId == schedule.PreferenceRtId.ToString()),
                Arg.Any<CancellationToken?>());
        });
        await _removeRecurringJobsCommandClient.DidNotReceive().GetResponseWithRetry<GenericCommandResponse>(
            Arg.Any<RemoveRecurringJobsByScheduleGroupRequest>(), Arg.Any<int>(), Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    [Test]
    public async Task SyncPreferenceAsync_DeletedOrUndeployedPreference_IsOnlyCancelled()
    {
        var preferenceRtId = OctoObjectId.GenerateNewId();
        _notificationRepository.GetDeployedPreferenceScheduleAsync(TenantId, preferenceRtId)
            .Returns((NotificationPreferenceSchedule?)null);

        await _service.SyncPreferenceAsync(TenantId, preferenceRtId);

        await _distributionEventHubService.Received(1)
            .CancelScheduledRecurringSendAsync($"{preferenceRtId}-notification-tenant1", Group);
        await _distributionEventHubService.Received(1)
            .CancelScheduledRecurringSendAsync($"{preferenceRtId}-notification-wake-tenant1", Group);
        await _distributionEventHubService.DidNotReceive().ScheduleRecurringSendAsync(
            Arg.Any<NotificationScheduleTick>(), Arg.Any<string>(), Arg.Any<RecurringSchedulingOptions>());
    }

    [Test]
    public async Task SyncPreferenceAsync_PreferenceThatRanBefore_GetsNoFirstRun()
    {
        var schedule = Schedule();
        _notificationRepository.GetDeployedPreferenceScheduleAsync(TenantId, schedule.PreferenceRtId).Returns(schedule);

        await _service.SyncPreferenceAsync(TenantId, schedule.PreferenceRtId);

        await _distributionEventHubService.DidNotReceive().SendAsync(Arg.Any<Uri>(),
            Arg.Any<NotificationScheduleTick>(), Arg.Any<CancellationToken?>());
    }

    [Test]
    public async Task UndeploySubscriptionAsync_UnknownSubscription_IsNotFound()
    {
        var subscriptionRtId = OctoObjectId.GenerateNewId();
        _notificationRepository.SubscriptionExistsAsync(TenantId, subscriptionRtId).Returns(false);

        var exception = await Assert.ThrowsAsync<NotificationScheduleServiceException>(() =>
            _service.UndeploySubscriptionAsync(TenantId, subscriptionRtId));

        await Assert.That(exception!.Kind).IsEqualTo(NotificationScheduleErrorKind.SubscriptionNotFound);
    }

    [Test]
    public async Task CreateScheduleAddress_IsTheLowerCasedQueueOfTheEnginePipeline()
    {
        var address = NotificationEngine.CreateScheduleAddress("EcFinal");

        await Assert.That(address)
            .IsEqualTo("queue:octo::com-controller::notification-schedule-ecfinal-670000000000000000002581");
    }
}
