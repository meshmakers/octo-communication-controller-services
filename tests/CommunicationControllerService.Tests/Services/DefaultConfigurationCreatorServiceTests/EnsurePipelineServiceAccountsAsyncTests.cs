using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DefaultConfigurationCreatorServiceTests;

/// <summary>
/// AB#5027 phase 2 backfill hook. The tenant-setup path is what keeps tenants that pre-date the
/// mandatory-identity guard out of it, so the two properties that matter are: it runs, and it can
/// never take a tenant's startup down with it.
/// </summary>
internal class EnsurePipelineServiceAccountsAsyncTests
{
    private const string TenantId = "child-a";

    private readonly IPipelineServiceAccountProvisioningService _provisioningService =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();

    private readonly INotificationScheduleService _notificationScheduleService =
        Substitute.For<INotificationScheduleService>();

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();

    [Test]
    public async Task ReportsAnAuditEvent_WhenSomethingWasProvisioned()
    {
        _provisioningService.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(2, 1, 3, []));

        await CreateSut().EnsurePipelineServiceAccountsAsync(TenantId);

        await _eventService.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("AB#5027")));
    }

    [Test]
    public async Task StaysSilent_WhenNothingChanged()
    {
        // The steady state runs on every tenant load; an event per load would be pure noise.
        _provisioningService.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 4, []));

        await CreateSut().EnsurePipelineServiceAccountsAsync(TenantId);

        await _eventService.DidNotReceiveWithAnyArgs()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task DoesNotThrow_WhenProvisioningItselfBlowsUp()
    {
        // A tenant that cannot reach the identity service must still load, keep serving its
        // already-deployed pipelines, and get its adapters, pools and trigger schedules. Only the
        // deploy of a NEW pipeline is refused, and that refusal names its own remedy.
        _provisioningService.EnsureTenantProvisionedAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("bus unavailable"));

        await CreateSut().EnsurePipelineServiceAccountsAsync(TenantId);
    }

    [Test]
    public async Task NotificationScheduleStep_FailureIsReportedButNeverEscapes()
    {
        await CreateSut().RunNotificationScheduleStepAsync(TenantId, "rebuilt",
            () => throw new InvalidOperationException("bus unavailable"));

        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("Notification schedule could not be rebuilt") && m.Contains("bus unavailable")));
    }

    [Test]
    public async Task NotificationScheduleStep_IncompleteSchedule_IsNotReportedASecondTime()
    {
        var incomplete = NotificationScheduleServiceException.ScheduleIncomplete(TenantId,
            [OctoObjectId.GenerateNewId()], new InvalidOperationException("bus unavailable"));

        await CreateSut().RunNotificationScheduleStepAsync(TenantId, "rebuilt", () => throw incomplete);

        await _eventService.DidNotReceive().StoreErrorEventAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task TenantStart_RebuildsTheNotificationSchedule_AndSurvivesItsFailure()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        _systemContext.FindTenantContextAsync(TenantId).Returns(tenantContext);
        tenantContext.GetAdminSessionAsync().Returns(Substitute.For<IOctoAdminSession>());
        tenantContext.GetConfigurationAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<string>(),
                Arg.Any<DefaultConfigurationEnabled>())
            .Returns(new DefaultConfigurationEnabled { IsEnabled = true });
        _provisioningService.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 0, []));
        _notificationScheduleService.UpdateScheduleAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("bus unavailable"));

        await CreateSut().StartAsync(TenantId);

        await _notificationScheduleService.Received(1).UpdateScheduleAsync(TenantId);
        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(m => m.Contains("Notification schedule could not be rebuilt")));
    }

    private TestableCreator CreateSut()
    {
        return new TestableCreator(_provisioningService, _eventService, _notificationScheduleService, _systemContext);
    }

    private sealed class TestableCreator(
        IPipelineServiceAccountProvisioningService provisioningService,
        ICommunicationEventService eventService,
        INotificationScheduleService notificationScheduleService,
        ISystemContext systemContext) : DefaultConfigurationCreatorService(
        NullLogger<DefaultConfigurationCreatorService>.Instance,
        Substitute.For<IDiagnosticsService>(),
        Options.Create(new CommunicationControllerOptions()),
        Substitute.For<ITriggerManagementService>(),
        notificationScheduleService,
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
        systemContext,
        Substitute.For<IDeploymentSiteService>(),
        Substitute.For<IAdapterCachePublish>(),
        Substitute.For<IAdapterService>(),
        provisioningService,
        Substitute.For<IAdapterPoolMirrorProvisioningService>(),
        new FailedTenantRegistry(),
        eventService,
        Substitute.For<IBlueprintService>(),
        Array.Empty<IBlueprintEmbeddedSource>())
    {
        public Task StartAsync(string tenantId) => StartTenantAsync(tenantId);
    }
}
