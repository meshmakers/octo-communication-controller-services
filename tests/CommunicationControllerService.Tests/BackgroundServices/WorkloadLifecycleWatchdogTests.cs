using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.BackgroundServices;

/// <summary>
///     Pins the AB#4918 idle-watchdog sweep (<see cref="WorkloadLifecycleWatchdogBackgroundService"/>):
///     idle OnDemand adapters are drained (Running → Draining + scale-0), busy guards
///     (running executions, recent activity, recent pipeline execution, in-flight wake)
///     leave them untouched, and stale <c>Waking</c> states left behind by a controller
///     restart are reconciled (Configured → Running, stuck → Hibernated).
/// </summary>
internal class WorkloadLifecycleWatchdogTests
{
    private const string TenantId = "meshtest";
    private const string WorkloadRtId = "66004fda527ac79a03ecedd7";
    private const string PoolWorkloadRtId = "66004fda527ac79a03ecedd8";

    private readonly ICommunicationRepository _repository =
        Substitute.For<ICommunicationRepository>();
    private readonly ILifecycleConfigurationService _lifecycleConfiguration =
        Substitute.For<ILifecycleConfigurationService>();
    private readonly IWorkloadLifecycleService _workloadLifecycleService =
        Substitute.For<IWorkloadLifecycleService>();
    private readonly ICommunicationEventService _eventService =
        Substitute.For<ICommunicationEventService>();
    private readonly IWorkloadOnDemandCapabilityService _onDemandCapabilityService =
        Substitute.For<IWorkloadOnDemandCapabilityService>();
    private readonly WorkloadLifecycleWatchdogBackgroundService _service;

    public WorkloadLifecycleWatchdogTests()
    {
        // Default: capable (AB#4984). The capability-guard test overrides this.
        _onDemandCapabilityService
            .EvaluateAsync(Arg.Any<string>(), Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(true, []));

        _service = new WorkloadLifecycleWatchdogBackgroundService(
            Substitute.For<IAdapterCache>(),
            _repository,
            _lifecycleConfiguration,
            _workloadLifecycleService,
            _eventService,
            _onDemandCapabilityService,
            Microsoft.Extensions.Options.Options.Create(new CommunicationControllerOptions()));
    }

    [After(Test)]
    public void DisposeService()
    {
        // BackgroundService owns a stopping CTS; the tests never start it, but it still
        // must be disposed.
        _service.Dispose();
    }

    private static RtAdapter CreateOnDemandAdapter(
        RtLifecycleStateEnum lifecycleState = RtLifecycleStateEnum.Running,
        RtDeploymentStateEnum deploymentState = RtDeploymentStateEnum.Deployed,
        DateTime? lastActivityAt = null,
        int idleTimeoutMinutes = 30,
        RtConfigurationStateEnum configurationState = RtConfigurationStateEnum.Unconfigured)
    {
        return new RtAdapter
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterTypeId,
            Name = "meshtest-adapter",
            LifecycleMode = RtLifecycleModeEnum.OnDemand,
            LifecycleState = lifecycleState,
            DeploymentState = deploymentState,
            LastActivityAt = lastActivityAt,
            IdleTimeoutMinutes = idleTimeoutMinutes,
            ConfigurationState = configurationState,
        };
    }

    private void GivenWorkloads(params RtDeployableWorkload[] workloads)
    {
        _repository.GetWorkloadsAsync(TenantId).Returns(workloads);
    }

    private void GivenNoRunningExecutionsAndNoPipelines()
    {
        _repository.GetRunningExecutionsForAdapterAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(Array.Empty<RtPipelineExecution>());
        _repository.GetPipelinesAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(Array.Empty<RtPipeline>());
    }

    private async Task ThenWorkloadIsUntouchedAsync()
    {
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _workloadLifecycleService.DidNotReceiveWithAnyArgs().RequestScaleAsync(
            Arg.Any<string>(), Arg.Any<RtDeployableWorkload>(), Arg.Any<int>());
        await _eventService.DidNotReceiveWithAnyArgs().StoreInformationEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
        await _eventService.DidNotReceiveWithAnyArgs().StoreErrorEventAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task AlwaysOnWorkload_IsIgnored()
    {
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        adapter.LifecycleMode = RtLifecycleModeEnum.AlwaysOn;
        GivenWorkloads(adapter);

        await _service.SweepTenantAsync(TenantId);

        await _repository.DidNotReceiveWithAnyArgs().GetRunningExecutionsForAdapterAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>());
        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task IdleOnDemandAdapter_IsDrainedAndScaledToZero()
    {
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        using var _ = Assert.Multiple();
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Draining, Arg.Any<string?>());
        await _eventService.Received(1).StoreInformationEventAsync(
            TenantId, Arg.Is<string>(m => m.Contains("idle")), Arg.Any<RtEntityId?>());
        await _workloadLifecycleService.Received(1).RequestScaleAsync(TenantId, adapter, 0);
    }

    [Test]
    public async Task IdleAdapterNotOnDemandCapable_IsNotDrained_StoresWarning()
    {
        // AB#4984 defense-in-depth: LifecycleMode can be set to OnDemand via GraphQL or a
        // blueprint without passing the DeploymentSiteService deploy gate. The watchdog must never
        // hibernate a workload whose pipelines use process-bound triggers.
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();
        _onDemandCapabilityService.EvaluateAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new OnDemandCapabilityResult(false,
                ["Pipeline 'sync' uses process-bound trigger 'FromPolling@1'"]));

        await _service.SweepTenantAsync(TenantId);

        using var _ = Assert.Multiple();
        await _repository.DidNotReceiveWithAnyArgs().SetWorkloadLifecycleStateAsync(
            Arg.Any<string>(), Arg.Any<OctoObjectId>(), Arg.Any<RtLifecycleStateEnum>(), Arg.Any<string?>());
        await _workloadLifecycleService.DidNotReceiveWithAnyArgs().RequestScaleAsync(
            Arg.Any<string>(), Arg.Any<RtDeployableWorkload>(), Arg.Any<int>());
        await _eventService.Received(1).StoreWarningEventAsync(
            TenantId, Arg.Is<string>(m => m.Contains("FromPolling@1") && m.Contains("NOT be")),
            Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task IdleAdapterWithRunningExecution_IsNotDrained()
    {
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();
        _repository.GetRunningExecutionsForAdapterAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new List<RtPipelineExecution> { RtEntityCreator.CreatePipelineExecution() });

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task RecentlyActiveAdapter_IsNotDrained()
    {
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddMinutes(-5));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task StaleActivityStampButRecentPipelineExecution_IsNotDrained()
    {
        // The statistics' LastExecutionAt survives the AB#4370 execution fold and must win
        // over a stale LastActivityAt stamp.
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        var pipeline = RtEntityCreator.CreatePipeline();
        _repository.GetPipelinesAsync(TenantId, Arg.Any<RtEntityId>())
            .Returns(new List<RtPipeline> { pipeline });
        var statistics = RtEntityCreator.CreatePipelineStatistics();
        statistics.LastExecutionAt = DateTime.UtcNow.AddMinutes(-5);
        _repository.GetPipelineStatisticsAsync(TenantId,
                Arg.Is<RtEntityId>(id => id.RtId == pipeline.RtId))
            .Returns(statistics);

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task NoObservedActivityAtAll_IsDrained()
    {
        // Idle-since-forever contract: no LastActivityAt and no pipeline statistics means
        // the adapter has been idle since before observation began.
        var adapter = CreateOnDemandAdapter(lastActivityAt: null);
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Draining, Arg.Any<string?>());
        await _workloadLifecycleService.Received(1).RequestScaleAsync(TenantId, adapter, 0);
    }

    [Test]
    public async Task NotDeployedAdapter_IsNotDrained()
    {
        var adapter = CreateOnDemandAdapter(
            deploymentState: RtDeploymentStateEnum.Undeployed,
            lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task WakingConfiguredWithoutActiveWaiter_IsReconciledToRunning()
    {
        // The wake completed but the Running transition was lost (controller restart between
        // the config ack and the state write).
        var adapter = CreateOnDemandAdapter(
            lifecycleState: RtLifecycleStateEnum.Waking,
            configurationState: RtConfigurationStateEnum.Configured);
        GivenWorkloads(adapter);

        await _service.SweepTenantAsync(TenantId);

        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Running, Arg.Any<string?>());
        await _workloadLifecycleService.DidNotReceiveWithAnyArgs().RequestScaleAsync(
            Arg.Any<string>(), Arg.Any<RtDeployableWorkload>(), Arg.Any<int>());
    }

    [Test]
    public async Task WakingUnconfiguredAndStale_IsRevertedToHibernatedWithErrorEvent()
    {
        // Stale beyond twice the wake budget (default 60s -> 120s) with no active waiter:
        // the wake was orphaned and is folded back to Hibernated.
        var adapter = CreateOnDemandAdapter(
            lifecycleState: RtLifecycleStateEnum.Waking,
            lastActivityAt: DateTime.UtcNow.AddMinutes(-10));
        GivenWorkloads(adapter);

        await _service.SweepTenantAsync(TenantId);

        using var _ = Assert.Multiple();
        await _repository.Received(1).SetWorkloadLifecycleStateAsync(
            TenantId, Arg.Is<OctoObjectId>(id => id.ToString() == WorkloadRtId),
            RtLifecycleStateEnum.Hibernated, Arg.Any<string?>());
        await _eventService.Received(1).StoreErrorEventAsync(
            TenantId, Arg.Any<string>(), Arg.Any<RtEntityId?>());
    }

    [Test]
    public async Task WakingWithActiveWaiter_IsLeftAlone()
    {
        // A gate on this pod owns the wake and its budget - nothing to reconcile.
        var adapter = CreateOnDemandAdapter(
            lifecycleState: RtLifecycleStateEnum.Waking,
            lastActivityAt: DateTime.UtcNow.AddMinutes(-10));
        GivenWorkloads(adapter);
        _workloadLifecycleService.HasActiveWake(TenantId, WorkloadRtId).Returns(true);

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task OnDemandApplication_IsSkipped()
    {
        // Applications have no pipeline activity signal yet - only Adapters are hibernated.
        var application = new RtApplication
        {
            RtId = new OctoObjectId(WorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkApplicationTypeId,
            Name = "meshtest-app",
            LifecycleMode = RtLifecycleModeEnum.OnDemand,
            LifecycleState = RtLifecycleStateEnum.Running,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            LastActivityAt = DateTime.UtcNow.AddHours(-2),
            IdleTimeoutMinutes = 30,
        };
        GivenWorkloads(application);

        await _service.SweepTenantAsync(TenantId);

        await _repository.DidNotReceiveWithAnyArgs().GetRunningExecutionsForAdapterAsync(
            Arg.Any<string>(), Arg.Any<RtEntityId>());
        await ThenWorkloadIsUntouchedAsync();
    }

    private static RtAdapterPool CreateAdapterPool(
        RtLifecycleModeEnum lifecycleMode = RtLifecycleModeEnum.OnDemand,
        int minReplicas = 1,
        int maxReplicas = 3,
        int idleTimeoutMinutes = 30)
    {
        return new RtAdapterPool
        {
            RtId = new OctoObjectId(PoolWorkloadRtId),
            CkTypeId = SystemCommunicationCkIds.RtCkAdapterPoolTypeId,
            Name = "meshtest-pool",
            LifecycleMode = lifecycleMode,
            LifecycleState = RtLifecycleStateEnum.Running,
            DeploymentState = RtDeploymentStateEnum.Deployed,
            // Idle since before observation began — the state that drains an ordinary adapter.
            LastActivityAt = null,
            IdleTimeoutMinutes = idleTimeoutMinutes,
            MinReplicas = minReplicas,
            MaxReplicas = maxReplicas,
        };
    }

    [Test]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    public async Task AdapterPool_IsNeverDrainedBelowMinReplicas(RtLifecycleModeEnum lifecycleMode)
    {
        // 🔴 AB#4924 §7.3. The sweep judges idleness from the workload's OWN pipelines'
        // LastExecutionAt. A pool has none — every pipeline it runs belongs to a borrower in
        // another tenant database — so a busy pool reads as idle forever and would be drained to
        // zero, taking every borrower's work with it and looking like a correctly working timeout.
        // No lifecycle write, no scale request, at any lifecycle mode: MinReplicas is never crossed
        // because the watchdog never touches a pool at all.
        var pool = CreateAdapterPool(lifecycleMode, minReplicas: 2);
        GivenWorkloads(pool);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task AdapterPoolWithMinReplicasZero_IsStillNotDrainedByTheWatchdog()
    {
        // Even where a scale-to-0 would not cross the declared floor, the decision is not the
        // watchdog's to make: it has no signal about a pool's load, so "0 is allowed here" would be
        // a guess dressed as a policy. The pool's own lifecycle owns this (concept §4a).
        var pool = CreateAdapterPool(minReplicas: 0);
        GivenWorkloads(pool);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task AdapterPoolStuckInWaking_IsNotRevertedByTheWatchdog()
    {
        // 🔴 The stale-wake reconcile runs BEFORE the `is not RtAdapter` guard that keeps
        // Applications out of the idle judgement, so without the explicit pool exclusion this path
        // reaches a pool and writes to it: LifecycleState → Hibernated plus an error event about a
        // wake that never existed. A pool has no wake — it is woken by nothing and drained by
        // nothing the watchdog knows about — so every byte of that is fiction, and the state it
        // leaves behind is a pool the lifecycle believes is down.
        var pool = CreateAdapterPool();
        pool.LifecycleState = RtLifecycleStateEnum.Waking;
        pool.LastActivityAt = null;
        GivenWorkloads(pool);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await ThenWorkloadIsUntouchedAsync();
    }

    [Test]
    public async Task AdapterPoolBesideAnIdleAdapter_DoesNotStopTheAdapterFromBeingDrained()
    {
        // The exclusion is per workload, not per sweep: a tenant that owns a pool must still get
        // its ordinary idle adapters hibernated.
        var pool = CreateAdapterPool();
        var adapter = CreateOnDemandAdapter(lastActivityAt: DateTime.UtcNow.AddHours(-2));
        GivenWorkloads(pool, adapter);
        GivenNoRunningExecutionsAndNoPipelines();

        await _service.SweepTenantAsync(TenantId);

        await _workloadLifecycleService.Received(1).RequestScaleAsync(TenantId, adapter, 0);
        await _workloadLifecycleService.DidNotReceive().RequestScaleAsync(
            TenantId, pool, Arg.Any<int>());
    }
}
