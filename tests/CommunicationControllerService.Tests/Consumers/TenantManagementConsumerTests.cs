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

    // AB#5866: the controller start is an injected, explicitly captured value. Messages of the
    // pairing tests are stamped one second after it; FutureTimestamp keeps its name for the
    // existing tests.
    private static readonly DateTime StartedAt = new(2026, 10, 7, 19, 28, 58, DateTimeKind.Utc);
    private static readonly DateTime FutureTimestamp = StartedAt.AddSeconds(1);
    private static readonly ControllerStartTime StartTime = new(StartedAt);

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
            _configurationService, eventService, StartTime);
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
    public async Task PreThenPos_NotifiesCkModelChanged()
    {
        // Arrange — a completed Pre/Pos pair signals a finished tenant update (CK model import,
        // cache clear); connected adapters must be told to invalidate their CK caches (AB#4456).
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
    }

    [Test]
    public async Task PreThenPos_TenantDisabled_StillNotifiesCkModelChanged()
    {
        // Arrange — the CK cache flush must fire even when the enabled-gated restart relay
        // does not (AB#4456): a connected adapter holds a stale CK cache regardless of the
        // tenant's communication-enabled flag.
        _configurationService.IsEnabledAsync(TenantId).Returns(false);

        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task PreThenPos_CkModelChangedFails_StillRunsUpdates()
    {
        // Arrange — a failing CK cache-flush notification must not block the restart relay.
        _adapterService.CkModelChangedAsync(TenantId)
            .Returns<Task>(_ => throw new InvalidOperationException("hub send failed"));

        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
    }

    [Test]
    public async Task PreOnly_DoesNotNotifyCkModelChanged()
    {
        // Arrange — an unpaired Pre means the tenant update is still in progress; flushing the
        // adapters' CK caches now would let them re-load a half-imported model.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));

        // Assert
        await _adapterService.DidNotReceive().CkModelChangedAsync(Arg.Any<string>());
    }

    [Test]
    public async Task PreAndPosOnDifferentConsumerInstances_StillPair()
    {
        // Arrange — AB#4456 root cause: the consumer is registered SCOPED, so MassTransit
        // delivers Pre and Pos to two different instances. The pair state must survive across
        // instances (static), otherwise the paired branch silently never runs in production.
        var secondConsumer = new TenantManagementConsumer(
            Substitute.For<ILogger<TenantManagementConsumer>>(), _poolService, _adapterService,
            _configurationService, Substitute.For<ICommunicationEventService>(), StartTime);

        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act — Pre on the first instance, Pos on a fresh second instance.
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await secondConsumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
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

    [Test]
    public async Task FirstPairAfterStart_IsProcessed_EvenWhenTheConsumerIsCreatedMuchLater()
    {
        // AB#5866 regression: the start time used to be a lazily initialised static that the first
        // consumed message set — so that very message was "older than the start" and dropped. Here
        // the controller started at StartedAt, the tenant update was published 4 min 23 s later
        // (test-2-dev: start 19:28:58, Pre 19:33:21) and the consumer instance is only created now.
        // The first Pre/Pos pair after the start must run the full relay.
        var consumer = new TenantManagementConsumer(Substitute.For<ILogger<TenantManagementConsumer>>(),
            _poolService, _adapterService, _configurationService,
            Substitute.For<ICommunicationEventService>(), StartTime);
        var publishedAt = StartedAt.AddMinutes(4).AddSeconds(23);
        var correlationId = Guid.NewGuid();

        await consumer.ConsumeAsync(BuildContext(new PreUpdateTenant(TenantId, correlationId, publishedAt)));
        await consumer.ConsumeAsync(BuildContext(new PosUpdateTenant(TenantId, correlationId, publishedAt)));

        using var _ = Assert.Multiple();
        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
    }

    [Test]
    public async Task PairPublishedJustBeforeStart_IsIgnored_AndLeavesNoUnpairedHalf()
    {
        // The filter itself stays: a pair published before this process started is dropped — both
        // halves, so neither lingers in the static pairing dictionary.
        var publishedAt = StartedAt.AddMilliseconds(-1);
        var correlationId = Guid.NewGuid();

        await _consumer.ConsumeAsync(BuildContext(new PreUpdateTenant(TenantId, correlationId, publishedAt)));
        await _consumer.ConsumeAsync(BuildContext(new PosUpdateTenant(TenantId, correlationId, publishedAt)));
        // A fresh message with the same correlation id must not pair with a dropped half.
        await _consumer.ConsumeAsync(BuildContext(new PosUpdateTenant(TenantId, correlationId, FutureTimestamp)));

        using var _ = Assert.Multiple();
        await _adapterService.DidNotReceive().CkModelChangedAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task ControllerStartTime_IsBeforeStart_ComparesAgainstTheCapturedInstant()
    {
        using var _ = Assert.Multiple();
        await Assert.That(StartTime.IsBeforeStart(StartedAt.AddTicks(-1))).IsTrue();
        await Assert.That(StartTime.IsBeforeStart(StartedAt)).IsFalse();
        await Assert.That(StartTime.IsBeforeStart(StartedAt.AddTicks(1))).IsFalse();
    }

    [Test]
    public async Task CacheOnlyPair_NotifiesCkModelChangedButSkipsRestartRelay()
    {
        // Arrange — a CacheOnly pair (AB#4895, e.g. the nightly autocomplete aggregation) must
        // flush the adapters' CK caches but never relay the full adapter restart: the fleet-wide
        // midnight restart was the trigger window for AB#4876.
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp,
            TenantUpdateScope.CacheOnly);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp,
            TenantUpdateScope.CacheOnly);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
        await _adapterService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PreUpdateTenantAsync(Arg.Any<string>());
        await _adapterService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
        await _poolService.DidNotReceive().PosUpdateTenantAsync(Arg.Any<string>());
    }

    [Test]
    public async Task MixedScopePair_FullWins_RunsRestartRelay()
    {
        // Arrange — defensive: a pair whose halves disagree must be treated as Full, otherwise a
        // real tenant update could lose its restart relay (AB#4895).
        var correlationId = Guid.NewGuid();
        var preMessage = new PreUpdateTenant(TenantId, correlationId, FutureTimestamp,
            TenantUpdateScope.CacheOnly);
        var posMessage = new PosUpdateTenant(TenantId, correlationId, FutureTimestamp);

        // Act
        await _consumer.ConsumeAsync(BuildContext(preMessage));
        await _consumer.ConsumeAsync(BuildContext(posMessage));

        // Assert
        using var _ = Assert.Multiple();

        await _adapterService.Received(1).CkModelChangedAsync(TenantId);
        await _adapterService.Received(1).PreUpdateTenantAsync(TenantId);
        await _adapterService.Received(1).PosUpdateTenantAsync(TenantId);
    }

    private static IDistributedContext<TMessage> BuildContext<TMessage>(TMessage message)
        where TMessage : class
    {
        var context = Substitute.For<IDistributedContext<TMessage>>();
        context.Message.Returns(message);
        return context;
    }
}
