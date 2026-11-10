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
///     The tenant-load hook of the SecretDefault target reconcile: it runs on every load and never fails it.
/// </summary>
internal sealed class EnsureSecretDefaultsAsyncTests
{
    private const string TenantId = "grandchild-new";

    private readonly ISecretDefaultProvisioningService _secretDefaultProvisioningService =
        Substitute.For<ISecretDefaultProvisioningService>();

    private readonly ICommunicationEventService _eventService = Substitute.For<ICommunicationEventService>();

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();

    [Test]
    public async Task ATenantLoad_ReconcilesTheTenantsSecretDefaults()
    {
        ArrangeEnabledTenant();
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(SecretDefaultTargetResult.Unchanged(TenantId, SecretDefaultTargetStatus.Reconciled));

        await CreateSut().StartAsync(TenantId);

        await _secretDefaultProvisioningService.Received(1)
            .ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AThrowingService_NeverFailsTheTenantLoad()
    {
        ArrangeEnabledTenant();
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tenant database unreachable"));

        await CreateSut().StartAsync(TenantId);

        await _eventService.Received(1).StoreErrorEventAsync(TenantId,
            Arg.Is<string>(message => message.Contains("Secret defaults")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AFailingEventStore_NeverFailsTheTenantLoadEither()
    {
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("tenant database unreachable"));
        _eventService.StoreErrorEventAsync(TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>())
            .ThrowsAsync(new InvalidOperationException("event store unreachable"));

        await CreateSut().EnsureSecretDefaultsAsync(TenantId);
    }

    [Test]
    public async Task ATenantWhereSomethingChanged_GetsAnInformationEventWithCountsOnly()
    {
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new SecretDefaultTargetResult(TenantId, SecretDefaultTargetStatus.Reconciled,
                DefaultsCreated: 2, DefaultsUpdated: 0, DefaultsRemoved: 1, DefaultsKept: 0, SourcesUnreadable: 0,
                WritesFailed: 0));

        await CreateSut().EnsureSecretDefaultsAsync(TenantId);

        await _eventService.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(message => message.Contains("2 created") && message.Contains("1 removed")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ATenantWhoseDefaultsWereAllUnchanged_StaysSilent()
    {
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new SecretDefaultTargetResult(TenantId, SecretDefaultTargetStatus.Reconciled,
                DefaultsCreated: 0, DefaultsUpdated: 0, DefaultsRemoved: 0, DefaultsKept: 0, SourcesUnreadable: 0,
                WritesFailed: 0, DefaultsUnchanged: 3));

        await CreateSut().EnsureSecretDefaultsAsync(TenantId);

        await _eventService.DidNotReceiveWithAnyArgs()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ATenantWhoseDefaultWasRestored_GetsAnInformationEvent()
    {
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(new SecretDefaultTargetResult(TenantId, SecretDefaultTargetStatus.Reconciled,
                DefaultsCreated: 0, DefaultsUpdated: 1, DefaultsRemoved: 0, DefaultsKept: 0, SourcesUnreadable: 0,
                WritesFailed: 0));

        await CreateSut().EnsureSecretDefaultsAsync(TenantId);

        await _eventService.Received(1).StoreInformationEventAsync(TenantId,
            Arg.Is<string>(message => message.Contains("1 updated")), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task ATenantWithoutDefaults_StaysSilent()
    {
        _secretDefaultProvisioningService.ProvisionForTargetAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(SecretDefaultTargetResult.Unchanged(TenantId, SecretDefaultTargetStatus.Reconciled));

        await CreateSut().EnsureSecretDefaultsAsync(TenantId);

        await _eventService.DidNotReceiveWithAnyArgs()
            .StoreInformationEventAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    private void ArrangeEnabledTenant()
    {
        var tenantContext = Substitute.For<ITenantContext>();
        _systemContext.FindTenantContextAsync(TenantId).Returns(tenantContext);
        tenantContext.GetAdminSessionAsync().Returns(Substitute.For<IOctoAdminSession>());
        tenantContext.GetConfigurationAsync(Arg.Any<IOctoAdminSession>(), Arg.Any<string>(),
                Arg.Any<DefaultConfigurationEnabled>())
            .Returns(new DefaultConfigurationEnabled { IsEnabled = true });
    }

    private TestableCreator CreateSut()
    {
        return new TestableCreator(_secretDefaultProvisioningService, _eventService, _systemContext);
    }

    private sealed class TestableCreator(
        ISecretDefaultProvisioningService secretDefaultProvisioningService,
        ICommunicationEventService eventService,
        ISystemContext systemContext) : DefaultConfigurationCreatorService(
        NullLogger<DefaultConfigurationCreatorService>.Instance,
        Substitute.For<IDiagnosticsService>(),
        Options.Create(new CommunicationControllerOptions()),
        Substitute.For<ITriggerManagementService>(),
        Substitute.For<INotificationScheduleService>(),
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
        systemContext,
        Substitute.For<IDeploymentSiteService>(),
        Substitute.For<IAdapterCachePublish>(),
        Substitute.For<IAdapterService>(),
        Substitute.For<IPipelineServiceAccountProvisioningService>(),
        Substitute.For<IAdapterPoolMirrorProvisioningService>(),
        secretDefaultProvisioningService,
        new FailedTenantRegistry(),
        new TenantSetupRetryTracker(),
        eventService,
        Substitute.For<IBlueprintService>(),
        Array.Empty<IBlueprintEmbeddedSource>())
    {
        public Task StartAsync(string tenantId) => StartTenantAsync(tenantId);
    }
}
