using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.DefaultConfigurationCreatorServiceTests;

/// <summary>
/// AB#7249 - staging-1, 2026-10-21: during a MongoDB primary election the tenant setup of 'sbeg' and
/// 'eegroider' failed, the log promised a background retry, but this service wired no retry store, so
/// nothing was ever retried and their adapters stayed offline until a pod restart. The shared
/// <see cref="TenantSetupRetryTracker"/> is the retry that needs no MongoDB; these tests pin that the
/// communication controller's creator hands it to the shared base and that a failed setup is retried
/// with backoff (the backoff, health and metrics themselves are covered in octo-common-services).
/// </summary>
internal class TenantSetupRetryWiringTests
{
    private const string TenantId = "sbeg";

    private readonly ISystemContext _systemContext = Substitute.For<ISystemContext>();
    private readonly TenantSetupRetryTracker _tracker =
        new(options: new TenantSetupRetryOptions { InitialDelay = TimeSpan.FromTicks(1), MaxDelay = TimeSpan.FromTicks(1) });

    [Test]
    public async Task FailedSetup_IsQueuedInTheInjectedTracker_AndRetriedWithoutARetryStore()
    {
        _systemContext.IsSystemTenantExistingAsync().Returns(true);
        var lookups = 0;
        _systemContext.FindTenantContextAsync(TenantId)
            .Returns(_ =>
            {
                lookups++;
                return Task.FromException<ITenantContext>(
                    new TimeoutException("A timeout occurred after 29999ms selecting a server. Mode: Primary"));
            });
        var sut = CreateSut();

        await Assert.ThrowsAsync<TimeoutException>(() => sut.SetupAsync(TenantId));

        await Assert.That(_tracker.Snapshot().Select(e => e.TenantId)).IsEquivalentTo([TenantId]);

        // the background service's next tick (a fresh scope, hence a fresh creator) picks it up again
        await Task.Delay(20);
        await CreateSut().RetryFailedTenantsAsync();

        await Assert.That(lookups).IsEqualTo(2);
        await Assert.That(_tracker.Snapshot().Single().Attempts).IsEqualTo(2);
    }

    private DefaultConfigurationCreatorService CreateSut() => new(
        NullLogger<DefaultConfigurationCreatorService>.Instance,
        Substitute.For<IDiagnosticsService>(),
        Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()),
        Substitute.For<ITriggerManagementService>(),
        Substitute.For<INotificationScheduleService>(),
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
        _systemContext,
        Substitute.For<IDeploymentSiteService>(),
        Substitute.For<IAdapterCachePublish>(),
        Substitute.For<IAdapterService>(),
        Substitute.For<IPipelineServiceAccountProvisioningService>(),
        Substitute.For<IAdapterPoolMirrorProvisioningService>(),
        new FailedTenantRegistry(),
        _tracker,
        Substitute.For<ICommunicationEventService>(),
        Substitute.For<IBlueprintService>(),
        Array.Empty<IBlueprintEmbeddedSource>());
}
