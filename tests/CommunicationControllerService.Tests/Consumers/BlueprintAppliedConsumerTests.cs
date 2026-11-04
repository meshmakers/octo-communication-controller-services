using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Consumers;

/// <summary>
/// AB#5111 — reconcile-after-blueprint-apply: the asset repository's <c>BlueprintApplied</c>
/// broadcast is the only observable path that bulk-creates Adapters /
/// ServiceAccountConfigurations between two tenant loads, so it triggers the idempotent
/// tenant-wide reconcile sweep.
/// </summary>
internal class BlueprintAppliedConsumerTests
{
    private const string TenantId = "acme";

    private readonly IPipelineServiceAccountProvisioningService _provisioning =
        Substitute.For<IPipelineServiceAccountProvisioningService>();

    private readonly IAdapterService _adapterService = Substitute.For<IAdapterService>();

    private BlueprintAppliedConsumer CreateSut()
    {
        return new BlueprintAppliedConsumer(NullLogger<BlueprintAppliedConsumer>.Instance, _provisioning,
            _adapterService);
    }

    private static IDistributedContext<BlueprintUpdated> UpdatedContext(int added, int updated, int deleted = 0)
    {
        var context = Substitute.For<IDistributedContext<BlueprintUpdated>>();
        context.Message.Returns(new BlueprintUpdated(TenantId, "Acme.App-2.0.0", "Acme.App-1.0.0", "Merge",
            added, updated, deleted, Guid.NewGuid(), DateTime.UtcNow));
        return context;
    }

    private static IDistributedContext<BlueprintApplied> Context(int added, int updated, int deleted = 0)
    {
        var context = Substitute.For<IDistributedContext<BlueprintApplied>>();
        context.Message.Returns(new BlueprintApplied(TenantId, "Acme.App-1.0.0", "Initial",
            added, updated, deleted, Guid.NewGuid(), DateTime.UtcNow));
        return context;
    }

    [Test]
    public async Task BlueprintThatWroteEntities_TriggersTheTenantSweep()
    {
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(1, 0, 2, []));

        await CreateSut().ConsumeAsync(Context(added: 3, updated: 0));

        await _provisioning.Received(1).EnsureTenantProvisionedAsync(TenantId);
    }

    [Test]
    public async Task BlueprintThatOnlyDeleted_DoesNotSweep()
    {
        // A delete cannot have created an adapter or a service account — skipping the sweep keeps the bus
        // consumer cheap for uninstalls (the pipeline refresh still runs, AB#6631).
        await CreateSut().ConsumeAsync(Context(added: 0, updated: 0, deleted: 5));

        await _provisioning.DidNotReceiveWithAnyArgs().EnsureTenantProvisionedAsync(default!);
    }

    [Test]
    public async Task SweepFailure_IsSwallowed()
    {
        // A broadcast consumer must never fault the bus over one tenant; the next tenant load
        // retries anyway.
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("tenant unloading"));

        await Assert.That(async () => await CreateSut().ConsumeAsync(Context(added: 1, updated: 0)))
            .ThrowsNothing();
    }

    // AB#6631 - an update rewrites pipeline entities in place; the adapters that run them must be told.

    [Test]
    public async Task BlueprintUpdate_PushesTheChangedPipelinesToTheAdapters()
    {
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 2, []));
        _adapterService.RefreshDeployedPipelinesAsync(TenantId).Returns(1);

        await CreateSut().ConsumeAsync(UpdatedContext(added: 0, updated: 4));

        await _adapterService.Received(1).RefreshDeployedPipelinesAsync(TenantId);
    }

    [Test]
    public async Task BlueprintApply_AlsoRefreshesTheDeployedPipelines()
    {
        // A force re-apply replaces the entities in place as well.
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 2, []));

        await CreateSut().ConsumeAsync(Context(added: 0, updated: 3));

        await _adapterService.Received(1).RefreshDeployedPipelinesAsync(TenantId);
    }

    [Test]
    public async Task BlueprintUpdateThatOnlyDeleted_RefreshesSoRemovedRoutesStopAnswering()
    {
        // A removed pipeline must be dropped from the adapter; the service account sweep is not needed.
        await CreateSut().ConsumeAsync(UpdatedContext(added: 0, updated: 0, deleted: 2));

        await _adapterService.Received(1).RefreshDeployedPipelinesAsync(TenantId);
        await _provisioning.DidNotReceiveWithAnyArgs().EnsureTenantProvisionedAsync(default!);
    }

    [Test]
    public async Task BlueprintApplyThatOnlyDeleted_Refreshes()
    {
        await CreateSut().ConsumeAsync(Context(added: 0, updated: 0, deleted: 1));

        await _adapterService.Received(1).RefreshDeployedPipelinesAsync(TenantId);
    }

    [Test]
    public async Task BlueprintThatTouchedNothing_SkipsEverything()
    {
        await CreateSut().ConsumeAsync(UpdatedContext(added: 0, updated: 0, deleted: 0));
        await CreateSut().ConsumeAsync(Context(added: 0, updated: 0, deleted: 0));

        await _adapterService.DidNotReceiveWithAnyArgs().RefreshDeployedPipelinesAsync(default!);
        await _provisioning.DidNotReceiveWithAnyArgs().EnsureTenantProvisionedAsync(default!);
    }

    [Test]
    public async Task FailedServiceAccountSweep_DoesNotKeepTheRouteChangeFromReachingTheAdapter()
    {
        // The authorization of a route is the more urgent of the two.
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("identity down"));

        await CreateSut().ConsumeAsync(UpdatedContext(added: 0, updated: 1));

        await _adapterService.Received(1).RefreshDeployedPipelinesAsync(TenantId);
    }

    [Test]
    public async Task FailedRefresh_IsSwallowed()
    {
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 0, []));
        _adapterService.RefreshDeployedPipelinesAsync(TenantId)
            .ThrowsAsync(new InvalidOperationException("adapter hub down"));

        await Assert.That(async () => await CreateSut().ConsumeAsync(UpdatedContext(added: 0, updated: 1)))
            .ThrowsNothing();
    }
}
