using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.WorkloadLifecycleServiceTests;

/// <summary>
///     Pins the AB#4918 wake-gate state machine of <see cref="WorkloadLifecycleService"/>:
///     the scale-to-zero tenant gate, the AlwaysOn no-op, the Running activity stamp, the
///     Hibernated → Waking → Running wake (released by
///     <c>NotifyWorkloadConfiguredAsync</c>), the wake-budget timeout revert and the
///     fail-fast on a failed scale-1 ack.
/// </summary>
internal class EnsureWorkloadRunningTests
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";
    private const string PoolRtId = "65d5c447b420da3fb12381bc";
    private const string PipelineRtId = "66004fda527ac79a03ecedd8";

    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IOperatorConnectionManager _connectionManager =
        Substitute.For<IOperatorConnectionManager>();
    private readonly ILifecycleConfigurationService _lifecycleConfiguration =
        Substitute.For<ILifecycleConfigurationService>();
    private readonly WorkloadLifecycleService _service;

    public EnsureWorkloadRunningTests()
    {
        // 1s wake budget keeps the timeout / fail-fast tests fast.
        _service = new WorkloadLifecycleService(
            Substitute.For<ILogger<WorkloadLifecycleService>>(),
            _repository, _eventService, _connectionManager, _lifecycleConfiguration,
            Microsoft.Extensions.Options.Options.Create(
                new CommunicationControllerOptions { LifecycleWakeBudgetSeconds = 1 }));
    }

    private void GivenScaleToZeroEnabled(bool enabled = true)
    {
        _lifecycleConfiguration.IsScaleToZeroEnabledAsync(TenantId).Returns(enabled);
    }

    private void GivenWorkloadIsInPool()
    {
        var pool = new RtPool
        {
            RtId = new OctoObjectId(PoolRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkPoolTypeId,
            Name = "cloud-pool",
            Environment = RtEnvironmentEnum.Cloud,
        };
        _repository.GetPoolForWorkloadAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(pool);
    }

    private static RtAdapter CreateAdapter(RtLifecycleModeEnum lifecycleMode, RtLifecycleStateEnum lifecycleState)
    {
        return new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "meshtest-adapter",
            LifecycleMode = lifecycleMode,
            LifecycleState = lifecycleState,
        };
    }

    private async Task WaitForActiveWakeAsync()
    {
        // The awaits before WaitForConfiguredAsync all complete synchronously against the
        // substitutes, so the waiter is registered by the time the Ensure call returns its
        // (incomplete) task — the poll is a safety net only.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_service.HasActiveWake(TenantId, WorkloadRtId))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Wake waiter was never registered.");
            }

            await Task.Delay(10);
        }
    }

    [Test]
    public async Task ScaleToZeroDisabled_ByRtId_DoesNotTouchRepository()
    {
        GivenScaleToZeroEnabled(false);

        await _service.EnsureWorkloadRunningAsync(TenantId, new OctoObjectId(WorkloadRtId));

        // The cheap cached gate must short-circuit before any repository lookup.
        await Assert.That(_repository.ReceivedCalls()).IsEmpty();
    }

    [Test]
    public async Task AlwaysOnWorkload_NoStateWritesAndNoScale()
    {
        GivenScaleToZeroEnabled();
        var adapter = CreateAdapter(RtLifecycleModeEnum.AlwaysOn, RtLifecycleStateEnum.Hibernated);

        await _service.EnsureWorkloadRunningAsync(TenantId, adapter);

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLastActivityAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<DateTime>());
        await _connectionManager.DidNotReceiveWithAnyArgs().NotifyWorkloadScaleAsync(
            Arg.Any<ScaleWorkloadDto>());
    }

    [Test]
    public async Task OnDemandRunning_OnlyStampsActivity()
    {
        GivenScaleToZeroEnabled();
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Running);

        await _service.EnsureWorkloadRunningAsync(TenantId, adapter);

        await _repository.Received(1).SetWorkloadLastActivityAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId), Arg.Any<DateTime>());
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _connectionManager.DidNotReceiveWithAnyArgs().NotifyWorkloadScaleAsync(
            Arg.Any<ScaleWorkloadDto>());
    }

    [Test]
    public async Task OnDemandHibernated_WakesAndCompletesWhenConfigured()
    {
        GivenScaleToZeroEnabled();
        GivenWorkloadIsInPool();
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Hibernated);
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(adapter);

        // Act - start the gate, then complete the wake from a "parallel" configuration ack.
        var ensureTask = _service.EnsureWorkloadRunningAsync(TenantId, adapter);
        await WaitForActiveWakeAsync();
        await _service.NotifyWorkloadConfiguredAsync(TenantId, new OctoObjectId(WorkloadRtId));
        await ensureTask;

        using var _ = Assert.Multiple();
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Waking, Arg.Any<string?>());
        await _repository.Received().SetWorkloadLastActivityAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId), Arg.Any<DateTime>());
        await _eventService.Received(1).StoreInformationEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await _connectionManager.Received(1).NotifyWorkloadScaleAsync(Arg.Is<ScaleWorkloadDto>(dto =>
            dto.TenantId == TenantId
            && dto.WorkloadRtId == WorkloadRtId
            && dto.PoolRtId == PoolRtId
            && dto.Replicas == 1));
    }

    [Test]
    public async Task OnDemandHibernated_NoConfiguredAck_TimesOutAndRevertsToHibernated()
    {
        GivenScaleToZeroEnabled();
        GivenWorkloadIsInPool();
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Hibernated);

        var exception = await Assert.ThrowsAsync<WorkloadLifecycleServiceException>(
            () => _service.EnsureWorkloadRunningAsync(TenantId, adapter));

        using var _ = Assert.Multiple();
        await Assert.That(exception!.Message).Contains("did not reach the Configured state");
        // Budget exhausted: Waking is reverted so the next demand signal starts a fresh wake.
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Hibernated, Arg.Any<string?>());
        await _eventService.Received(1).StoreErrorEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await Assert.That(_service.HasActiveWake(TenantId, WorkloadRtId)).IsFalse();
    }

    [Test]
    public async Task OnDemandHibernated_FailedScaleAck_FailsWaiterFastWithScaleError()
    {
        GivenScaleToZeroEnabled();
        GivenWorkloadIsInPool();
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Hibernated);
        // OnScaleStatusReportedAsync loads the workload itself; by then the repository state
        // is Waking (written when the wake started).
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Waking));

        var ensureTask = _service.EnsureWorkloadRunningAsync(TenantId, adapter);
        await WaitForActiveWakeAsync();

        await _service.OnScaleStatusReportedAsync(new WorkloadScaleStatusDto
        {
            TenantId = TenantId,
            WorkloadRtId = WorkloadRtId,
            WorkloadName = "meshtest-adapter",
            Replicas = 1,
            Success = false,
            StatusMessage = "no Deployments found",
        });

        var exception = await Assert.ThrowsAsync<WorkloadLifecycleServiceException>(() => ensureTask);

        // The fail-fast path throws the scale error, not the budget timeout - proving the
        // waiter did not burn the wake budget.
        await Assert.That(exception!.Message).Contains("could not scale it up");
        await Assert.That(exception.Message).Contains("no Deployments found");
    }

    [Test]
    public async Task NotifyWorkloadConfigured_OnDemandWaking_TransitionsToRunningAndStampsActivity()
    {
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Waking);
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(adapter);

        await _service.NotifyWorkloadConfiguredAsync(TenantId, new OctoObjectId(WorkloadRtId));

        using var _ = Assert.Multiple();
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Running, Arg.Any<string?>());
        await _repository.Received(1).SetWorkloadLastActivityAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId), Arg.Any<DateTime>());
    }

    [Test]
    public async Task NotifyWorkloadConfigured_AlwaysOn_NoWrites()
    {
        var adapter = CreateAdapter(RtLifecycleModeEnum.AlwaysOn, RtLifecycleStateEnum.Running);
        _repository.GetWorkloadByRtIdAsync(TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId))
            .Returns(adapter);

        await _service.NotifyWorkloadConfiguredAsync(TenantId, new OctoObjectId(WorkloadRtId));

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLastActivityAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task NotifyWorkloadConfigured_RepositoryThrows_IsSwallowed()
    {
        // Best-effort bookkeeping on the configuration-ack path - never break the ack.
        _repository.GetWorkloadByRtIdAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>())
            .ThrowsAsync(new InvalidOperationException("mongo down"));

        await _service.NotifyWorkloadConfiguredAsync(TenantId, new OctoObjectId(WorkloadRtId));
    }

    [Test]
    public async Task ForPipeline_TenantGateOff_DoesNotResolveAdapter()
    {
        GivenScaleToZeroEnabled(false);

        await _service.EnsureWorkloadRunningForPipelineAsync(TenantId, new OctoObjectId(PipelineRtId));

        await _repository.DidNotReceiveWithAnyArgs().GetAdapterByPipelineAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>());
    }

    [Test]
    public async Task ForPipeline_AdapterNotFound_NoThrowAndNoWrites()
    {
        GivenScaleToZeroEnabled();
        // Repository returns null (default) - the caller's own path produces its
        // established "pipeline has no adapter" error.
        await _service.EnsureWorkloadRunningForPipelineAsync(TenantId, new OctoObjectId(PipelineRtId));

        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLastActivityAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<DateTime>());
    }

    [Test]
    public async Task ForPipeline_OnDemandRunningAdapter_StampsActivity()
    {
        GivenScaleToZeroEnabled();
        var adapter = CreateAdapter(RtLifecycleModeEnum.OnDemand, RtLifecycleStateEnum.Running);
        _repository.GetAdapterByPipelineAsync(TenantId,
                Arg.Is<RtEntityId>(id => id.RtId.ToString() == PipelineRtId))
            .Returns(adapter);

        await _service.EnsureWorkloadRunningForPipelineAsync(TenantId, new OctoObjectId(PipelineRtId));

        await _repository.Received(1).SetWorkloadLastActivityAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId), Arg.Any<DateTime>());
    }
}
