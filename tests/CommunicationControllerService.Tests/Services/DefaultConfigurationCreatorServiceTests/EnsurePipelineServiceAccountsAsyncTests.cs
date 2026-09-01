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

    private TestableCreator CreateSut()
    {
        return new TestableCreator(_provisioningService, _eventService);
    }

    private sealed class TestableCreator(
        IPipelineServiceAccountProvisioningService provisioningService,
        ICommunicationEventService eventService) : DefaultConfigurationCreatorService(
        NullLogger<DefaultConfigurationCreatorService>.Instance,
        Substitute.For<IDiagnosticsService>(),
        Options.Create(new CommunicationControllerOptions()),
        Substitute.For<ITriggerManagementService>(),
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
        Substitute.For<ISystemContext>(),
        Substitute.For<IPoolService>(),
        Substitute.For<IAdapterCachePublish>(),
        Substitute.For<IAdapterService>(),
        provisioningService,
        new FailedTenantRegistry(),
        eventService,
        Substitute.For<IBlueprintService>(),
        Array.Empty<IBlueprintEmbeddedSource>());
}
