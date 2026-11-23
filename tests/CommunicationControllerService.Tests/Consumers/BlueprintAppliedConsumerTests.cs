using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Blueprints;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Microsoft.Extensions.Hosting;

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

    private readonly IBlueprintCapabilityReconciler _capabilities = Substitute.For<IBlueprintCapabilityReconciler>();

    private BlueprintAppliedConsumer CreateSut()
    {
        return new BlueprintAppliedConsumer(NullLogger<BlueprintAppliedConsumer>.Instance, _provisioning,
            _adapterService, _capabilities, Substitute.For<IHostApplicationLifetime>());
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

    // ---- AB#9578: Communication enabled by the platform --------------------------------------------

    [Test]
    public async Task Applied_AsksTheReconcilerForCommunication_WithTheReportedDependencies_BeforeTheSweep()
    {
        var context = Substitute.For<IDistributedContext<BlueprintApplied>>();
        context.Message.Returns(new BlueprintApplied(TenantId, "Acme.App-1.0.0", "Initial", 3, 0, 0, Guid.NewGuid(),
            DateTime.UtcNow) { CkModelDependencies = ["System.Ai", "System.Communication"] });
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 0, []));

        await CreateSut().ConsumeAsync(context);

        Received.InOrder(() =>
        {
            _capabilities.ReconcileAsync(
                Arg.Is<BlueprintCapabilityRequest>(r => r.TenantId == TenantId && r.BlueprintId == "Acme.App-1.0.0" &&
                                                        r.Trigger == nameof(BlueprintApplied) &&
                                                        r.CkModelDependencies!.SequenceEqual(new[]
                                                            { "System.Ai", "System.Communication" })),
                BlueprintCapability.Communication, Arg.Any<CancellationToken>());
            _provisioning.EnsureTenantProvisionedAsync(TenantId);
        });
    }

    [Test]
    public async Task Updated_AsksTheReconcilerForCommunication()
    {
        var context = UpdatedContext(added: 0, updated: 1);

        await CreateSut().ConsumeAsync(context);

        await _capabilities.Received(1).ReconcileAsync(
            Arg.Is<BlueprintCapabilityRequest>(r => r.TenantId == TenantId && r.Trigger == nameof(BlueprintUpdated)),
            BlueprintCapability.Communication, Arg.Any<CancellationToken>());
        await _capabilities.DidNotReceive().ReconcileAsync(Arg.Any<BlueprintCapabilityRequest>(),
            BlueprintCapability.Ai, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task EventThatChangedNoEntity_StillReconcilesTheCapability()
    {
        // A re-apply of a blueprint that still needs Communication enables it again after a manual disable.
        await CreateSut().ConsumeAsync(Context(added: 0, updated: 0));

        await _capabilities.Received(1).ReconcileAsync(Arg.Any<BlueprintCapabilityRequest>(),
            BlueprintCapability.Communication, Arg.Any<CancellationToken>());
        await _provisioning.DidNotReceiveWithAnyArgs().EnsureTenantProvisionedAsync(default!);
    }

    [Test]
    public async Task ReconcilerFailure_NeitherFaultsTheBusNorSkipsTheSweep()
    {
        _capabilities.ReconcileAsync(Arg.Any<BlueprintCapabilityRequest>(), Arg.Any<BlueprintCapability>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("unexpected"));
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 0, []));

        await Assert.That(async () => await CreateSut().ConsumeAsync(Context(added: 1, updated: 0)))
            .ThrowsNothing();
        await _provisioning.Received(1).EnsureTenantProvisionedAsync(TenantId);
    }

    [Test]
    public async Task HostShutdown_CancelsTheCapabilityWaitAndStillRunsTheSweep()
    {
        using var stopping = new CancellationTokenSource();
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(stopping.Token);
        _capabilities.ReconcileAsync(Arg.Any<BlueprintCapabilityRequest>(), Arg.Any<BlueprintCapability>(),
                stopping.Token)
            .Returns(async call =>
            {
                await stopping.CancelAsync();
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return BlueprintCapabilityOutcome.Enabled;
            });
        _provisioning.EnsureTenantProvisionedAsync(TenantId)
            .Returns(new PipelineServiceAccountProvisioningReport(0, 0, 0, []));
        var logger = new FakeLogger<BlueprintAppliedConsumer>();
        var sut = new BlueprintAppliedConsumer(logger, _provisioning, _adapterService, _capabilities, lifetime);

        await Assert.That(async () => await sut.ConsumeAsync(Context(added: 1, updated: 0))).ThrowsNothing();

        await _capabilities.Received(1).ReconcileAsync(Arg.Any<BlueprintCapabilityRequest>(),
            BlueprintCapability.Communication, stopping.Token);
        await _provisioning.Received(1).EnsureTenantProvisionedAsync(TenantId);
        var records = logger.Collector.GetSnapshot();
        await Assert.That(records.Count(r => r.Level == LogLevel.Warning && r.Message.Contains("EnableCommunication")))
            .IsEqualTo(1);
        await Assert.That(records.Any(r => r.Level == LogLevel.Error)).IsFalse();
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

    // ---------------------------------------------------------------- AB#8982 through the real provisioning service

    private static BlueprintAppliedConsumer CreateSutWithRealProvisioning(UsesLinkedServiceAccountFixture fixture)
    {
        return new BlueprintAppliedConsumer(NullLogger<BlueprintAppliedConsumer>.Instance,
            fixture.CreateProvisioningService(), Substitute.For<IAdapterService>(),
            Substitute.For<IBlueprintCapabilityReconciler>(), Substitute.For<IHostApplicationLifetime>());
    }

    private static IDistributedContext<T> ContextFor<T>(T message) where T : class
    {
        var context = Substitute.For<IDistributedContext<T>>();
        context.Message.Returns(message);
        return context;
    }

    [Test]
    public async Task BlueprintInstall_CreatesTheClientsOfUsesLinkedAccounts()
    {
        var fixture = new UsesLinkedServiceAccountFixture();
        var aiPrompt = fixture.AddStandalone("accounting-ai-prompt", ["AccountingRead"]);
        var reader = fixture.AddStandalone("accounting-pipeline-reader", ["AccountingRead"]);

        await CreateSutWithRealProvisioning(fixture).ConsumeAsync(ContextFor(new BlueprintApplied(
            UsesLinkedServiceAccountFixture.TenantId, "MeshmakersAccounting-3.29.0", "Initial", 12, 0, 0,
            Guid.NewGuid(), DateTime.UtcNow)));

        var sentClientIds = fixture.SentClients().Select(c => c.ClientId).ToList();
        using var _ = Assert.Multiple();
        await Assert.That(sentClientIds).Contains(PipelineServiceAccountProvisioningService.BuildClientId(aiPrompt.RtId));
        await Assert.That(sentClientIds).Contains(PipelineServiceAccountProvisioningService.BuildClientId(reader.RtId));
        await Assert.That(fixture.UpdatedStandalone().Count).IsEqualTo(2);
    }

    [Test]
    public async Task BlueprintUpdate_MaterialisesChangedDeclarationsOfUsesLinkedAccounts()
    {
        var fixture = new UsesLinkedServiceAccountFixture();
        var reader = fixture.AddStandalone("accounting-pipeline-reader", ["AccountingRead", "AccountingWrite"],
            clientId: "octo-pipeline-sa-reader");
        reader.ClientSecret = SecretTestValues.Stored("reader-secret");

        await CreateSutWithRealProvisioning(fixture).ConsumeAsync(ContextFor(new BlueprintUpdated(
            UsesLinkedServiceAccountFixture.TenantId, "MeshmakersAccounting-3.30.0", "MeshmakersAccounting-3.29.0",
            "Merge", 0, 3, 0, Guid.NewGuid(), DateTime.UtcNow)));

        var sent = fixture.SentClients().Single(c => c.ClientId == "octo-pipeline-sa-reader");
        await Assert.That(sent.AssignedRoleNames)
            .IsEquivalentTo(new[] { "AccountingRead", "AccountingWrite", CommonConstants.StreamDataReaderRole });
    }
}
