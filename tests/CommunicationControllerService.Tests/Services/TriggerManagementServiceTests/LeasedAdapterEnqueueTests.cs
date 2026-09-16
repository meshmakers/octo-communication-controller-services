using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.TriggerManagementServiceTests;

/// <summary>
///     AB#4924 §9.1 — a <c>Leased</c> adapter has no process of its own, so its work is queued rather
///     than sent to the execute queue. A manual adapter is untouched: it has no queue and executes
///     immediately, and that asymmetry is intended (concept §5).
/// </summary>
/// <remarks>
///     🔴 <b><c>[NotInParallel]</c> is load-bearing, not tidiness.</b> Every test in this file opens a
///     <see cref="System.Diagnostics.Metrics.MeterListener"/> over process-wide instruments, and a
///     listener being started or disposed on one thread mutates the very subscription lists another
///     thread's <c>Add</c> is walking. The symptom is a measurement that is simply never delivered —
///     one refusal short of sixteen, once in a few dozen runs. A metrics test that loses a
///     measurement at random is worse than no test: it fails for a reason that has nothing to do with
///     the metric. Every class in this repository that opens a listener shares this constraint key.
/// </remarks>
[NotInParallel(nameof(MeterListener))]
internal class LeasedAdapterEnqueueTests : TriggerManagementServiceTestsBase
{
    /// <summary>
    ///     A pool rtId unique to this test instance. The increment 9 leasing metrics are process-wide
    ///     statics tagged by it, and the suite runs concurrently.
    /// </summary>
    private readonly string _poolRtId = OctoObjectId.GenerateNewId().ToString();

    private const string LenderTenantId = "lender";

    private RtAdapter ArrangeAdapter(RtLifecycleModeEnum lifecycleMode, OctoObjectId pipelineRtId)
    {
        var adapter = RtEntityCreator.CreateAdapter();
        adapter.Name = "an-adapter";
        adapter.LifecycleMode = lifecycleMode;
        adapter.LentFromTenantId = LenderTenantId;
        adapter.LentFromPoolRtId = _poolRtId;

        CommunicationRepository
            .GetAdapterByPipelineAsync(TenantId,
                Arg.Is<RtEntityId>(id => id.RtId == pipelineRtId))
            .Returns(adapter);
        return adapter;
    }

    [Test]
    public async Task ALeasedAdapter_QueuesTheWorkItemInsteadOfSendingTheExecuteCommand()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        var adapter = ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        var result = await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
            pipelineInput: "{\"x\":1}");

        using var _ = Assert.Multiple();
        await Assert.That(result).IsNotNull();
        await Assert.That(result.Id).IsNotEqualTo(Guid.Empty);

        await CommunicationRepository.Received(1).EnqueueExecutionAsync(TenantId,
            Arg.Is<RtPipelineExecution>(e =>
                e.ExecutionId == result.Id.ToString() && e.InputData == "{\"x\":1}"),
            Arg.Is<RtEntityId>(id => id.RtId == pipelineRtId),
            Arg.Is<RtEntityId>(id => id.RtId == adapter.RtId),
            Arg.Any<DateTime>());

        // 🔴 Nothing was sent. A Leased adapter has no process listening on the execute queue, so a
        // send here would be a message nobody consumes — the silent-drop failure the AB#4918 wake
        // gate exists to prevent, reintroduced by a different route.
        await ExecuteMeshPipelineCommandClient.DidNotReceive()
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    /// <summary>
    ///     🔴 AB#4924 §9.6 — the half that decides whether the <c>Interactive</c> execution class means
    ///     anything. Work has just arrived and a member of the pool may be idle ALREADY, so without
    ///     pulling the round forward a Studio Execute waits up to a full
    ///     <c>LeaseSchedulerIntervalSeconds</c> before anything starts while nobody is busy. The
    ///     release-side wake cannot cover this case: nothing was released.
    /// </summary>
    [Test]
    public async Task ALeasedAdapter_PullsTheNextSchedulingRoundForward()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        WakeSignal.Received(1).RequestRound(Arg.Any<string>());
    }

    /// <summary>
    ///     A manual adapter executes immediately and has no queue, so there is no round to pull
    ///     forward — and waking the scheduler for it would read every borrower's queue in the estate
    ///     on every ordinary pipeline execution.
    /// </summary>
    [Test]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    public async Task AManualAdapter_PullsNoRoundForward(RtLifecycleModeEnum lifecycleMode)
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(lifecycleMode, pipelineRtId);

        // The manual path really sends, so the command client has to answer — without it the send
        // throws and the test would pass for the wrong reason (no wake because nothing got that far).
        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        WakeSignal.DidNotReceive().RequestRound(Arg.Any<string>());
    }

    [Test]
    public async Task ALeasedAdapter_DoesNotRunTheWakeGate()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        // There is no workload to wake — the whole point of Leased is that the borrower owns no
        // process. Calling the wake gate would look for one and find nothing to do, at best.
        await WorkloadLifecycleService.DidNotReceive()
            .EnsureWorkloadRunningForPipelineAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    [Arguments(RtLifecycleModeEnum.AlwaysOn)]
    [Arguments(RtLifecycleModeEnum.OnDemand)]
    public async Task AManualAdapter_StillExecutesImmediately(RtLifecycleModeEnum lifecycleMode)
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(lifecycleMode, pipelineRtId);

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null);

        using var _ = Assert.Multiple();
        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
        await ExecuteMeshPipelineCommandClient.Received(1)
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    /// <summary>
    ///     A dry run is a synchronous answer to a caller holding the request open. Parking it behind
    ///     a rotation would turn "validate this pipeline" into something that returns minutes later,
    ///     so it falls through to the normal path and fails there instead of pretending to have run.
    /// </summary>
    [Test]
    public async Task ADryRunOnALeasedAdapter_IsNotQueued()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: null,
            isDryRun: true);

        await CommunicationRepository.DidNotReceive().EnqueueExecutionAsync(Arg.Any<string>(),
            Arg.Any<RtPipelineExecution>(), Arg.Any<RtEntityId>(), Arg.Any<RtEntityId>(), Arg.Any<DateTime>());
    }

    /// <summary>
    ///     AB#4924 §14 — the per-tenant leasing kill switch, <b>enqueue</b> half. With leasing off the
    ///     work is refused with a named reason and <b>nothing is written</b>: no execution entity, no
    ///     <c>QueuedAt</c>, no event that looks like progress.
    /// </summary>
    /// <remarks>
    ///     🔴 It throws rather than falling through to the manual-adapter path. That path publishes to
    ///     a per-pipeline queue nothing is listening on and reports failure only after the 30 s
    ///     MassTransit request timeout, with a message about an adapter — which is not what happened.
    /// </remarks>
    [Test]
    public async Task WithLeasingDisabled_NothingIsQueuedAndTheCallerIsToldWhy()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);
        LifecycleConfigurationService.IsLeasingEnabledAsync(TenantId).Returns(false);

        var exception = await Assert.ThrowsAsync<TriggerManagementServiceException>(async () =>
            await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
                pipelineInput: "{\"x\":1}"));

        using var _ = Assert.Multiple();
        await Assert.That(exception!.Message).Contains("leasing is disabled");
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .EnqueueExecutionAsync(default!, default!, default!, default!, default);
        // Not smuggled onto the manual path either — a Leased adapter has no process to send to.
        await ExecuteMeshPipelineCommandClient.DidNotReceive()
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>());
    }

    /// <summary>
    ///     The switch is about <b>leasing</b>, not about executing. A manual adapter in a tenant with
    ///     leasing off keeps running exactly as before — it never had a queue and never needed one.
    /// </summary>
    [Test]
    public async Task WithLeasingDisabled_AManualAdapterIsUnaffected()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.AlwaysOn, pipelineRtId);
        LifecycleConfigurationService.IsLeasingEnabledAsync(TenantId).Returns(false);

        ExecuteMeshPipelineCommandClient
            .GetResponse<ExecutePipelineResponse>(Arg.Any<string>(), Arg.Any<ExecutePipelineRequest>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ExecutePipelineResponse(true, null, Guid.NewGuid(), DateTime.UtcNow));

        var result = await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
            pipelineInput: "{\"x\":1}");

        using var _ = Assert.Multiple();
        await Assert.That(result).IsNotNull();
        await CommunicationRepository.DidNotReceiveWithAnyArgs()
            .EnqueueExecutionAsync(default!, default!, default!, default!, default);
    }

    /// <summary>
    ///     AB#4924 increment 9 (plan §11) — the queue's in-rate. Read against
    ///     <c>octo.lease.granted.count</c> (the out-rate) this is the only honest answer to "is the
    ///     queue growing or draining"; the depth gauge shows the level but not which way it moves.
    /// </summary>
    [Test]
    public async Task AQueuedWorkItem_IsCountedAgainstThePoolItWillBeLeasedFrom()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);

        var recorded = await CollectAsync(() =>
            TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId, pipelineInput: "{\"x\":1}"));

        var enqueued = recorded.Single(r => r.Instrument == "octo.lease.enqueued.count");

        using var _ = Assert.Multiple();
        await Assert.That(enqueued.Value).IsEqualTo(1);
        await Assert.That(enqueued.Tags["octo.tenant.id"]).IsEqualTo(TenantId);
        await Assert.That(enqueued.Tags["octo.pool.tenant_id"]).IsEqualTo(LenderTenantId);
    }

    /// <summary>
    ///     AB#4924 increment 9. The enqueue half of the kill switch is counted at its own stage and
    ///     named as the BORROWER's half: the lender's is checked at grant, and during a staged
    ///     rollout an operator has to be able to tell which of the two they are looking at.
    /// </summary>
    [Test]
    public async Task WithLeasingDisabled_TheRefusalIsCountedAtTheEnqueueStage()
    {
        var pipelineRtId = OctoObjectId.GenerateNewId();
        ArrangeAdapter(RtLifecycleModeEnum.Leased, pipelineRtId);
        LifecycleConfigurationService.IsLeasingEnabledAsync(TenantId).Returns(false);

        var recorded = await CollectAsync(async () =>
        {
            try
            {
                await TriggerManagementService.StartExecutePipelineAsync(TenantId, pipelineRtId,
                    pipelineInput: null);
            }
            catch (TriggerManagementServiceException)
            {
                // The refusal is the subject; the throw is asserted by the test above.
            }
        });

        var refused = recorded.Single(r => r.Instrument == "octo.lease.refused.count");

        using var _ = Assert.Multiple();
        await Assert.That(refused.Tags["octo.lease.stage"]).IsEqualTo("enqueue");
        await Assert.That(refused.Tags["octo.lease.refusal_reason"]).IsEqualTo("leasing_disabled_borrower");
        await Assert.That(recorded.Any(r => r.Instrument == "octo.lease.enqueued.count")).IsFalse();
    }

    private sealed record Recorded(string Instrument, double Value, Dictionary<string, string> Tags);

    private async Task<List<Recorded>> CollectAsync(Func<Task> act)
    {
        var recorded = new List<Recorded>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AdapterLeasingMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            recorded.Add(new Recorded(instrument.Name, value, ToDictionary(tags))));
        // 🔴 The instruments have to EXIST before the listener starts. They are static fields of
        // AdapterLeasingMetrics, so the first test in the process to touch that class is the one
        // that creates them — and if that happens inside the act below, it happens while this
        // listener is already running and racing its own subscription. Forcing the class
        // constructor here makes every run look like the second one.
        RuntimeHelpers.RunClassConstructor(typeof(AdapterLeasingMetrics).TypeHandle);

        listener.Start();

        await act();

        return recorded.Where(r => r.Tags.GetValueOrDefault("octo.pool.rt_id") == _poolRtId).ToList();
    }

    private static Dictionary<string, string> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var map = new Dictionary<string, string>();
        foreach (var tag in tags)
        {
            map[tag.Key] = tag.Value?.ToString() ?? string.Empty;
        }

        return map;
    }
}
