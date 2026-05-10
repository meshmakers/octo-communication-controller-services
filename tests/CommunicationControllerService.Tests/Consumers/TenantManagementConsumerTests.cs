using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Consumers;

[SuppressMessage("Substitute creation", "NS2002:Constructor parameters count mismatch.")]
internal class TenantManagementConsumerTests
{
    private const string TenantId = "tenantId";

    // Constants.StartTime is `static readonly DateTime StartTime = DateTime.UtcNow` and is
    // initialised on first access — which happens inside the consumer when it compares against
    // `context.Message.Timestamp`. Messages created with `DateTime.UtcNow` in the test arrange
    // step would therefore be timestamped *before* StartTime is set and silently dropped as
    // "old" messages. Use a far-future timestamp so the comparison reliably accepts the message.
    private static readonly DateTime FutureTimestamp = DateTime.UtcNow.AddYears(1);

    private readonly TenantManagementConsumer _consumer;
    private readonly IPoolService _poolService;
    private readonly IAdapterService _adapterService;
    private readonly IConfigurationService _configurationService;

    public TenantManagementConsumerTests()
    {
        _poolService = Substitute.For<IPoolService>();
        _adapterService = Substitute.For<IAdapterService>();
        _configurationService = Substitute.For<IConfigurationService>();
        var logger = Substitute.For<ILogger<TenantManagementConsumer>>();
        var eventService = Substitute.For<ICommunicationEventService>();

        _configurationService.IsEnabledAsync(TenantId).Returns(true);

        _consumer = new TenantManagementConsumer(logger, _poolService, _adapterService,
            _configurationService, eventService);
    }

    [Test]
    public async Task PreThenPos_RunsBothPreAndPosUpdates()
    {
        // Arrange — simulate the normal in-order delivery: PreUpdateTenant first, then PosUpdateTenant.
        // Both halves of the pair must execute, in order: Pre then Pos.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _poolService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
        await _poolService.Received(1).PosUpdateTenantAsync(TenantId);
    }

    [Test]
    public async Task PosThenPre_RunsBothPreAndPosUpdates()
    {
        // Arrange — out-of-order delivery: PosUpdateTenant arrives first, then PreUpdateTenant.
        // The consumer must still execute Pre then Pos when the pair completes.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(posMessage));
        await _consumer.ConsumeAsync(BuildContext(preMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _poolService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
        await _poolService.Received(1).PosUpdateTenantAsync(TenantId);
    }

    [Test]
    public async Task PreOnly_DoesNotRunUpdates()
    {
        // Arrange — only Pre arrives; the consumer must wait for Pos before executing anything.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task PosOnly_DoesNotRunUpdates()
    {
        // Arrange — only Pos arrives; the consumer must wait for Pre before executing anything.
        var correlationId = Guid.NewGuid();
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task PreThenPos_TenantDisabled_DoesNotRunUpdates()
    {
        // Arrange — tenant is disabled; ExecutePre/PosTenantUpdate must be no-ops.
        _configurationService.IsEnabledAsync(TenantId).Returns(false);

        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task DifferentCorrelationIds_DoNotPair()
    {
        // Arrange — Pre and Pos with different correlation ids must not be paired together.
        var preMessage = new PreUpdateTenant(TenantId, Guid.NewGuid(), FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, Guid.NewGuid(), FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task PreThenPos_RunsPreBeforePos()
    {
        // Arrange — verify Pre runs strictly before Pos, since Pre clears caches and notifies
        // adapters via SignalR while Pos reinitialises the tenant cache.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        var callOrder = new List<string>();
        _adapterService.PreUpdateTenantAsync(TenantId)
            .Returns(_ =>
            {
                callOrder.Add("AdapterService.Pre");
                return Task.CompletedTask;
            });
        _poolService.PreUpdateTenantAsync(TenantId)
            .Returns(_ =>
            {
                callOrder.Add("PoolService.Pre");
                return Task.CompletedTask;
            });
        _adapterService.PosUpdateTenantAsync(TenantId)
            .Returns(_ =>
            {
                callOrder.Add("AdapterService.Pos");
                return Task.CompletedTask;
            });
        _poolService.PosUpdateTenantAsync(TenantId)
            .Returns(_ =>
            {
                callOrder.Add("PoolService.Pos");
                return Task.CompletedTask;
            });

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        await Assert.That(callOrder).IsEquivalentTo(new[]
        {
            "AdapterService.Pre", "PoolService.Pre", "AdapterService.Pos", "PoolService.Pos"
        });
    }

    [Test]
    public async Task OldMessage_BeforeStartTime_IsIgnored()
    {
        // Arrange — a message older than service start time must be discarded without
        // recording the correlation id, otherwise a later partner could falsely pair with it.
        var correlationId = Guid.NewGuid();
        var oldPre = new PreUpdateTenant(TenantId, correlationId, DateTime.MinValue);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(oldPre));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert — the old Pre is ignored, so the Pos arrives "alone" and waits for a partner.
        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    private static IDistributedContext<TMessage> BuildContext<TMessage>(TMessage message)
        where TMessage : class
    {
        var context = Substitute.For<IDistributedContext<TMessage>>();
        context.Message.Returns(message);
        return context;
    }
}
