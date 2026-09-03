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

    private BlueprintAppliedConsumer CreateSut()
    {
        return new BlueprintAppliedConsumer(NullLogger<BlueprintAppliedConsumer>.Instance, _provisioning);
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
        // A delete cannot have created an adapter or a service account — skipping keeps the bus
        // consumer cheap for uninstalls.
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
}
