using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Common.DistributionEventHub.Consumers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Consumers;

/// <summary>
///     AB#5278: a cron tick for a pipeline on a Leased adapter arrives here and becomes a queued
///     work item through the one path that creates leases — <c>StartExecutePipelineAsync</c>. The
///     consumer adds exactly two things to that path: it coalesces a tick whose previous run is
///     still waiting for a lease, and it never dead-letters the queue.
/// </summary>
internal class LeaseTriggerConsumerTests
{
    private const string TenantId = "tenantId";

    private readonly ICommunicationRepository _repository = Substitute.For<ICommunicationRepository>();
    private readonly ITriggerManagementService _triggerManagementService = Substitute.For<ITriggerManagementService>();
    private readonly LeaseTriggerConsumer _consumer;
    private readonly RtAdapter _leasedAdapter;
    private readonly OctoObjectId _pipelineRtId = OctoObjectId.GenerateNewId();
    private readonly OctoObjectId _triggerRtId = OctoObjectId.GenerateNewId();

    public LeaseTriggerConsumerTests()
    {
        _leasedAdapter = RtEntityCreator.CreateAdapter();
        _leasedAdapter.LifecycleMode = RtLifecycleModeEnum.Leased;
        _repository.GetAdapterByPipelineAsync(TenantId, Arg.Is<RtEntityId>(id => id.RtId == _pipelineRtId))
            .Returns(_leasedAdapter);
        _repository.GetQueuedExecutionsForAdapterAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<int>())
            .Returns([]);

        _consumer = new LeaseTriggerConsumer(Substitute.For<ILogger<LeaseTriggerConsumer>>(), _repository,
            _triggerManagementService);
    }

    private IDistributedContext<LeaseTriggerMessage> Tick()
    {
        var context = Substitute.For<IDistributedContext<LeaseTriggerMessage>>();
        context.Message.Returns(new LeaseTriggerMessage(TenantId, _pipelineRtId.ToString(), _triggerRtId.ToString()));
        return context;
    }

    [Test]
    public async Task ATick_BecomesAQueuedWorkItemThroughTheExecutePath()
    {
        await _consumer.ConsumeAsync(Tick());

        // No input and no caller: the tick carries what the adapter-side cron path carries — nothing.
        await _triggerManagementService.Received(1)
            .StartExecutePipelineAsync(TenantId, _pipelineRtId, null, false, null, null);
    }

    [Test]
    public async Task ATick_WhileTheLastRunIsStillQueued_IsCoalesced()
    {
        _repository.GetQueuedExecutionsForAdapterAsync(TenantId,
                Arg.Is<RtEntityId>(id => id.RtId == _leasedAdapter.RtId), Arg.Any<int>())
            .Returns([
                new QueuedExecution(Guid.NewGuid().ToString(), OctoObjectId.GenerateNewId(), DateTime.UtcNow,
                    _pipelineRtId, "nightly", QueuedExecution.BatchClass)
            ]);

        await _consumer.ConsumeAsync(Tick());

        // 🔴 A cron that fires faster than the pool serves it must not grow the queue by one item
        // per tick; one waiting run of the pipeline is the whole cron's intent.
        await _triggerManagementService.DidNotReceiveWithAnyArgs()
            .StartExecutePipelineAsync(default!, default, default, default, default, default);
    }

    [Test]
    public async Task ATick_WithAnotherPipelineQueued_IsNotCoalesced()
    {
        _repository.GetQueuedExecutionsForAdapterAsync(TenantId, Arg.Any<RtEntityId>(), Arg.Any<int>())
            .Returns([
                new QueuedExecution(Guid.NewGuid().ToString(), OctoObjectId.GenerateNewId(), DateTime.UtcNow,
                    OctoObjectId.GenerateNewId(), "other", QueuedExecution.BatchClass)
            ]);

        await _consumer.ConsumeAsync(Tick());

        await _triggerManagementService.Received(1)
            .StartExecutePipelineAsync(TenantId, _pipelineRtId, null, false, null, null);
    }

    [Test]
    public async Task ATick_ThatOutlivedASwitchToADedicatedMode_StillRunsThePipeline()
    {
        // The schedule was registered while the adapter was Leased; it has since been given a
        // process of its own. The execute path handles the current mode — the tick is not lost.
        _leasedAdapter.LifecycleMode = RtLifecycleModeEnum.OnDemand;

        await _consumer.ConsumeAsync(Tick());

        using var _ = Assert.Multiple();
        await _triggerManagementService.Received(1)
            .StartExecutePipelineAsync(TenantId, _pipelineRtId, null, false, null, null);
        await _repository.DidNotReceiveWithAnyArgs()
            .GetQueuedExecutionsForAdapterAsync(default!, default!, default);
    }

    [Test]
    public async Task AFailedTick_IsLoggedAndDoesNotThrow()
    {
        _triggerManagementService
            .StartExecutePipelineAsync(TenantId, _pipelineRtId, null, false, null, null)
            .Returns<Task<Meshmakers.Octo.Communication.Contracts.DataTransferObjects.PipelineExecutionDataDto>>(
                _ => throw new InvalidOperationException("pool leasing is disabled for this tenant"));

        // A refusal has been logged and stored by the execute path already; the next tick is the
        // retry a cron implies. Throwing here would dead-letter a durable queue.
        await _consumer.ConsumeAsync(Tick());
    }
}
