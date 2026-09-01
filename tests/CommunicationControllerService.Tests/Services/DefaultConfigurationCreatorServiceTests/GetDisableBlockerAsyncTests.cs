using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts.BlueprintCatalogs;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
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
/// The Communication half of the AB#4255 disable guard: the creator answers the base class's
/// pre-disable hook from the persisted deployment state and produces the operator-facing
/// refusal that CLI, MCP and Studio show verbatim.
/// </summary>
internal class GetDisableBlockerAsyncTests
{
    private const string TenantId = "child-a";

    private readonly IPoolService _poolService = Substitute.For<IPoolService>();

    [Test]
    public async Task AnswersNull_WhenNothingIsDeployed()
    {
        _poolService.GetActiveDeploymentsAsync(TenantId).Returns(Array.Empty<ActiveDeployment>());

        var blocker = await CreateSut().ProbeDisableBlockerAsync(TenantId);

        await Assert.That(blocker).IsNull();
    }

    [Test]
    public async Task NamesEveryActiveResource_WithKindAndState()
    {
        _poolService.GetActiveDeploymentsAsync(TenantId).Returns(new List<ActiveDeployment>
        {
            new(ActiveDeployment.PoolKind, "edge-a", RtDeploymentStateEnum.Deployed),
            new(ActiveDeployment.AdapterKind, "mesh-adapter", RtDeploymentStateEnum.Pending),
            new(ActiveDeployment.ApplicationKind, "grafana", RtDeploymentStateEnum.Error),
        });

        var blocker = await CreateSut().ProbeDisableBlockerAsync(TenantId);

        await Assert.That(blocker).IsNotNull();
        await Assert.That(blocker!).Contains("Pool 'edge-a' (Deployed)");
        await Assert.That(blocker!).Contains("Adapter 'mesh-adapter' (Pending)");
        await Assert.That(blocker!).Contains("Application 'grafana' (Error)");
        await Assert.That(blocker!).Contains("UndeployWorkload");
        await Assert.That(blocker!).Contains("UndeployPool");
    }

    [Test]
    public async Task PropagatesReadFailures_InsteadOfAllowingTheDisable()
    {
        _poolService.GetActiveDeploymentsAsync(TenantId).ThrowsAsync(new InvalidOperationException("mongo down"));

        await Assert.That(async () => await CreateSut().ProbeDisableBlockerAsync(TenantId))
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task BuildDisableBlockedMessage_IsTheOperatorContract()
    {
        var message = DefaultConfigurationCreatorService.BuildDisableBlockedMessage("child-a",
        [
            new ActiveDeployment(ActiveDeployment.PoolKind, "edge-a", RtDeploymentStateEnum.Deployed),
            new ActiveDeployment(ActiveDeployment.AdapterKind, "mesh-adapter", RtDeploymentStateEnum.Pending),
        ]);

        await Assert.That(message).IsEqualTo(
            "Communication cannot be disabled for tenant 'child-a' while the following resources are still deployed: " +
            "Pool 'edge-a' (Deployed), Adapter 'mesh-adapter' (Pending). Undeploy them first - workloads with UndeployWorkload, " +
            "pools with UndeployPool (octo-cli in a context of tenant 'child-a', or Refinery Studio > Communication > " +
            "Adapters / Applications / Pools) - then retry DisableCommunication.");
        await Assert.That(message.All(c => c < 128)).IsTrue();
    }

    private TestableCreator CreateSut()
    {
        return new TestableCreator(_poolService);
    }

    private sealed class TestableCreator(IPoolService poolService) : DefaultConfigurationCreatorService(
        NullLogger<DefaultConfigurationCreatorService>.Instance,
        Substitute.For<IDiagnosticsService>(),
        Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()),
        Substitute.For<ITriggerManagementService>(),
        Substitute.For<ICommandClient<CreateIdentityDataCommandRequest>>(),
        Substitute.For<ISystemContext>(),
        poolService,
        Substitute.For<IAdapterCachePublish>(),
        Substitute.For<IAdapterService>(),
        Substitute.For<IPipelineServiceAccountProvisioningService>(),
        new FailedTenantRegistry(),
        Substitute.For<ICommunicationEventService>(),
        Substitute.For<IBlueprintService>(),
        Array.Empty<IBlueprintEmbeddedSource>())
    {
        public Task<string?> ProbeDisableBlockerAsync(string tenantId) => GetDisableBlockerAsync(tenantId);
    }
}
